using System.Globalization;
using Study.Core.Models;

namespace Study.Core.Monitoring;

public static class ValueFormat
{
    public static string Format(double? value, TagDataType type)
    {
        if (value is not { } v)
        {
            return "—";
        }

        return type switch
        {
            TagDataType.Bool => v != 0 ? "TRUE" : "FALSE",
            TagDataType.Real => v.ToString("G7", CultureInfo.InvariantCulture),
            _ => ((long)v).ToString(CultureInfo.InvariantCulture),
        };
    }
}
