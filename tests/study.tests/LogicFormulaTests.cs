using Study.Core.Config;
using Study.Core.Expressions;
using Study.Core.Models;
using Study.Core.Triggers;
using Xunit;

namespace Study.Tests;

public class LogicFormulaTests
{
    private static bool[] Bits(string s) => s.Select(c => c == '1').ToArray();

    [Theory]
    [InlineData("A and B", "11", true)]
    [InlineData("A and B", "10", false)]
    [InlineData("A or B", "01", true)]
    [InlineData("A xor B", "11", false)]
    [InlineData("A xor B", "10", true)]
    [InlineData("A && !B", "10", true)]
    [InlineData("A & (B | C)", "101", true)]
    [InlineData("A ^ B ^ C", "111", true)]   // odd number of TRUE
    [InlineData("not (A or B)", "00", true)]
    [InlineData("a AND b", "11", true)]      // case-insensitive
    public void Evaluates(string formula, string values, bool expected) =>
        Assert.Equal(expected, LogicFormula.Parse(formula, values.Length).Evaluate(Bits(values)));

    [Theory]
    [InlineData("A or B and C", "A OR B AND C")]          // AND binds tighter than OR
    [InlineData("(A or B) and C", "(A OR B) AND C")]
    [InlineData("A xor B or C", "A XOR B OR C")]
    [InlineData("A xor (B or C)", "A XOR (B OR C)")]
    [InlineData("((A))", "A")]
    [InlineData("!(A and B)", "NOT (A AND B)")]
    [InlineData("A and (B and C)", "A AND (B AND C)")]    // kept: right-nested is written as typed
    public void ShowsCanonicalText(string formula, string expected) =>
        Assert.Equal(expected, LogicFormula.Parse(formula, 3).ToString());

    [Fact]
    public void Precedence_AndBeforeXorBeforeOr()
    {
        var f = LogicFormula.Parse("A or B xor C and D", 4);
        // = A OR (B XOR (C AND D))
        Assert.True(f.Evaluate(Bits("0100")));
        Assert.False(f.Evaluate(Bits("0111")));
    }

    [Fact]
    public void Chain_AppliesEachJoinToEverythingAbove()
    {
        var f = LogicFormula.Chain([LogicJoin.Or, LogicJoin.And]);
        Assert.Equal("(A OR B) AND C", f.ToString());
        Assert.True(f.Evaluate(Bits("011")));
        Assert.False(f.Evaluate(Bits("110")));
        Assert.Equal("A", LogicFormula.Chain([]).ToString());
    }

    [Fact]
    public void Chain_Not_NegatesOnlyThatRow()
    {
        var f = LogicFormula.Chain([LogicJoin.And, LogicJoin.Or], [true, false]);
        Assert.Equal("A AND NOT B OR C", f.ToString());
        Assert.True(f.Evaluate(Bits("100")));   // A, not B
        Assert.False(f.Evaluate(Bits("110")));  // B blocks it
        Assert.True(f.Evaluate(Bits("011")));   // C alone
        Assert.Equal("A XOR NOT B", LogicFormula.Chain([LogicJoin.Xor], [true]).ToString());
    }

    [Fact]
    public void Watch_BuildLogic_UsesNotFlag_AndJsonKeepsIt()
    {
        var w = new WatchDefinition
        {
            PlcIp = "10.0.0.1",
            Tag = "A",
            AndConditions = [new AndCondition { Tag = "B", Join = LogicJoin.Or, Not = true }],
        };
        Assert.Equal("A OR NOT B", w.BuildLogic().ToString());

        var dir = Directory.CreateTempSubdirectory("study-not-").FullName;
        try
        {
            var store = new JsonStore(dir);
            store.Save("w.json", new List<WatchDefinition> { w });
            Assert.True(store.Load("w.json", () => new List<WatchDefinition>()).Single().AndConditions.Single().Not);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("A and C", 2, "'C' does not exist")]
    [InlineData("A and B", 1, "only A")]
    [InlineData("A and", 2, "ends too early")]
    [InlineData("A + B", 2, "Unexpected character")]
    [InlineData("(A or B", 2, "Expected ')'")]
    [InlineData("A B", 2, "Unexpected 'B'")]
    [InlineData("AB", 2, "Unexpected 'AB'")]
    [InlineData("  ", 2, "empty")]
    public void Rejects(string formula, int letters, string message)
    {
        var ex = Assert.Throws<ExpressionException>(() => LogicFormula.Parse(formula, letters));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Letters_ListsWhatIsUsed()
    {
        Assert.Equal(new HashSet<int> { 0, 2 }, LogicFormula.Parse("A or C", 3).Letters.ToHashSet());
    }

    // -------------------------------------------------------------- watch level

    [Fact]
    public void Watch_ValidatesCustomFormula()
    {
        var w = new WatchDefinition
        {
            PlcIp = "10.0.0.1",
            Tag = "A",
            AndConditions = [new AndCondition { Tag = "B" }],
            Logic = "A or C",
        };
        Assert.Contains(w.Validate(), e => e.StartsWith("Formula:"));
        Assert.Empty((w with { Logic = "A xor B", Mode = TriggerMode.WhileTrue }).Validate());
    }

    [Fact]
    public void Watch_BuildLogic_UsesJoinsWhenNoFormula()
    {
        var w = new WatchDefinition
        {
            AndConditions =
            [
                new AndCondition { Tag = "B", Join = LogicJoin.Xor },
                new AndCondition { Tag = "C", Join = LogicJoin.Or },
            ],
        };
        Assert.Equal("A XOR B OR C", w.BuildLogic().ToString());
        Assert.Equal("A AND NOT C", (w with { Logic = "a and not c" }).BuildLogic().ToString());
    }

    [Fact]
    public void Json_RoundTripsJoinAndLogic_OldConditionsLoadAsAnd()
    {
        var dir = Directory.CreateTempSubdirectory("study-logic-").FullName;
        try
        {
            var store = new JsonStore(dir);
            var w = new WatchDefinition
            {
                PlcIp = "10.0.0.1",
                Tag = "A",
                AndConditions = [new AndCondition { Tag = "B", Join = LogicJoin.Or }],
                Logic = "A xor B",
            };
            store.Save("w.json", new List<WatchDefinition> { w });
            var back = store.Load("w.json", () => new List<WatchDefinition>()).Single();
            Assert.Equal(w, back);

            File.WriteAllText(Path.Combine(dir, "old.json"),
                """[ { "PlcIp": "10.0.0.1", "Tag": "A", "AndConditions": [ { "Tag": "B", "Test": "IsFalse" } ] } ]""");
            var old = store.Load("old.json", () => new List<WatchDefinition>()).Single();
            Assert.Equal(LogicJoin.And, old.AndConditions.Single().Join);
            Assert.False(old.HasCustomLogic);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------ trigger engine

    [Fact]
    public void Level_Or_FiresWhenEitherHolds_ClearsWhenBothStop()
    {
        var e = new TriggerEngine(TriggerMode.WhileTrue);
        var b = false;
        Func<bool, bool> or = a => a || b;
        Assert.Equal(TriggerOutcome.None, e.Update(0, or));
        b = true;
        Assert.Equal(TriggerOutcome.Fired, e.Update(0, or));   // B alone
        Assert.Equal(TriggerOutcome.None, e.Update(1, or));    // both: still met, no re-fire
        b = false;
        Assert.Equal(TriggerOutcome.None, e.Update(1, or));    // A alone keeps it met
        Assert.Equal(TriggerOutcome.Cleared, e.Update(0, or));
    }

    [Fact]
    public void Level_Xor_ClearsWhenBothTrue()
    {
        var e = new TriggerEngine(TriggerMode.WhileTrue);
        var b = false;
        Func<bool, bool> xor = a => a ^ b;
        Assert.Equal(TriggerOutcome.Fired, e.Update(1, xor));
        b = true;
        Assert.Equal(TriggerOutcome.Cleared, e.Update(1, xor));
        Assert.Equal(TriggerOutcome.Fired, e.Update(0, xor));
    }

    [Fact]
    public void Edge_Or_FiresOnEdgeOrWhenOtherBecomesTrue_OncePerRise()
    {
        var e = new TriggerEngine(TriggerMode.RisingEdge);
        var b = false;
        Func<bool, bool> or = a => a || b;
        e.Update(0, or);
        Assert.Equal(TriggerOutcome.Fired, e.Update(1, or));   // the edge
        Assert.Equal(TriggerOutcome.None, e.Update(1, or));    // no new edge
        b = true;
        Assert.Equal(TriggerOutcome.Fired, e.Update(1, or));   // B became true
        Assert.Equal(TriggerOutcome.None, e.Update(1, or));    // B still true: once only
        Assert.True(e.Acknowledge());
        Assert.Equal(AlarmState.Idle, e.State);                // edges have no "still active" state
    }
}
