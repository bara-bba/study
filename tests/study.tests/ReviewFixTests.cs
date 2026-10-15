using Study.Core.Expressions;
using Study.Core.Models;
using Study.Core.Monitoring;
using Study.Core.Plc;
using Study.Core.Triggers;
using Xunit;

namespace Study.Tests;

/// <summary>Cases found in the October review: edge + NOT/XOR, double NOT, bad formulas, reconnects.</summary>
public class ReviewFixTests
{
    // ------------------------------------------------------------- A only positive

    [Theory]
    [InlineData("A and B", true)]
    [InlineData("A or not B", true)]
    [InlineData("not not A", true)]
    [InlineData("B or C", true)]
    [InlineData("not A", false)]
    [InlineData("not A or B", false)]
    [InlineData("A xor B", false)]
    [InlineData("not (A and B)", false)]
    public void UsesLetterOnlyPositively(string formula, bool expected) =>
        Assert.Equal(expected, LogicFormula.Parse(formula, 3).UsesLetterOnlyPositively(0));

    [Fact]
    public void RowXor_IsNotPositiveInA() =>
        Assert.False(LogicFormula.Chain([LogicJoin.Xor]).UsesLetterOnlyPositively(0));

    [Fact]
    public void RowAndNot_IsPositiveInA() =>
        Assert.True(LogicFormula.Chain([LogicJoin.And], [true]).UsesLetterOnlyPositively(0));

    private static WatchDefinition EdgeWatch(string logic, LogicJoin join = LogicJoin.And) => new()
    {
        PlcIp = "192.168.1.10",
        Tag = "T",
        Mode = TriggerMode.RisingEdge,
        AndConditions = [new AndCondition { Tag = "B", Join = join }],
        Logic = logic,
    };

    [Fact]
    public void Validate_RejectsNotAOnEdge() =>
        Assert.Contains(EdgeWatch("not A or B").Validate(), e => e.StartsWith("With an edge"));

    [Fact]
    public void Validate_RejectsRowXorOnEdge() =>
        Assert.Contains(EdgeWatch("", LogicJoin.Xor).Validate(), e => e.StartsWith("With an edge"));

    [Fact]
    public void Validate_AllowsNotAOnLevel() =>
        Assert.Empty((EdgeWatch("not A or B") with { Mode = TriggerMode.WhileTrue }).Validate());

    [Fact]
    public void Validate_AllowsAndNotBOnEdge() =>
        Assert.Empty(EdgeWatch("A and not B").Validate());

    // ------------------------------------------------------------- engine combos

    [Fact]
    public void Edge_AndNotB_FiresOncePerEdge()
    {
        var logic = LogicFormula.Parse("A and not B", 2);
        var e = new TriggerEngine(TriggerMode.RisingEdge);
        bool B(bool b, bool a) => logic.Evaluate([a, b]);
        e.Update(0, a => B(false, a));
        Assert.Equal(TriggerOutcome.Fired, e.Update(1, a => B(false, a)));
        Assert.Equal(TriggerOutcome.None, e.Update(1, a => B(false, a)));
        Assert.Equal(TriggerOutcome.None, e.Update(0, a => B(false, a)));
        Assert.Equal(TriggerOutcome.None, e.Update(1, a => B(true, a)));   // edge while B holds: no alarm
        e.Update(0, a => B(false, a));
        Assert.Equal(TriggerOutcome.Fired, e.Update(1, a => B(false, a)));
    }

    [Fact]
    public void DoubleNot_EvaluatesLikeTheLetter()
    {
        var f = LogicFormula.Parse("not not A", 1);
        Assert.True(f.Evaluate([true]));
        Assert.False(f.Evaluate([false]));
        Assert.Equal("NOT NOT A", f.ToString());
    }

    // ------------------------------------------------------------------- runner

    /// <summary>Reads values in order; NaN means "this read fails", running out fails every read.</summary>
    private sealed class ScriptedReader(Queue<double> values) : ITagReader
    {
        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<double> ReadAsync(CancellationToken ct)
        {
            lock (values)
            {
                if (values.Count == 0 || double.IsNaN(values.Peek()))
                {
                    if (values.Count > 0)
                    {
                        values.Dequeue();
                    }

                    throw new PlcException("read failed");
                }

                return Task.FromResult(values.Dequeue());
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class ScriptedFactory(Queue<double> values) : ITagReaderFactory
    {
        public ITagReader Create(PlcEntry plc, TagAddress address, TimeSpan timeout) => new ScriptedReader(values);
    }

    private sealed class OkPinger : IPinger
    {
        public Task<bool> PingAsync(string ip, TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
    }

    private static readonly PlcEntry Plc = new() { Name = "P", Ip = "192.168.1.10" };

    /// <summary>Runs until <paramref name="errors"/> read errors were seen, returns every update.</summary>
    private static async Task<List<WatchUpdate>> RunUntilErrors(WatchDefinition watch, double[] script, int errors)
    {
        var runner = new WatchRunner(watch, Plc, new ScriptedFactory(new Queue<double>(script)), new OkPinger(),
            skipPing: true, retryDelay: TimeSpan.FromMilliseconds(10));
        var updates = new List<WatchUpdate>();
        var done = new TaskCompletionSource();
        runner.Updated += u =>
        {
            lock (updates)
            {
                updates.Add(u);
                if (updates.Count(x => x.Status == WatchStatus.Error) >= errors)
                {
                    done.TrySetResult();
                }
            }
        };

        runner.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runner.StopAsync();
        lock (updates)
        {
            return updates.ToList();
        }
    }

    [Fact]
    public async Task Level_ReconnectWhileStillTrue_DoesNotFireAgain()
    {
        var watch = new WatchDefinition { PlcIp = Plc.Ip, Tag = "T", Mode = TriggerMode.WhileTrue, PollMs = 100 };
        var updates = await RunUntilErrors(watch, [1, double.NaN, 1], errors: 2);
        Assert.Single(updates, u => u.Outcome == TriggerOutcome.Fired);
    }

    [Fact]
    public async Task Level_ReconnectAfterClearing_FiresAgain()
    {
        var watch = new WatchDefinition { PlcIp = Plc.Ip, Tag = "T", Mode = TriggerMode.WhileTrue, PollMs = 100 };
        var updates = await RunUntilErrors(watch, [1, double.NaN, 0, 1], errors: 2);
        Assert.Equal(2, updates.Count(u => u.Outcome == TriggerOutcome.Fired));
    }

    [Fact]
    public async Task BadFormula_ReportsErrorFromRunLoop_InsteadOfThrowing()
    {
        var watch = new WatchDefinition
        {
            PlcIp = Plc.Ip,
            Tag = "T",
            AndConditions = [new AndCondition { Tag = "B" }],
            Logic = "A and Q",
        };
        var updates = await RunUntilErrors(watch, [], errors: 1);
        var error = Assert.Single(updates, u => u.Status == WatchStatus.Error);
        Assert.StartsWith("Formula or expression:", error.Message);
    }
}
