namespace Study.Core.Models;

/// <summary>One monitored tag: which PLC, which tag, and what makes it alarm.</summary>
public sealed record WatchDefinition
{
    public const int MinPollMs = 100;
    public const int MaxPollMs = 3_600_000;

    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Friendly label shown in the list and the alarm banner.</summary>
    public string Name { get; init; } = "";

    /// <summary>IP of the PLC; resolved against the PLC list (name + slot) at run time.</summary>
    public string PlcIp { get; init; } = "";

    /// <summary>Logix tag name, e.g. <c>Estop_Active</c>, <c>Program:Main.Fault</c>, <c>MyDint.5</c>.</summary>
    public string Tag { get; init; } = "";

    public TagDataType DataType { get; init; } = TagDataType.Bool;

    public TriggerMode Mode { get; init; } = TriggerMode.RisingEdge;

    /// <summary>Only used when <see cref="Mode"/> is <see cref="TriggerMode.Expression"/>.</summary>
    public string Expression { get; init; } = "";

    public int PollMs { get; init; } = 1000;

    /// <summary>
    /// For level / expression modes: silence the alarm automatically when the condition is no
    /// longer met. When false the alarm keeps sounding until you press Acknowledge.
    /// </summary>
    public bool StopWhenCleared { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Returns a list of human-readable problems; empty when the watch is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(PlcIp))
        {
            errors.Add("Pick a PLC.");
        }

        if (string.IsNullOrWhiteSpace(Tag))
        {
            errors.Add("Enter a tag name.");
        }

        if (PollMs is < MinPollMs or > MaxPollMs)
        {
            errors.Add($"Polling time must be between {MinPollMs} ms and {MaxPollMs / 1000} s.");
        }

        if (Mode == TriggerMode.Expression)
        {
            if (string.IsNullOrWhiteSpace(Expression))
            {
                errors.Add("Enter an expression, e.g. x > 100.");
            }
            else
            {
                try
                {
                    Expressions.ConditionExpression.Parse(Expression);
                }
                catch (Expressions.ExpressionException ex)
                {
                    errors.Add($"Expression: {ex.Message}");
                }
            }
        }

        return errors;
    }
}
