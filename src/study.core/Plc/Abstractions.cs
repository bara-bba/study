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

/// <summary>One entry of a controller's tag list (for the tag browser).</summary>
public sealed record BrowsedTag(string Name, TagDataType? DataType, string TypeLabel, bool IsProgram, bool IsArray);

public interface ITagBrowser
{
    /// <summary>Lists controller-scope tags, or a program's tags when <paramref name="program"/> is set.</summary>
    Task<IReadOnlyList<BrowsedTag>> ListTagsAsync(PlcEntry plc, string? program, TimeSpan timeout, CancellationToken ct);
}

public sealed class PlcException : Exception
{
    public PlcException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
