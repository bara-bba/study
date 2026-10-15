using System.Text.RegularExpressions;
using Study.Core.Models;
using Study.Core.Monitoring;
using Study.Core.Plc;
using Study.Core.Triggers;

namespace Study.Tests;

/// <summary>
/// Hard guarantee that study never writes to a PLC: fails if any libplctag write API
/// (Write / WriteAsync / Set* buffer setters / AutoSyncWriteInterval) appears in the source.
/// </summary>
public class ReadOnlyGuardTests
{
    private static readonly Regex Forbidden = new(
        @"\.(Write|WriteAsync)\s*\(|\.Set(Bit|Int8|UInt8|Int16|UInt16|Int32|UInt32|Int64|UInt64|Float32|Float64|String|Buffer)\s*\(|AutoSyncWriteInterval",
        RegexOptions.Compiled);

    private static string SrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "study.core")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src");
    }

    [Fact]
    public void NoPlcWriteCallsAnywhereInSource()
    {
        var offenders = Directory.EnumerateFiles(SrcRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("libplctag"))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, i, line)))
            .Where(x => Forbidden.IsMatch(x.line))
            .Select(x => $"{x.f}:{x.i + 1}: {x.line.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0, "PLC write call found:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void ReaderInterfaceExposesNoWrite()
    {
        var names = typeof(ITagReader).GetMethods().Select(m => m.Name);
        Assert.DoesNotContain(names, n => n.Contains("Write", StringComparison.OrdinalIgnoreCase));
    }
}

public class WatchRunnerTests
{
    private sealed class FakeReader(Queue<double> values) : ITagReader
    {
        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<double> ReadAsync(CancellationToken ct) =>
            values.Count > 0 ? Task.FromResult(values.Dequeue()) : throw new PlcException("no more data");

        public void Dispose()
        {
        }
    }

    private sealed class FakeFactory(Queue<double> values) : ITagReaderFactory
    {
        public ITagReader Create(PlcEntry plc, TagAddress address, TimeSpan timeout) => new FakeReader(values);
    }

    private sealed class FakePinger(bool ok) : IPinger
    {
        public int Calls;

        public Task<bool> PingAsync(string ip, TimeSpan timeout, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(ok);
        }
    }

    private static readonly PlcEntry Plc = new() { Name = "P", Ip = "10.0.0.1" };

    [Fact]
    public async Task FiresOnRisingEdge_ThenReportsReadError()
    {
        var watch = new WatchDefinition { PlcIp = Plc.Ip, Tag = "T", PollMs = 100 };
        var runner = new WatchRunner(watch, Plc, new FakeFactory(new Queue<double>([0, 1])), new FakePinger(true),
            skipPing: false, retryDelay: TimeSpan.FromMilliseconds(50));
        var updates = new List<WatchUpdate>();
        var gotError = new TaskCompletionSource();
        runner.Updated += u =>
        {
            lock (updates)
            {
                updates.Add(u);
            }

            if (u.Status == WatchStatus.Error)
            {
                gotError.TrySetResult();
            }
        };

        runner.Start();
        await gotError.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runner.StopAsync();

        lock (updates)
        {
            Assert.Contains(updates, u => u.Outcome == TriggerOutcome.Fired && u.Value == 1);
            Assert.Contains(updates, u => u.Status == WatchStatus.Error && u.Message == "no more data");
            Assert.Equal(WatchStatus.Stopped, updates[^1].Status);
        }
    }

    [Fact]
    public async Task UnreachableWhenPingFails_AndNeverConnects()
    {
        var watch = new WatchDefinition { PlcIp = Plc.Ip, Tag = "T" };
        var pinger = new FakePinger(false);
        var runner = new WatchRunner(watch, Plc, new FakeFactory(new Queue<double>()), pinger, false,
            TimeSpan.FromMilliseconds(20));
        var seen = new TaskCompletionSource<WatchUpdate>();
        runner.Updated += u =>
        {
            if (u.Status == WatchStatus.Unreachable)
            {
                seen.TrySetResult(u);
            }
        };

        runner.Start();
        var update = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runner.StopAsync();
        Assert.Contains("10.0.0.1", update.Message);
    }

    [Fact]
    public async Task SkipPing_DoesNotPing()
    {
        var watch = new WatchDefinition { PlcIp = Plc.Ip, Tag = "T", PollMs = 100 };
        var pinger = new FakePinger(false);
        var runner = new WatchRunner(watch, Plc, new FakeFactory(new Queue<double>([0])), pinger, true,
            TimeSpan.FromMilliseconds(20));
        var connected = new TaskCompletionSource();
        runner.Updated += u =>
        {
            if (u.Status == WatchStatus.Connected)
            {
                connected.TrySetResult();
            }
        };

        runner.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runner.StopAsync();
        Assert.Equal(0, pinger.Calls);
    }
}
