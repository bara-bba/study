using Microsoft.Win32;

namespace Study.Services;

/// <summary>
/// Reads the IP list that RSLinx Classic keeps for its "Ethernet devices" (AB_ETH) drivers:
/// <c>HKLM\SOFTWARE\WOW6432Node\Rockwell Software\RSLinx\Drivers\AB_ETH\&lt;driver&gt;\Node Table</c>.
/// Read-only registry access; no RSLinx process or licence is needed.
/// </summary>
internal static class RsLinxImporter
{
    private static readonly string[] Roots =
    [
        @"SOFTWARE\WOW6432Node\Rockwell Software\RSLinx\Drivers\AB_ETH",
        @"SOFTWARE\Rockwell Software\RSLinx\Drivers\AB_ETH",
    ];

    /// <summary>Returns (driver name, node value) pairs; empty when RSLinx is not installed.</summary>
    public static List<(string Driver, string Value)> ReadNodeTables()
    {
        var result = new List<(string, string)>();
        foreach (var root in Roots)
        {
            using var drivers = Registry.LocalMachine.OpenSubKey(root, writable: false);
            if (drivers is null)
            {
                continue;
            }

            foreach (var driver in drivers.GetSubKeyNames())
            {
                using var table = drivers.OpenSubKey($@"{driver}\Node Table", writable: false);
                if (table is null)
                {
                    continue;
                }

                // Values are named 0,1,2…; keep that order.
                foreach (var name in table.GetValueNames().OrderBy(n => int.TryParse(n, out var i) ? i : int.MaxValue))
                {
                    if (table.GetValue(name) is string value)
                    {
                        result.Add((driver, value));
                    }
                }
            }

            if (result.Count > 0)
            {
                break; // found under the first root; don't double-read the other view
            }
        }

        return result;
    }
}
