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

    /// <summary>
    /// Extra tags on the same PLC, lettered B, C, … (A is the main trigger). Empty = the main
    /// trigger alone. How they combine is <see cref="Logic"/>, or each row's
    /// <see cref="AndCondition.Join"/> when that is empty.
    /// </summary>
    public IReadOnlyList<AndCondition> AndConditions { get; init; } = [];

    /// <summary>
    /// Optional custom formula over the letters, e.g. <c>A and (B or C)</c>. Empty = row by row:
    /// each condition's join applies to everything above it.
    /// </summary>
    public string Logic { get; init; } = "";

    public bool HasCustomLogic => !string.IsNullOrWhiteSpace(Logic);

    /// <summary>The effective formula. Throws <see cref="Expressions.ExpressionException"/> on a bad custom formula.</summary>
    public Expressions.LogicFormula BuildLogic() => HasCustomLogic
        ? Expressions.LogicFormula.Parse(Logic, AndConditions.Count + 1)
        : Expressions.LogicFormula.Chain(AndConditions.Select(c => c.Join).ToList(),
            AndConditions.Select(c => c.Not).ToList());

    /// <summary>Value equality, including the condition list (a record would compare that by reference).</summary>
    public bool Equals(WatchDefinition? other) =>
        other is not null
        && Id == other.Id && Name == other.Name && PlcIp == other.PlcIp && Tag == other.Tag
        && DataType == other.DataType && Mode == other.Mode && Expression == other.Expression
        && PollMs == other.PollMs && StopWhenCleared == other.StopWhenCleared && Enabled == other.Enabled
        && Logic == other.Logic && AndConditions.SequenceEqual(other.AndConditions);

    public override int GetHashCode() =>
        HashCode.Combine(Id, Name, PlcIp, Tag, Mode, Expression, PollMs, AndConditions.Count);

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

        if (AndConditions.Count >= Expressions.LogicFormula.MaxLetters)
        {
            errors.Add($"At most {Expressions.LogicFormula.MaxLetters - 1} extra conditions (B to Z).");
        }

        for (var i = 0; i < AndConditions.Count; i++)
        {
            errors.AddRange(AndConditions[i].Validate(i));
        }

        if (HasCustomLogic)
        {
            try
            {
                Expressions.LogicFormula.Parse(Logic, AndConditions.Count + 1);
            }
            catch (Expressions.ExpressionException ex)
            {
                errors.Add($"Formula: {ex.Message}");
            }
        }

        if (Mode.IsEdge() && AndConditions.Count > 0 && TryBuildLogic() is { } logic
            && !logic.UsesLetterOnlyPositively(0))
        {
            errors.Add("With an edge trigger, A is only TRUE on the read where the edge happens, so NOT A "
                       + "or A XOR … would fire again on every other read. Use A without NOT/XOR, or pick "
                       + "Is FALSE / Falling edge on the Basic tab instead.");
        }

        return errors;
    }

    private Expressions.LogicFormula? TryBuildLogic()
    {
        try
        {
            return BuildLogic();
        }
        catch (Expressions.ExpressionException)
        {
            return null;
        }
    }
}
