using Study.Core.Config;
using Study.Core.Logging;
using Study.Core.Models;
using Study.Core.Plc;

namespace Study.Tests;

public class PlcListTests
{
    [Fact]
    public void ParseCsv_HeaderCommaAndSlot()
    {
        var (plcs, skipped) = PlcList.ParseCsv(["name,ip,slot", "UIS F1,10.137.16.74,0", "Merge,10.137.16.63,2"]);
        Assert.Empty(skipped);
        Assert.Equal(2, plcs.Count);
        Assert.Equal(new PlcEntry { Name = "Merge", Ip = "10.137.16.63", Slot = 2, Source = "CSV" }, plcs[1]);
    }

    [Fact]
    public void ParseCsv_SemicolonIpFirstAndIpOnly()
    {
        var (plcs, _) = PlcList.ParseCsv(["10.0.0.1;Sorter", "10.0.0.2", "", "# comment"]);
        Assert.Equal("Sorter", plcs[0].Name);
        Assert.Equal("10.0.0.2", plcs[1].Ip);
        Assert.Equal(2, plcs.Count);
    }

    [Fact]
    public void ParseCsv_ReportsBadRows()
    {
        var (plcs, skipped) = PlcList.ParseCsv(["name,ip", "A,999.1.1.1", "B,10.0.0.1,x"]);
        Assert.Empty(plcs);
        Assert.Equal(2, skipped.Count);
    }

    [Fact]
    public void ParseCsv_QuotedNameWithComma()
    {
        var (plcs, _) = PlcList.ParseCsv(["\"Line 1, North\",10.0.0.5"]);
        Assert.Equal("Line 1, North", plcs[0].Name);
    }

    [Fact]
    public void CsvRoundTrip()
    {
        var src = new[] { new PlcEntry { Name = "A, b", Ip = "10.0.0.1", Slot = 3, Notes = "n", Source = "CSV" } };
        var (back, _) = PlcList.ParseCsv(PlcList.ToCsv(src));
        Assert.Equal(src[0], back[0]);
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("10.1.2", false)]
    [InlineData("256.1.1.1", false)]
    [InlineData("plc01", false)]
    [InlineData("", false)]
    public void IPv4Validation(string ip, bool ok) => Assert.Equal(ok, PlcList.IsValidIPv4(ip));

    [Fact]
    public void FromRsLinx_DedupesAndSkipsNonIp()
    {
        var (plcs, skipped) = PlcList.FromRsLinx([("AB_ETH-1", "10.0.0.1"), ("AB_ETH-2", "10.0.0.1"), ("AB_ETH-2", "HOSTNAME")]);
        Assert.Single(plcs);
        Assert.Equal("AB_ETH-1", plcs[0].Notes);
        Assert.Single(skipped);
    }

    [Fact]
    public void Merge_KeepsExistingNameUnlessIncomingHasOne()
    {
        var existing = new List<PlcEntry> { new() { Name = "Sorter", Ip = "10.0.0.1", Slot = 1 } };
        var result = PlcList.Merge(existing, [new PlcEntry { Ip = "10.0.0.1" }, new PlcEntry { Ip = "10.0.0.2", Name = "New" }]);
        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Updated);
        var sorter = result.Plcs.Single(p => p.Ip == "10.0.0.1");
        Assert.Equal("Sorter", sorter.Name);
        Assert.Equal(1, sorter.Slot);
        Assert.Equal(["New", "Sorter"], result.Plcs.Select(p => p.Name)); // alphabetical

        var renamed = PlcList.Merge(result.Plcs, [new PlcEntry { Ip = "10.0.0.1", Name = "Renamed" }]);
        Assert.Equal(1, renamed.Updated);
        Assert.Equal("Renamed", renamed.Plcs.Single(p => p.Ip == "10.0.0.1").Name);
    }

    [Fact]
    public void Merge_KeepsExistingFolder_FillsEmptyOne()
    {
        var existing = new List<PlcEntry>
        {
            new() { Name = "A", Ip = "10.0.0.1", Group = "Mine" },
            new() { Name = "B", Ip = "10.0.0.2" },
        };
        var result = PlcList.Merge(existing, [
            new PlcEntry { Ip = "10.0.0.1", Group = "Theirs" },
            new PlcEntry { Ip = "10.0.0.2", Group = "Theirs" },
        ]);
        Assert.Equal("Mine", result.Plcs.Single(p => p.Ip == "10.0.0.1").Group);
        Assert.Equal("Theirs", result.Plcs.Single(p => p.Ip == "10.0.0.2").Group);
    }

    [Fact]
    public void CsvRoundTrip_WithGroup()
    {
        var src = new[] { new PlcEntry { Name = "S1", Ip = "10.0.0.9", Group = "TRN3-ELTEN/Stacker", Source = "CSV" } };
        var (back, _) = PlcList.ParseCsv(PlcList.ToCsv(src));
        Assert.Equal(src[0], back[0]);
    }
}

public class PlcTreeTests
{
    private static readonly List<PlcEntry> Sample =
    [
        new() { Name = "Stacker_10", Ip = "10.0.0.10", Group = "ELTEN/Stacker" },
        new() { Name = "Stacker_2", Ip = "10.0.0.2", Group = "ELTEN/Stacker" },
        new() { Name = "Destacker_1", Ip = "10.0.0.3", Group = "ELTEN/Destacker" },
        new() { Name = "mdp01", Ip = "10.0.0.4", Group = "MDP" },
        new() { Name = "Loose", Ip = "10.0.0.5" },
    ];

    [Fact]
    public void RoundTrip_PreservesEverything()
    {
        var back = PlcTree.Parse(PlcTree.Serialize(Sample));
        Assert.Equal(PlcTree.Sort(Sample), back);
    }

    [Fact]
    public void Tree_IsNestedAndAlphabetical()
    {
        var tree = PlcTree.ToTree(Sample);
        Assert.Equal(["ELTEN", "MDP"], tree.Groups.Select(g => g.Name));
        Assert.Equal(["Destacker", "Stacker"], tree.Groups[0].Groups.Select(g => g.Name));
        // Natural order: 2 before 10.
        Assert.Equal(["Stacker_2", "Stacker_10"], tree.Groups[0].Groups[1].Plcs.Select(p => p.Name));
        Assert.Equal("Loose", Assert.Single(tree.Plcs).Name);
    }

    [Fact]
    public void Parse_AcceptsLegacyFlatArray()
    {
        const string legacy = """[{"Name":"PH02","Ip":"10.1.1.2","Slot":0},{"Name":"PH01","Ip":"10.1.1.1","Slot":1}]""";
        var plcs = PlcTree.Parse(legacy);
        Assert.Equal(["PH01", "PH02"], plcs.Select(p => p.Name));
        Assert.All(plcs, p => Assert.Equal("", p.Group));
        Assert.Equal(1, plcs[0].Slot);
    }

    [Fact]
    public void Parse_HandEditedTreeWithCommentsAndTrailingCommas()
    {
        const string json = """
            {
              // hand-edited
              "groups": [ { "name": " Line 1 ", "plcs": [ { "name": "P", "ip": "10.0.0.1", }, ], }, ],
            }
            """;
        var p = Assert.Single(PlcTree.Parse(json));
        Assert.Equal("Line 1", p.Group);
        Assert.Equal("Manual", p.Source);
    }

    [Fact]
    public void Parse_RejectsGarbage() => Assert.ThrowsAny<System.Text.Json.JsonException>(() => PlcTree.Parse("42"));

    [Theory]
    [InlineData(" A // B / ", "A/B")]
    [InlineData("", "")]
    [InlineData("/", "")]
    public void NormalizeGroup(string input, string expected) => Assert.Equal(expected, PlcTree.NormalizeGroup(input));

    [Fact]
    public void RenameFolder_MovesSubtreeOnly()
    {
        var moved = PlcTree.RenameFolder(Sample, "ELTEN", "TRN3/ELTEN");
        Assert.Equal("TRN3/ELTEN/Stacker", moved.Single(p => p.Name == "Stacker_2").Group);
        Assert.Equal("MDP", moved.Single(p => p.Name == "mdp01").Group);
        Assert.Equal("", moved.Single(p => p.Name == "Loose").Group);
    }

    [Fact]
    public void RenameFolder_DoesNotMatchSiblingWithSamePrefix()
    {
        var plcs = new[] { new PlcEntry { Ip = "1.1.1.1", Group = "MDP" }, new PlcEntry { Ip = "1.1.1.2", Group = "MDP2" } };
        var moved = PlcTree.RenameFolder(plcs, "MDP", "X");
        Assert.Equal("X", moved.Single(p => p.Ip == "1.1.1.1").Group);
        Assert.Equal("MDP2", moved.Single(p => p.Ip == "1.1.1.2").Group);
    }

    [Fact]
    public void AllGroups_IncludesParents()
    {
        Assert.Equal(["ELTEN", "ELTEN/Destacker", "ELTEN/Stacker", "MDP"], PlcTree.AllGroups(Sample));
    }

    [Theory]
    [InlineData("PH2", "PH10", -1)]
    [InlineData("ph01", "PH02", -1)]
    [InlineData("10.137.16.9", "10.137.16.74", -1)]
    [InlineData("B", "a", 1)]
    public void NaturalComparer_Orders(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(NaturalComparer.Instance.Compare(a, b)));
}

public class TagAddressTests
{
    [Theory]
    [InlineData("Estop", TagDataType.Bool, "Estop", null)]
    [InlineData("Status.5", TagDataType.Bool, "Status", 5)]
    [InlineData("Local:1:I.Data.31", TagDataType.Bool, "Local:1:I.Data", 31)]
    [InlineData("Program:Main.Fault", TagDataType.Bool, "Program:Main.Fault", null)]
    [InlineData("Counter.5", TagDataType.Dint, "Counter.5", null)]
    public void Parse(string tag, TagDataType type, string readName, int? bit)
    {
        var a = TagAddress.Parse(tag, type);
        Assert.Equal(readName, a.ReadName);
        Assert.Equal(bit, a.Bit);
    }
}

public class EventLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "study-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void WritesDailyFileAndReadsBack()
    {
        var now = new DateTime(2026, 10, 15, 8, 30, 0, 123);
        var log = new EventLogger(_dir, () => now);
        log.Log(LogEvents.Trigger, "Estop, line 1", "Sorter", "10.0.0.1", "Estop", "TRUE", "says \"hi\"");
        log.Log(LogEvents.Ack, "Estop, line 1");

        var files = LogReader.ListFiles(_dir);
        Assert.Single(files);
        Assert.Equal("study-2026-10-15.csv", files[0].Name);

        var entries = LogReader.Read(files[0].FullName);
        Assert.Equal(2, entries.Count);
        Assert.Equal(now, entries[0].Timestamp);
        Assert.Equal("Estop, line 1", entries[0].Watch);
        Assert.Equal("says \"hi\"", entries[0].Detail);
        Assert.Equal(LogEvents.Ack, entries[1].Event);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

public class JsonStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "study-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RoundTripsWatchesWithEnumsAsStrings()
    {
        var store = new JsonStore(_dir);
        var w = new WatchDefinition { Name = "a", PlcIp = "10.0.0.1", Tag = "T", Mode = TriggerMode.Expression, Expression = "x > 1" };
        store.Save("watches.json", new List<WatchDefinition> { w });

        Assert.Contains("\"Expression\"", File.ReadAllText(store.PathFor("watches.json")));
        var back = store.Load("watches.json", () => new List<WatchDefinition>());
        Assert.Equal(w, back[0]);
    }

    [Fact]
    public void MissingFileGivesFallback_CorruptFileThrows()
    {
        var store = new JsonStore(_dir);
        Assert.Equal(80, store.Load(StudySettings.FileName, () => new StudySettings()).Volume);

        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.PathFor("bad.json"), "{ nope");
        Assert.Throws<ConfigException>(() => store.Load("bad.json", () => new StudySettings()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

public class WatchValidationTests
{
    [Fact]
    public void ValidWatchHasNoErrors() =>
        Assert.Empty(new WatchDefinition { PlcIp = "10.0.0.1", Tag = "T" }.Validate());

    [Fact]
    public void ReportsEveryProblem()
    {
        var errors = new WatchDefinition { PollMs = 10, Mode = TriggerMode.Expression, Expression = "x =" }.Validate();
        Assert.Equal(4, errors.Count);
    }
}
