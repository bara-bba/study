namespace Study.Core.Models;

/// <summary>A Logix controller reachable over EtherNet/IP.</summary>
public sealed record PlcEntry
{
    public string Name { get; init; } = "";

    public string Ip { get; init; } = "";

    /// <summary>
    /// Backplane slot of the CPU (CIP path "1,&lt;slot&gt;"). 0 for CompactLogix and for most
    /// ControlLogix racks; use the PLC window's "Detect slot" when unsure.
    /// </summary>
    public int Slot { get; init; }

    /// <summary>Where the entry came from: "RSLinx", "CSV", "Manual".</summary>
    public string Source { get; init; } = "Manual";

    public string Notes { get; init; } = "";

    /// <summary>
    /// Folder in the PLC tree, as a '/'-separated path ("Line1/Stacker"). Empty = top level.
    /// </summary>
    public string Group { get; init; } = "";

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Ip : $"{Name} ({Ip})";

    /// <summary>"Line1 › Stacker" for display, or "" at top level.</summary>
    public string GroupLabel => Group.Replace("/", " › ");
}
