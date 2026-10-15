using System.Net;
using System.Net.Sockets;
using Study.Core.Models;

namespace Study.Core.Config;

/// <summary>Result of an import: the merged list plus what changed, for the status line.</summary>
public sealed record ImportResult(IReadOnlyList<PlcEntry> Plcs, int Added, int Updated, IReadOnlyList<string> Skipped);

/// <summary>PLC list operations: CSV import/export, RSLinx import, merge by IP.</summary>
public static class PlcList
{
    public const string FileName = "plcs.json";

    public static bool IsValidIPv4(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.Count(c => c == '.') == 3
        && IPAddress.TryParse(text.Trim(), out var ip)
        && ip.AddressFamily == AddressFamily.InterNetwork;

    /// <summary>
    /// Parses CSV with columns <c>name,ip,slot,notes,group</c>. Header row optional, slot/notes/group optional,
    /// comma or semicolon separated (Excel in EU locales saves with ';'). A row with a single
    /// value is treated as an IP.
    /// </summary>
    public static (List<PlcEntry> Plcs, List<string> Skipped) ParseCsv(IEnumerable<string> lines)
    {
        var plcs = new List<PlcEntry>();
        var skipped = new List<string>();
        var lineNo = 0;
        foreach (var raw in lines)
        {
            lineNo++;
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var sep = line.Contains(';') && !line.Contains(',') ? ';' : ',';
            var cells = Csv.SplitLine(line, sep).Select(c => c.Trim()).ToArray();

            string name, ip;
            if (cells.Length == 1)
            {
                (name, ip) = ("", cells[0]);
            }
            else if (IsValidIPv4(cells[0]) && !IsValidIPv4(cells[1]))
            {
                (name, ip) = (cells[1], cells[0]); // "ip,name" order
            }
            else
            {
                (name, ip) = (cells[0], cells[1]);
            }

            if (!IsValidIPv4(ip))
            {
                // A header such as "name,ip,slot" lands here; only report real data rows.
                if (lineNo != 1)
                {
                    skipped.Add($"line {lineNo}: '{ip}' is not an IPv4 address");
                }

                continue;
            }

            var slot = 0;
            if (cells.Length > 2 && cells[2].Length > 0 && !int.TryParse(cells[2], out slot))
            {
                skipped.Add($"line {lineNo}: slot '{cells[2]}' is not a number");
                continue;
            }

            plcs.Add(new PlcEntry
            {
                Name = name,
                Ip = ip.Trim(),
                Slot = slot,
                Notes = cells.Length > 3 ? cells[3] : "",
                Group = cells.Length > 4 ? PlcTree.NormalizeGroup(cells[4]) : "",
                Source = "CSV",
            });
        }

        return (plcs, skipped);
    }

    public static IEnumerable<string> ToCsv(IEnumerable<PlcEntry> plcs)
    {
        yield return "name,ip,slot,notes,group";
        foreach (var p in PlcTree.Sort(plcs))
        {
            yield return string.Join(',', Csv.Quote(p.Name), p.Ip, p.Slot, Csv.Quote(p.Notes), Csv.Quote(p.Group));
        }
    }

    /// <summary>
    /// Turns RSLinx "Node Table" registry values into PLC entries. Values that are not IPv4
    /// addresses (host names, blanks) are skipped; duplicates across drivers are collapsed.
    /// </summary>
    public static (List<PlcEntry> Plcs, List<string> Skipped) FromRsLinx(
        IEnumerable<(string Driver, string Value)> nodeTable)
    {
        var plcs = new List<PlcEntry>();
        var skipped = new List<string>();
        var seen = new HashSet<string>();
        foreach (var (driver, value) in nodeTable)
        {
            var ip = value?.Trim() ?? "";
            if (!IsValidIPv4(ip))
            {
                skipped.Add($"{driver}: '{ip}' is not an IPv4 address");
                continue;
            }

            if (seen.Add(ip))
            {
                plcs.Add(new PlcEntry { Ip = ip, Source = "RSLinx", Notes = driver });
            }
        }

        return (plcs, skipped);
    }

    /// <summary>
    /// Merges <paramref name="incoming"/> into <paramref name="existing"/> by IP. Existing names
    /// and slots are kept unless the incoming row supplies a non-empty name. Never removes entries.
    /// </summary>
    public static ImportResult Merge(IReadOnlyList<PlcEntry> existing, IEnumerable<PlcEntry> incoming,
        IReadOnlyList<string>? skipped = null)
    {
        var byIp = existing.ToDictionary(p => p.Ip);
        var order = existing.Select(p => p.Ip).ToList();
        int added = 0, updated = 0;

        foreach (var p in incoming)
        {
            if (byIp.TryGetValue(p.Ip, out var current))
            {
                var merged = current with
                {
                    Name = string.IsNullOrWhiteSpace(p.Name) ? current.Name : p.Name,
                    // A CSV without a slot column parses as 0, so only a non-zero slot overrides.
                    Slot = p.Slot != 0 ? p.Slot : current.Slot,
                    Notes = string.IsNullOrWhiteSpace(current.Notes) ? p.Notes : current.Notes,
                    // Folders you arranged in study win; an import only fills an empty one.
                    Group = string.IsNullOrWhiteSpace(current.Group) ? p.Group : current.Group,
                };
                if (merged != current)
                {
                    byIp[p.Ip] = merged;
                    updated++;
                }
            }
            else
            {
                byIp[p.Ip] = p;
                order.Add(p.Ip);
                added++;
            }
        }

        return new ImportResult(PlcTree.Sort(order.Select(ip => byIp[ip])), added, updated, skipped ?? []);
    }
}
