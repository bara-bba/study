using System.Text.RegularExpressions;
using Study.Core.Models;

namespace Study.Core.Plc;

/// <summary>
/// How a watch's tag name maps onto a CIP read. A BOOL watch whose name ends in <c>.N</c>
/// (e.g. <c>Status_Word.5</c>, <c>Local:1:I.Data.3</c>) is read as its parent integer and bit N
/// is extracted locally — the standard Logix way to address a bit of a SINT/INT/DINT.
/// </summary>
public sealed partial record TagAddress(string ReadName, TagDataType DataType, int? Bit)
{
    [GeneratedRegex(@"^(?<base>.+)\.(?<bit>\d{1,2})$")]
    private static partial Regex BitSuffix();

    public static TagAddress Parse(string tag, TagDataType dataType)
    {
        var name = (tag ?? "").Trim();
        if (dataType == TagDataType.Bool)
        {
            var m = BitSuffix().Match(name);
            if (m.Success && int.TryParse(m.Groups["bit"].Value, out var bit) && bit <= 63)
            {
                return new TagAddress(m.Groups["base"].Value, TagDataType.Bool, bit);
            }
        }

        return new TagAddress(name, dataType, null);
    }
}
