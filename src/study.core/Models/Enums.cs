namespace Study.Core.Models;

/// <summary>Logix atomic data types study can read.</summary>
public enum TagDataType
{
    Bool,
    Sint,
    Int,
    Dint,
    Lint,
    Real,
}

/// <summary>When a watch raises its alarm.</summary>
public enum TriggerMode
{
    /// <summary>Value goes 0 → non-zero (false → true).</summary>
    RisingEdge,

    /// <summary>Value goes non-zero → 0 (true → false).</summary>
    FallingEdge,

    /// <summary>Alarm when the value becomes true; silenced after Acknowledge until it goes false again.</summary>
    WhileTrue,

    /// <summary>Alarm when the value becomes false; silenced after Acknowledge until it goes true again.</summary>
    WhileFalse,

    /// <summary>Alarm when a math/logic expression on <c>x</c> (and <c>prev</c>) becomes true.</summary>
    Expression,
}

public static class TriggerModeExtensions
{
    /// <summary>Edge modes fire on a transition; there is no "still true" state to wait out.</summary>
    public static bool IsEdge(this TriggerMode mode) =>
        mode is TriggerMode.RisingEdge or TriggerMode.FallingEdge;

    public static string Describe(this TriggerMode mode, string? expression = null) => mode switch
    {
        TriggerMode.RisingEdge => "Rising edge (false → true)",
        TriggerMode.FallingEdge => "Falling edge (true → false)",
        TriggerMode.WhileTrue => "Is TRUE",
        TriggerMode.WhileFalse => "Is FALSE",
        TriggerMode.Expression => $"Expression: {expression}",
        _ => mode.ToString(),
    };
}

public static class TagDataTypeExtensions
{
    /// <summary>Bytes per element on the wire (Logix BOOL is transferred as one byte).</summary>
    public static int ElementSize(this TagDataType type) => type switch
    {
        TagDataType.Bool => 1,
        TagDataType.Sint => 1,
        TagDataType.Int => 2,
        TagDataType.Dint => 4,
        TagDataType.Lint => 8,
        TagDataType.Real => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>Maps a CIP atomic type code (from the controller tag list) to a data type.</summary>
    public static TagDataType? FromCipTypeCode(ushort code) => (code & 0x00FF) switch
    {
        0xC1 => TagDataType.Bool,
        0xC2 => TagDataType.Sint,
        0xC3 => TagDataType.Int,
        0xC4 => TagDataType.Dint,
        0xC5 => TagDataType.Lint,
        0xCA => TagDataType.Real,
        _ => null,
    };
}
