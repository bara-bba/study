using Study.Core.Expressions;
using Study.Core.Models;

namespace Study.Core.Triggers;

public enum AlarmState
{
    /// <summary>Not alarming.</summary>
    Idle,

    /// <summary>Alarm is active and the sound should be playing.</summary>
    Ringing,

    /// <summary>Operator acknowledged while the condition is still met; re-arms once it clears.</summary>
    Acknowledged,
}

public enum TriggerOutcome
{
    None,

    /// <summary>The alarm just started (or restarted on a new edge).</summary>
    Fired,

    /// <summary>The level condition is no longer met (alarm may or may not still be ringing).</summary>
    Cleared,

    /// <summary>The condition cleared and the alarm stopped by itself (StopWhenCleared).</summary>
    ClearedAndStopped,
}

/// <summary>
/// Decides when a watch alarms, from successive tag values. Thread-safe: values arrive from the
/// polling task while Acknowledge comes from the UI thread.
///
/// Rules:
/// <list type="bullet">
/// <item>Edge modes fire on the transition only. The first read just sets the baseline.</item>
/// <item>Level / expression modes fire when the condition <em>becomes</em> met — including on
/// the first read. After Acknowledge the alarm stays silent while the condition is still met
/// and re-arms once it clears.</item>
/// <item>Acknowledge always stops the sound, even if the condition is still true.</item>
/// </list>
/// </summary>
public sealed class TriggerEngine
{
    private readonly object _gate = new();
    private readonly TriggerMode _mode;
    private readonly ConditionExpression? _expression;
    private readonly bool _stopWhenCleared;

    private bool _hasPrevious;
    private double _previous;
    private bool _conditionMet;

    public TriggerEngine(TriggerMode mode, string? expression = null, bool stopWhenCleared = false)
    {
        _mode = mode;
        _stopWhenCleared = stopWhenCleared && !mode.IsEdge();
        if (mode == TriggerMode.Expression)
        {
            _expression = ConditionExpression.Parse(expression ?? "");
        }
    }

    public static TriggerEngine For(WatchDefinition watch) =>
        new(watch.Mode, watch.Expression, watch.StopWhenCleared);

    public AlarmState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    private AlarmState _state = AlarmState.Idle;

    /// <summary>True while a level/expression condition is currently met.</summary>
    public bool ConditionMet
    {
        get
        {
            lock (_gate)
            {
                return _conditionMet;
            }
        }
    }

    /// <param name="value">The main tag's value.</param>
    /// <param name="combine">
    /// Combines the main trigger's signal (A) with the extra conditions; null = A alone. A is
    /// "the edge happened on this read" for edge modes and "the condition is met" otherwise.
    /// The alarm fires when the combined result becomes true. For an edge watch that means an
    /// AND-only combination fires on the edge while the others hold, exactly as before.
    /// </param>
    public TriggerOutcome Update(double value, Func<bool, bool>? combine = null)
    {
        lock (_gate)
        {
            var prev = _hasPrevious ? _previous : value;
            var first = !_hasPrevious;
            _previous = value;
            _hasPrevious = true;

            var signal = MainSignal(prev, value, first);
            var met = combine is null ? signal : combine(signal);
            return _mode.IsEdge() ? UpdateEdge(met) : UpdateLevel(met);
        }
    }
    /// <summary>Stops the sound. Returns false if nothing was ringing.</summary>
    public bool Acknowledge()
    {
        lock (_gate)
        {
            if (_state != AlarmState.Ringing)
            {
                return false;
            }

            _state = !_mode.IsEdge() && _conditionMet ? AlarmState.Acknowledged : AlarmState.Idle;
            return true;
        }
    }

    /// <summary>Forget history (e.g. after a reconnect) so a stale baseline cannot fake an edge.</summary>
    public void ResetBaseline()
    {
        lock (_gate)
        {
            _hasPrevious = false;
        }
    }

    private bool MainSignal(double prev, double value, bool first) => _mode switch
    {
        // Edges: the first read only sets the baseline.
        TriggerMode.RisingEdge => !first && prev == 0 && value != 0,
        TriggerMode.FallingEdge => !first && prev != 0 && value == 0,
        TriggerMode.WhileTrue => value != 0,
        TriggerMode.WhileFalse => value == 0,
        TriggerMode.Expression => _expression!.IsTrue(value, prev),
        _ => false,
    };

    /// <summary>Edge watches fire when the result turns true; there is no "still active" state.</summary>
    private TriggerOutcome UpdateEdge(bool met)
    {
        var wasMet = _conditionMet;
        _conditionMet = met;
        if (!met || wasMet)
        {
            return TriggerOutcome.None;
        }

        _state = AlarmState.Ringing;
        return TriggerOutcome.Fired;
    }

    private TriggerOutcome UpdateLevel(bool met)
    {
        var wasMet = _conditionMet;
        _conditionMet = met;

        if (met && !wasMet)
        {
            _state = AlarmState.Ringing;
            return TriggerOutcome.Fired;
        }

        if (!met && wasMet)
        {
            if (_state == AlarmState.Acknowledged)
            {
                _state = AlarmState.Idle;
                return TriggerOutcome.Cleared;
            }

            if (_state == AlarmState.Ringing && _stopWhenCleared)
            {
                _state = AlarmState.Idle;
                return TriggerOutcome.ClearedAndStopped;
            }

            return TriggerOutcome.Cleared;
        }

        return TriggerOutcome.None;
    }
}