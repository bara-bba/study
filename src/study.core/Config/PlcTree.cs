using System.Text.Json;
using System.Text.Json.Serialization;
using Study.Core.Models;

namespace Study.Core.Config;

/// <summary>One folder of the PLC tree as stored in plcs.json.</summary>
public sealed class PlcGroupNode
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("groups")]
    public List<PlcGroupNode> Groups { get; set; } = [];

    [JsonPropertyName("plcs")]
    public List<PlcItem> Plcs { get; set; } = [];
}

/// <summary>A PLC inside the tree file (its folder is implied by where it sits).</summary>
public sealed class PlcItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("ip")]
    public string Ip { get; set; } = "";

    [JsonPropertyName("slot")]
    public int Slot { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = "Manual";

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = "";
}

/// <summary>Root of plcs.json: top-level folders plus PLCs that sit in no folder.</summary>
public sealed class PlcTreeFile
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = PlcTree.FormatVersion;

    [JsonPropertyName("groups")]
    public List<PlcGroupNode> Groups { get; set; } = [];

    [JsonPropertyName("plcs")]
    public List<PlcItem> Plcs { get; set; } = [];
}

/// <summary>
/// Converts between the flat in-memory PLC list (each entry carries a Group path) and the
/// nested, alphabetically sorted tree that is written to plcs.json.
/// </summary>
public static class PlcTree
{
    public const int FormatVersion = 2;
    public const char Separator = '/';

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Trims each segment and drops empty ones: " TRN3 // MDP " → "TRN3/MDP".</summary>
    public static string NormalizeGroup(string? group) =>
        string.Join(Separator, (group ?? "")
            .Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>
    /// Sort order used everywhere = the tree read top to bottom: at each level the sub-folders
    /// first, then the PLCs; all natural A→Z (name, then IP).
    /// </summary>
    public static List<PlcEntry> Sort(IEnumerable<PlcEntry> plcs) => Collect(ToTree(plcs));

    public static PlcTreeFile ToTree(IEnumerable<PlcEntry> plcs)
    {
        var root = new PlcGroupNode();
        foreach (var p in plcs)
        {
            var node = root;
            foreach (var segment in NormalizeGroup(p.Group).Split(Separator, StringSplitOptions.RemoveEmptyEntries))
            {
                var child = node.Groups.FirstOrDefault(g => string.Equals(g.Name, segment, StringComparison.OrdinalIgnoreCase));
                if (child is null)
                {
                    child = new PlcGroupNode { Name = segment };
                    node.Groups.Add(child);
                }

                node = child;
            }

            node.Plcs.Add(new PlcItem { Name = p.Name, Ip = p.Ip, Slot = p.Slot, Source = p.Source, Notes = p.Notes });
        }

        SortNode(root);
        return new PlcTreeFile { Groups = root.Groups, Plcs = root.Plcs };
    }

    /// <summary>Flat list from a (possibly hand-edited, unsorted) tree, in tree order.</summary>
    public static List<PlcEntry> Flatten(PlcTreeFile file) => Sort(Collect(file));

    private static List<PlcEntry> Collect(PlcTreeFile file)
    {
        var result = new List<PlcEntry>();
        foreach (var g in file.Groups)
        {
            FlattenNode(result, g, "");
        }

        AddItems(result, file.Plcs, "");
        return result;
    }

    public static string Serialize(IEnumerable<PlcEntry> plcs) => JsonSerializer.Serialize(ToTree(plcs), WriteOptions);

    /// <summary>
    /// Reads plcs.json. Accepts the tree format and the original flat array
    /// (<c>[{"Name":…,"Ip":…}]</c>), so older files load without any manual step.
    /// </summary>
    /// <exception cref="JsonException">Not valid JSON, or neither format.</exception>
    public static List<PlcEntry> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        var plcs = doc.RootElement.ValueKind switch
        {
            JsonValueKind.Array => (doc.RootElement.Deserialize<List<PlcEntry>>(ReadOptions) ?? [])
                .Select(p => p with { Group = NormalizeGroup(p.Group) }),
            JsonValueKind.Object => Flatten(doc.RootElement.Deserialize<PlcTreeFile>(ReadOptions) ?? new PlcTreeFile()),
            _ => throw new JsonException("Expected a PLC tree object or a list of PLCs."),
        };

        // A hand-edited file may list an IP twice; the IP is the PLC's identity, so keep the first.
        return Sort(plcs.Where(p => p.Ip.Length > 0).DistinctBy(p => p.Ip));
    }

    /// <summary>All folder paths in use, including parents ("A", "A/B"), sorted.</summary>
    public static List<string> AllGroups(IEnumerable<PlcEntry> plcs)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in plcs)
        {
            var parts = NormalizeGroup(p.Group).Split(Separator, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i <= parts.Length; i++)
            {
                set.Add(string.Join(Separator, parts.Take(i)));
            }
        }

        return set.OrderBy(g => g, NaturalComparer.Instance).ToList();
    }

    /// <summary>True when <paramref name="group"/> is <paramref name="folder"/> or lies beneath it.</summary>
    public static bool IsInFolder(string group, string folder) =>
        string.Equals(group, folder, StringComparison.OrdinalIgnoreCase)
        || group.StartsWith(folder + Separator, StringComparison.OrdinalIgnoreCase);

    /// <summary>Moves a folder (and everything beneath it) to a new path. Empty = top level.</summary>
    public static List<PlcEntry> RenameFolder(IEnumerable<PlcEntry> plcs, string folder, string newPath)
    {
        folder = NormalizeGroup(folder);
        newPath = NormalizeGroup(newPath);
        return Sort(plcs.Select(p =>
        {
            if (folder.Length == 0 || !IsInFolder(p.Group, folder))
            {
                return p;
            }

            var rest = p.Group.Length > folder.Length ? p.Group[(folder.Length + 1)..] : "";
            return p with { Group = NormalizeGroup(newPath + Separator + rest) };
        }));
    }

    private static void FlattenNode(List<PlcEntry> result, PlcGroupNode node, string parent)
    {
        var path = NormalizeGroup(parent.Length == 0 ? node.Name : parent + Separator + node.Name);
        foreach (var child in node.Groups)
        {
            FlattenNode(result, child, path);
        }

        AddItems(result, node.Plcs, path);
    }

    private static void AddItems(List<PlcEntry> result, IEnumerable<PlcItem> items, string group)
    {
        foreach (var i in items)
        {
            result.Add(new PlcEntry
            {
                Name = i.Name?.Trim() ?? "",
                Ip = i.Ip?.Trim() ?? "",
                Slot = i.Slot,
                Source = string.IsNullOrWhiteSpace(i.Source) ? "Manual" : i.Source,
                Notes = i.Notes ?? "",
                Group = group,
            });
        }
    }

    private static void SortNode(PlcGroupNode node)
    {
        node.Groups = node.Groups.OrderBy(g => g.Name, NaturalComparer.Instance).ToList();
        node.Plcs = node.Plcs
            .OrderBy(p => string.IsNullOrWhiteSpace(p.Name) ? p.Ip : p.Name, NaturalComparer.Instance)
            .ThenBy(p => p.Ip, NaturalComparer.Instance)
            .ToList();
        foreach (var g in node.Groups)
        {
            SortNode(g);
        }
    }
}

/// <summary>
/// Case-insensitive "natural" order: digit runs compare by value, so PH2 &lt; PH10 and
/// 10.137.16.9 &lt; 10.137.16.74.
/// </summary>
public sealed class NaturalComparer : IComparer<string?>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        x ??= "";
        y ??= "";
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                var c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                if (c != 0)
                {
                    return c;
                }
            }
            else
            {
                var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0)
                {
                    return c;
                }

                i++;
                j++;
            }
        }

        var rest = (x.Length - i).CompareTo(y.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(x, y);
    }
}
