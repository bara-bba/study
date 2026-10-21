using libplctag.DataTypes;
using Study.Core.Config;
using Study.Core.Models;
using Study.Core.Monitoring;
using Study.Core.Plc;
using Study.Core.Triggers;
using Xunit;

namespace Study.Tests;

public class AndConditionTests
{
    // ------------------------------------------------------------ trigger engine gate

    [Fact]
    public void Level_FiresOnlyWhileGateOpen_AndClearsWhenGateCloses()
    {
        var e = new TriggerEngine(TriggerMode.WhileTrue);
        Assert.Equal(TriggerOutcome.None, e.Update(1, a => a && false));      // main TRUE, other not
        Assert.Equal(TriggerOutcome.Fired, e.Update(1, a => a && true));      // both hold
        Assert.Equal(TriggerOutcome.Cleared, e.Update(1, a => a && false));   // AND stops holding
        Assert.Equal(TriggerOutcome.Fired, e.Update(1, a => a && true));      // holds again
    }

    [Fact]
    public void Edge_IgnoredWhileGateClosed()
    {
        var e = new TriggerEngine(TriggerMode.RisingEdge);
        e.Update(0);
        Assert.Equal(TriggerOutcome.None, e.Update(1, a => a && false));
        e.Update(0, a => a && true);
        Assert.Equal(TriggerOutcome.Fired, e.Update(1, a => a && true));
    }

    [Fact]
    public void Edge_WhileGateClosed_DoesNotFireLaterWithoutNewEdge()
    {
        var e = new TriggerEngine(TriggerMode.RisingEdge);
        e.Update(0);
        e.Update(1, a => a && false);
        Assert.Equal(TriggerOutcome.None, e.Update(1, a => a && true)); // no transition any more
    }

    // ------------------------------------------------------------------- evaluator

    [Theory]
    [InlineData(AndTest.IsTrue, 1, true)]
    [InlineData(AndTest.IsTrue, 0, false)]
    [InlineData(AndTest.IsFalse, 0, true)]
    [InlineData(AndTest.IsFalse, 1, false)]
    public void Evaluator_LevelTests(AndTest test, double value, bool expected) =>
        Assert.Equal(expected, new AndConditionEvaluator(new AndCondition { Tag = "B", Test = test }).IsMet(value));

    [Fact]
    public void Evaluator_Expression_UsesPrev()
    {
        var ev = new AndConditionEvaluator(new AndCondition
            { Tag = "N", DataType = TagDataType.Dint, Test = AndTest.Expression, Expression = "x > prev" });
        Assert.False(ev.IsMet(5)); // first read: prev = x
        Assert.True(ev.IsMet(6));
        ev.ResetBaseline();
        Assert.False(ev.IsMet(7));
    }

    [Fact]
    public void Validate_ReportsEachBadRowByPosition()
    {
        var watch = new WatchDefinition
        {
            PlcIp = "10.0.0.1",
            Tag = "A",
            AndConditions =
            [
                new AndCondition { Tag = "B" },
                new AndCondition { Tag = "" },
                new AndCondition { Tag = "C", Test = AndTest.Expression, Expression = "x >" },
            ],
        };

        var errors = watch.Validate();
        Assert.Equal(2, errors.Count);
        Assert.StartsWith("Condition C:", errors[0]);
        Assert.StartsWith("Condition D: expression", errors[1]);
    }

    // ------------------------------------------------------------------ persistence

    [Fact]
    public void Json_RoundTripsAndConditions_AndOldFilesLoadWithNone()
    {
        var dir = Directory.CreateTempSubdirectory("study-and-").FullName;
        try
        {
            var store = new JsonStore(dir);
            var watch = new WatchDefinition
            {
                PlcIp = "10.0.0.1",
                Tag = "A",
                AndConditions = [new AndCondition { Tag = "B", Test = AndTest.IsFalse }],
            };
            store.Save("w.json", new List<WatchDefinition> { watch });
            var back = store.Load("w.json", () => new List<WatchDefinition>()).Single();
            Assert.Equal("B", back.AndConditions.Single().Tag);
            Assert.Equal(AndTest.IsFalse, back.AndConditions.Single().Test);

            File.WriteAllText(Path.Combine(dir, "old.json"), """[ { "PlcIp": "10.0.0.1", "Tag": "A" } ]""");
            Assert.Empty(store.Load("old.json", () => new List<WatchDefinition>()).Single().AndConditions);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ----------------------------------------------------------------------- runner

    /// <summary>Each tag name gets its own value sequence; the last value repeats.</summary>
    private sealed class ScriptedFactory(Dictionary<string, double[]> script) : ITagReaderFactory
    {
        public ITagReader Create(PlcEntry plc, TagAddress address, TimeSpan timeout) =>
            new Reader(script[address.ReadName]);

        private sealed class Reader(double[] values) : ITagReader
        {
            private int _i;

            public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

            public Task<double> ReadAsync(CancellationToken ct) =>
                Task.FromResult(values[Math.Min(_i++, values.Length - 1)]);

            public void Dispose()
            {
            }
        }
    }

    private sealed class OkPinger : IPinger
    {
        public Task<bool> PingAsync(string ip, TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
    }

    [Fact]
    public async Task Runner_FiresOnlyWhenBothHold_AndPublishesAndValues()
    {
        var plc = new PlcEntry { Name = "P", Ip = "10.0.0.1" };
        var watch = new WatchDefinition
        {
            PlcIp = plc.Ip,
            Tag = "Run",
            Mode = TriggerMode.WhileTrue,
            PollMs = 100,
            AndConditions = [new AndCondition { Tag = "Feedback", Test = AndTest.IsFalse }],
        };
        // Run: TRUE throughout. Feedback: TRUE, TRUE, then FALSE → alarm on the 3rd read only.
        var factory = new ScriptedFactory(new()
        {
            ["Run"] = [1],
            ["Feedback"] = [1, 1, 0],
        });
        var runner = new WatchRunner(watch, plc, factory, new OkPinger(), skipPing: true);
        var updates = new List<WatchUpdate>();
        var fired = new TaskCompletionSource();
        runner.Updated += u =>
        {
            lock (updates)
            {
                updates.Add(u);
            }

            if (u.Outcome == TriggerOutcome.Fired)
            {
                fired.TrySetResult();
            }
        };

        runner.Start();
        await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runner.StopAsync();

        lock (updates)
        {
            var reads = updates.Where(u => u.Status == WatchStatus.Connected).ToList();
            Assert.Equal(TriggerOutcome.None, reads[0].Outcome);
            Assert.Equal(TriggerOutcome.None, reads[1].Outcome);
            Assert.Equal(TriggerOutcome.Fired, reads[2].Outcome);
            Assert.Equal([1.0], reads[0].AndValues);
            Assert.Equal([false], reads[0].AndMet);
            Assert.Equal([true], reads[2].AndMet);
        }
    }

    // ----------------------------------------------------------- UDT member mapping

    [Fact]
    public void UdtMember_MapsAtomicBoolStructAndArray()
    {
        var b = LibPlcTagBrowser.ToMember(new UdtFieldInfo { Name = "Fault", Type = 0xC1, Metadata = 3 }, "Motor1");
        Assert.Equal("Motor1.Fault", b.Name);
        Assert.Equal(TagDataType.Bool, b.DataType);
        Assert.True(b.IsWatchable);
        Assert.False(b.IsArray); // Metadata on a BOOL is its bit number, not an array length

        var timer = LibPlcTagBrowser.ToMember(new UdtFieldInfo { Name = "Tmr", Type = 0x8F83 }, "Motor1");
        Assert.True(timer.IsStruct);
        Assert.Equal((ushort)0x0F83, timer.UdtId);
        Assert.False(timer.IsWatchable);

        var arr = LibPlcTagBrowser.ToMember(new UdtFieldInfo { Name = "Vals", Type = 0x20C4, Metadata = 10 }, "M");
        Assert.True(arr.IsArray);
        Assert.Equal("DINT[10]", arr.TypeLabel);
        Assert.False(arr.IsWatchable);
    }

    [Theory]
    [InlineData("ZZZZZZZZZZMotor0", true)]
    [InlineData("__Internal", true)]
    [InlineData("", true)]
    [InlineData("Fault", false)]
    public void UdtMember_HidesCompilerGenerated(string name, bool hidden) =>
        Assert.Equal(hidden, LibPlcTagBrowser.IsHiddenMember(name));
}
