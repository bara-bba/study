using Study.Core.Models;

namespace Study.Core.Plc;

/// <summary>
/// A connected, READ-ONLY handle on one PLC tag. study deliberately has no write path:
/// nothing in this interface (or its implementation) can change a value in a controller.
/// </summary>
public interface ITagReader : IDisposable
{
    /// <summary>Opens the CIP connection and does the first read. Throws <see cref="PlcException"/>.</summary>
    Task InitializeAsync(CancellationToken ct);

    /// <summary>Reads the current value as a number (BOOL = 1 / 0). Throws <see cref="PlcException"/>.</summary>
    Task<double> ReadAsync(CancellationToken ct);
}

public interface ITagReaderFactory
{
    ITagReader Create(PlcEntry plc, TagAddress address, TimeSpan timeout);
}

public interface IPinger
{
    Task<bool> PingAsync(string ip, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// One entry of a controller's tag list (for the tag browser). <paramref name="UdtId"/> is set for a
/// structure (UDT / AOI / module-defined type), whose members can be listed with
/// <see cref="ITagBrowser.ListMembersAsync"/>.
/// </summary>
public sealed record BrowsedTag(string Name, TagDataType? DataType, string TypeLabel, bool IsProgram, bool IsArray,
    ushort? UdtId = null)
{
    /// <summary>A single structure (not an array of them) whose members can be opened.</summary>
    public bool IsStruct => UdtId is not null && !IsArray;

    /// <summary>Can be watched directly: an atomic, non-array tag.</summary>
    public bool IsWatchable => DataType is not null && !IsArray && !IsProgram;
}

public interface ITagBrowser
{
    /// <summary>Lists controller-scope tags, or a program's tags when <paramref name="program"/> is set.</summary>
    Task<IReadOnlyList<BrowsedTag>> ListTagsAsync(PlcEntry plc, string? program, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Lists the members of structure type <paramref name="udtId"/> (read-only <c>@udt/&lt;id&gt;</c> template read),
    /// named <c>&lt;parentName&gt;.&lt;member&gt;</c>.
    /// </summary>
    Task<IReadOnlyList<BrowsedTag>> ListMembersAsync(PlcEntry plc, string parentName, ushort udtId, TimeSpan timeout,
        CancellationToken ct);
}

public sealed class PlcException : Exception
{
    public PlcException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
