using System.Net.NetworkInformation;
using libplctag;
using libplctag.DataTypes;
using Study.Core.Models;

namespace Study.Core.Plc;

/// <summary>
/// libplctag-backed reader for ControlLogix / CompactLogix over EtherNet/IP.
///
/// READ-ONLY BY DESIGN: this file calls <c>InitializeAsync</c> and <c>ReadAsync</c> only.
/// A unit test (ReadOnlyGuardTests) scans the source tree and fails the build's test run if a
/// libplctag write call ever appears anywhere in study.
/// </summary>
public sealed class LibPlcTagReader : ITagReader
{
    private readonly Tag _tag;
    private readonly TagAddress _address;

    public LibPlcTagReader(PlcEntry plc, TagAddress address, TimeSpan timeout)
    {
        _address = address;
        _tag = new Tag
        {
            Gateway = plc.Ip,
            Path = CipPath(plc.Slot),
            PlcType = PlcType.ControlLogix,
            Protocol = Protocol.ab_eip,
            Name = address.ReadName,
            Timeout = timeout,
            ElementCount = 1,
        };

        // For a bit-of-integer read the parent's size is whatever the controller returns.
        if (address.Bit is null)
        {
            _tag.ElementSize = address.DataType.ElementSize();
        }
    }

    /// <summary>CIP route: backplane port 1, then the CPU's slot.</summary>
    public static string CipPath(int slot) => $"1,{slot}";

    public async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            await _tag.InitializeAsync(ct).ConfigureAwait(false);
        }
        catch (LibPlcTagException ex)
        {
            throw new PlcException(Friendly(ex.Message, _address.ReadName), ex);
        }
    }

    public async Task<double> ReadAsync(CancellationToken ct)
    {
        try
        {
            await _tag.ReadAsync(ct).ConfigureAwait(false);
            return Decode();
        }
        catch (LibPlcTagException ex)
        {
            throw new PlcException(Friendly(ex.Message, _address.ReadName), ex);
        }
    }

    private double Decode()
    {
        if (_address.Bit is { } bit)
        {
            var size = _tag.GetSize();
            if (bit >= size * 8)
            {
                throw new PlcException($"Bit .{bit} is out of range: '{_address.ReadName}' is only {size * 8} bits wide.");
            }

            return _tag.GetBit(bit) ? 1 : 0;
        }

        return _address.DataType switch
        {
            TagDataType.Bool => _tag.GetUInt8(0) != 0 ? 1 : 0,
            TagDataType.Sint => _tag.GetInt8(0),
            TagDataType.Int => _tag.GetInt16(0),
            TagDataType.Dint => _tag.GetInt32(0),
            TagDataType.Lint => _tag.GetInt64(0),
            TagDataType.Real => _tag.GetFloat32(0),
            _ => throw new PlcException($"Unsupported data type {_address.DataType}."),
        };
    }

    /// <summary>Turns libplctag status names into something an operator can act on.</summary>
    internal static string Friendly(string raw, string tagName)
    {
        if (raw.Contains("ErrorNotFound", StringComparison.Ordinal))
        {
            return $"Tag '{tagName}' not found in the PLC. Check spelling and scope (program tags need Program:<name>.<tag>).";
        }

        if (raw.Contains("ErrorTimeout", StringComparison.Ordinal))
        {
            return "Timeout: the PLC did not answer on EtherNet/IP (TCP 44818).";
        }

        if (raw.Contains("ErrorBadGateway", StringComparison.Ordinal)
            || raw.Contains("ErrorBadConnection", StringComparison.Ordinal)
            || raw.Contains("ErrorRemoteErr", StringComparison.Ordinal))
        {
            return $"PLC refused the connection ({raw}). Wrong CPU slot? Try 'Detect slot' in the PLC list.";
        }

        if (raw.Contains("ErrorTooSmall", StringComparison.Ordinal)
            || raw.Contains("ErrorTooLarge", StringComparison.Ordinal)
            || raw.Contains("ErrorBadData", StringComparison.Ordinal))
        {
            return $"Data type mismatch reading '{tagName}' ({raw}). Check the watch's data type.";
        }

        return raw;
    }

    public void Dispose() => _tag.Dispose();
}

public sealed class LibPlcTagReaderFactory : ITagReaderFactory
{
    public ITagReader Create(PlcEntry plc, TagAddress address, TimeSpan timeout) =>
        new LibPlcTagReader(plc, address, timeout);
}

public sealed class IcmpPinger : IPinger
{
    public async Task<bool> PingAsync(string ip, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, (int)timeout.TotalMilliseconds).WaitAsync(ct).ConfigureAwait(false);
            return reply.Status == IPStatus.Success;
        }
        catch (PingException)
        {
            return false;
        }
    }
}

/// <summary>Reads the controller's tag list (the read-only <c>@tags</c> service).</summary>
public sealed class LibPlcTagBrowser : ITagBrowser
{
    public async Task<IReadOnlyList<BrowsedTag>> ListTagsAsync(PlcEntry plc, string? program, TimeSpan timeout,
        CancellationToken ct)
    {
        using var tag = new Tag<TagInfoPlcMapper, TagInfo[]>
        {
            Gateway = plc.Ip,
            Path = LibPlcTagReader.CipPath(plc.Slot),
            PlcType = PlcType.ControlLogix,
            Protocol = Protocol.ab_eip,
            Name = string.IsNullOrEmpty(program) ? "@tags" : $"{program}.@tags",
            Timeout = timeout,
        };

        TagInfo[] infos;
        try
        {
            infos = await tag.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (LibPlcTagException ex)
        {
            throw new PlcException(LibPlcTagReader.Friendly(ex.Message, tag.Name), ex);
        }

        return infos.Select(i => ToBrowsed(i, program)).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private const ushort StructBit = 0x8000;
    private const ushort SystemBit = 0x1000;
    private const ushort ArrayBits = 0x6000;
    private const ushort UdtIdMask = 0x0FFF;

    private readonly Dictionary<(string Ip, ushort Id), UdtInfo> _udtCache = new();

    public async Task<IReadOnlyList<BrowsedTag>> ListMembersAsync(PlcEntry plc, string parentName, ushort udtId,
        TimeSpan timeout, CancellationToken ct)
    {
        if (!_udtCache.TryGetValue((plc.Ip, udtId), out var udt))
        {
            using var tag = new Tag<UdtInfoPlcMapper, UdtInfo>
            {
                Gateway = plc.Ip,
                Path = LibPlcTagReader.CipPath(plc.Slot),
                PlcType = PlcType.ControlLogix,
                Protocol = Protocol.ab_eip,
                Name = $"@udt/{udtId}",
                Timeout = timeout,
            };

            try
            {
                udt = await tag.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (LibPlcTagException ex)
            {
                throw new PlcException($"Could not read the structure layout of '{parentName}' ({ex.Message}).", ex);
            }

            _udtCache[(plc.Ip, udtId)] = udt;
        }

        return udt.Fields
            .Where(f => !IsHiddenMember(f.Name))
            .Select(f => ToMember(f, parentName))
            .ToList(); // keep the controller's member order — it matches Studio 5000's
    }

    /// <summary>
    /// Compiler-generated members: the hidden SINT that hosts a UDT's BOOLs ("ZZZZZZZZZZ…") and
    /// internals like "__…". Neither can be addressed by name.
    /// </summary>
    internal static bool IsHiddenMember(string name) =>
        string.IsNullOrEmpty(name)
        || name.StartsWith("ZZZZZZZZZZ", StringComparison.Ordinal)
        || name.StartsWith("__", StringComparison.Ordinal);

    internal static BrowsedTag ToMember(UdtFieldInfo field, string parentName)
    {
        var isStruct = (field.Type & StructBit) != 0;
        // In a template an array member is flagged with 0x2000 and Metadata holds its length;
        // for a BOOL member Metadata is the bit number instead, so only the flag counts.
        var isArray = (field.Type & 0x2000) != 0;
        var atomic = isStruct ? null : TagDataTypeExtensions.FromCipTypeCode(field.Type);
        var label = atomic?.ToString().ToUpperInvariant() ?? (isStruct ? "UDT/struct" : $"0x{field.Type:X4}");
        if (isArray)
        {
            label += $"[{field.Metadata}]";
        }

        return new BrowsedTag($"{parentName}.{field.Name}", isArray ? null : atomic, label, false, isArray,
            isStruct ? (ushort)(field.Type & UdtIdMask) : null);
    }

    private static BrowsedTag ToBrowsed(TagInfo info, string? program)
    {
        var isProgram = info.Name.StartsWith("Program:", StringComparison.OrdinalIgnoreCase);
        var isArray = (info.Type & ArrayBits) != 0;
        var isStruct = (info.Type & StructBit) != 0;
        var atomic = !isStruct ? TagDataTypeExtensions.FromCipTypeCode(info.Type) : null;
        var label = isProgram ? "PROGRAM"
            : atomic?.ToString().ToUpperInvariant()
              ?? (isStruct ? "UDT/struct" : (info.Type & SystemBit) != 0 ? "system" : $"0x{info.Type:X4}");
        if (isArray)
        {
            label += "[]";
        }

        var name = string.IsNullOrEmpty(program) || isProgram ? info.Name : $"{program}.{info.Name}";
        return new BrowsedTag(name, isArray ? null : atomic, label, isProgram, isArray,
            isStruct && !isProgram ? (ushort)(info.Type & UdtIdMask) : null);
    }
}
