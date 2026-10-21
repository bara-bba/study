using Study.Core.Expressions;

namespace Study.Core.Models;

/// <summary>What an extra condition requires of its tag. Always a level test (no edges).</summary>
public enum AndTest
{
    IsTrue,
    IsFalse,
    Expression,
}

/// <summary>
/// An extra tag on the same PLC that is combined with the main trigger, e.g.
/// "Motor_Run is TRUE" AND "Motor_Feedback is FALSE". Read every poll alongside the main tag.
/// Named "AndCondition" for compatibility with saved watches (it started as AND-only); the
/// <see cref="Join"/> or the watch's custom formula decides how it combines.
/// </summary>
public sealed record AndCondition
{
    public string Tag { get; init; } = "";

    public TagDataType DataType { get; init; } = TagDataType.Bool;

    public AndTest Test { get; init; } = AndTest.IsTrue;

    /// <summary>Only used when <see cref="Test"/> is <see cref="AndTest.Expression"/> (x = this tag's value).</summary>
    public string Expression { get; init; } = "";

    /// <summary>
    /// How this row combines with everything above it in row-by-row mode (ignored when the watch
    /// has a custom formula). Missing in older files, which therefore load as AND.
    /// </summary>
    public LogicJoin Join { get; init; } = LogicJoin.And;

    /// <summary>
    /// Row-by-row mode only: use NOT this condition ("AND NOT", "OR NOT", "XOR NOT"). Ignored with
    /// a custom formula, where you write <c>not B</c> yourself. Missing in older files = false.
    /// </summary>
    public bool Not { get; init; }

    public string Describe() => Test switch
    {
        AndTest.IsTrue => $"{Tag} is TRUE",
        AndTest.IsFalse => $"{Tag} is FALSE",
        _ => $"{Tag}: {Expression}",
    };

    /// <summary>Problems with this condition, prefixed with its letter ("Condition C: ..."; index 0 = B).</summary>
    public IEnumerable<string> Validate(int index)
    {
        var prefix = $"Condition {LogicFormula.LetterFor(index + 1)}: ";
        if (string.IsNullOrWhiteSpace(Tag))
        {
            yield return prefix + "enter a tag name.";
        }

        if (Test != AndTest.Expression)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(Expression))
        {
            yield return prefix + "enter an expression, e.g. x > 100.";
            yield break;
        }

        string? problem = null;
        try
        {
            ConditionExpression.Parse(Expression);
        }
        catch (ExpressionException ex)
        {
            problem = ex.Message;
        }

        if (problem is not null)
        {
            yield return prefix + "expression: " + problem;
        }
    }
}

/// <summary>Evaluates one <see cref="AndCondition"/> against successive values.</summary>
public sealed class AndConditionEvaluator
{
    private readonly AndCondition _condition;
    private readonly ConditionExpression? _expression;
    private double? _previous;

    public AndConditionEvaluator(AndCondition condition)
    {
        _condition = condition;
        if (condition.Test == AndTest.Expression)
        {
            _expression = ConditionExpression.Parse(condition.Expression);
        }
    }

    public AndCondition Condition => _condition;

    public bool IsMet(double value)
    {
        var prev = _previous ?? value;
        _previous = value;
        return _condition.Test switch
        {
            AndTest.IsTrue => value != 0,
            AndTest.IsFalse => value == 0,
            _ => _expression!.IsTrue(value, prev),
        };
    }

    public void ResetBaseline() => _previous = null;
}
