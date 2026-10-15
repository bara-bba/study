using System.ComponentModel;
using System.Runtime.CompilerServices;
using Study.Core.Models;
using Study.Core.Monitoring;
using Study.Core.Triggers;

namespace Study.ViewModels;

/// <summary>One row in the main window: the definition plus its live state.</summary>
internal sealed class WatchItem : INotifyPropertyChanged
{
    private WatchDefinition _definition;
    private string _plcName = "";
    private WatchStatus _status = WatchStatus.Stopped;
    private double? _value;
    private DateTime? _lastRead;
    private string _message = "Stopped";
    private AlarmState _alarm = AlarmState.Idle;
    private bool _commAlarm;

    public WatchItem(WatchDefinition definition, string plcName)
    {
        _definition = definition;
        _plcName = plcName;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public WatchDefinition Definition
    {
        get => _definition;
        set
        {
            _definition = value;
            OnChanged();
            OnChanged(nameof(Title));
            OnChanged(nameof(TagText));
            OnChanged(nameof(ConditionText));
            OnChanged(nameof(Mode));
            OnChanged(nameof(ValueText));
            OnChanged(nameof(IsEnabled));
        }
    }

    public Guid Id => _definition.Id;

    public string Title => string.IsNullOrWhiteSpace(_definition.Name) ? _definition.Tag : _definition.Name;

    public string PlcName
    {
        get => _plcName;
        set
        {
            _plcName = value;
            OnChanged();
            OnChanged(nameof(TagText));
        }
    }

    public string TagText => $"{_plcName}  ·  {_definition.Tag}  ({_definition.DataType.ToString().ToUpperInvariant()})";

    public string ConditionText =>
        $"{_definition.Mode.Describe(_definition.Expression)}  ·  every {FormatPoll(_definition.PollMs)}";

    public TriggerMode Mode => _definition.Mode;

    public bool IsEnabled => _definition.Enabled;

    public WatchStatus Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnChanged();
            OnChanged(nameof(StatusText));
            OnChanged(nameof(StatusColorKey));
        }
    }

    public string StatusText => _status switch
    {
        WatchStatus.Connected => "Connected",
        WatchStatus.Pinging => "Pinging",
        WatchStatus.Connecting => "Connecting",
        WatchStatus.Unreachable => "Unreachable",
        WatchStatus.Error => "Error",
        _ => IsEnabled ? "Stopped" : "Disabled",
    };

    /// <summary>Theme brush key for the status dot.</summary>
    public string StatusColorKey => _status switch
    {
        WatchStatus.Connected => "Brush.Ok",
        WatchStatus.Pinging or WatchStatus.Connecting => "Brush.Warn",
        WatchStatus.Unreachable or WatchStatus.Error => "Brush.Bad",
        _ => "Brush.Idle",
    };

    public double? Value
    {
        get => _value;
        set
        {
            _value = value;
            OnChanged();
            OnChanged(nameof(ValueText));
        }
    }

    public string ValueText => ValueFormat.Format(_value, _definition.DataType);

    public DateTime? LastRead
    {
        get => _lastRead;
        set
        {
            _lastRead = value;
            OnChanged();
            OnChanged(nameof(LastReadText));
        }
    }

    public string LastReadText => _lastRead is { } t ? $"read {t:HH:mm:ss}" : "";

    public string Message
    {
        get => _message;
        set
        {
            _message = value;
            OnChanged();
        }
    }

    public AlarmState Alarm
    {
        get => _alarm;
        set
        {
            _alarm = value;
            OnChanged();
            OnChanged(nameof(IsRinging));
            OnChanged(nameof(AlarmBadge));
        }
    }

    /// <summary>Alarm raised because the PLC went unreachable (Settings → Alarm on connection lost).</summary>
    public bool CommAlarm
    {
        get => _commAlarm;
        set
        {
            _commAlarm = value;
            OnChanged();
            OnChanged(nameof(IsRinging));
            OnChanged(nameof(AlarmBadge));
        }
    }

    public bool IsRinging => _alarm == AlarmState.Ringing || _commAlarm;

    public string AlarmBadge => IsRinging ? "ALARM"
        : _alarm == AlarmState.Acknowledged ? "ACKED (still active)"
        : "";

    public static string FormatPoll(int ms) => ms < 1000 ? $"{ms} ms" : $"{ms / 1000.0:0.##} s";

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
