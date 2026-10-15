using System.Collections.ObjectModel;
using System.Windows.Threading;
using Study.Core.Config;
using Study.Core.Logging;
using Study.Core.Models;
using Study.Core.Monitoring;
using Study.Core.Plc;
using Study.Core.Triggers;
using Study.ViewModels;

namespace Study.Services;

/// <summary>
/// Owns the PLC list, the watches and their runners, the alarm sound and the log.
/// Lives on the UI thread; runner updates are marshalled here through the dispatcher.
/// </summary>
internal sealed class MonitorService
{
    public const string WatchesFile = "watches.json";

    private readonly JsonStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly ITagReaderFactory _readerFactory = new LibPlcTagReaderFactory();
    private readonly IPinger _pinger = new IcmpPinger();
    private readonly Dictionary<Guid, WatchRunner> _runners = new();
    private readonly Dictionary<Guid, string> _lastProblem = new();
    private bool _soundAutoSilenced;

    public MonitorService(JsonStore store, StudySettings settings, Dispatcher dispatcher)
    {
        _store = store;
        _dispatcher = dispatcher;
        Settings = settings;
        Logger = new EventLogger(settings.LogFolder);
        Player = new AlarmPlayer();
        Player.AutoStopped += () =>
        {
            _soundAutoSilenced = true;
            Logger.Log(LogEvents.AutoStop, detail: $"Sound stopped after {Settings.AutoStopSeconds} s");
        };
    }

    public StudySettings Settings { get; private set; }

    public EventLogger Logger { get; }

    public AlarmPlayer Player { get; }

    public IReadOnlyList<PlcEntry> Plcs { get; private set; } = [];

    /// <summary>Snapshot mode only: shows <paramref name="plcs"/> without saving anything.</summary>
    public void UseInMemoryPlcs(IEnumerable<PlcEntry> plcs) => Plcs = PlcTree.Sort(plcs);

    public ObservableCollection<WatchItem> Watches { get; } = new();

    public bool Paused { get; private set; }

    /// <summary>Raised whenever the set of ringing watches changes (banner, tray icon).</summary>
    public event Action? AlarmsChanged;

    /// <summary>Raised when an alarm fires: (title, message) for a tray balloon.</summary>
    public event Action<string, string>? AlarmFired;

    public IEnumerable<WatchItem> Ringing => Watches.Where(w => w.IsRinging);

    // ----------------------------------------------------------------- load / save

    /// <summary>Loads PLCs + watches. Returns a problem message if a file was unreadable.</summary>
    public string? Load()
    {
        var problems = new List<string>();
        try
        {
            var json = _store.LoadText(PlcList.FileName);
            Plcs = json is null ? [] : PlcTree.Parse(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            problems.Add($"{_store.PathFor(PlcList.FileName)} is not valid: {ex.Message}");
        }

        try
        {
            foreach (var def in _store.Load(WatchesFile, () => new List<WatchDefinition>()))
            {
                Watches.Add(new WatchItem(def, PlcLabel(def.PlcIp)));
            }
        }
        catch (ConfigException ex)
        {
            problems.Add(ex.Message);
        }

        return problems.Count == 0 ? null : string.Join("\n", problems);
    }

    public void SavePlcs(IReadOnlyList<PlcEntry> plcs)
    {
        Plcs = PlcTree.Sort(plcs);
        _store.SaveText(PlcList.FileName, PlcTree.Serialize(Plcs));
        foreach (var item in Watches)
        {
            item.PlcName = PlcLabel(item.Definition.PlcIp);
        }

        // A changed slot only takes effect on a fresh connection.
        RestartAll();
    }

    private void SaveWatches() => _store.Save(WatchesFile, Watches.Select(w => w.Definition).ToList());

    public PlcEntry? FindPlc(string ip) => Plcs.FirstOrDefault(p => p.Ip == ip);

    private string PlcLabel(string ip) => FindPlc(ip) is { } p && !string.IsNullOrWhiteSpace(p.Name) ? p.Name : ip;

    // --------------------------------------------------------------------- watches

    public void Upsert(WatchDefinition def)
    {
        var existing = Watches.FirstOrDefault(w => w.Id == def.Id);
        if (existing is null)
        {
            existing = new WatchItem(def, PlcLabel(def.PlcIp));
            Watches.Add(existing);
        }
        else
        {
            existing.Definition = def;
            existing.PlcName = PlcLabel(def.PlcIp);
        }

        SaveWatches();
        Restart(existing);
    }

    public void Remove(WatchItem item)
    {
        _ = StopRunnerAsync(item);
        Watches.Remove(item);
        SaveWatches();
        RefreshAlarms();
    }

    public void SetEnabled(WatchItem item, bool enabled)
    {
        item.Definition = item.Definition with { Enabled = enabled };
        SaveWatches();
        Restart(item);
    }

    // ------------------------------------------------------------------- run state

    public void StartAll()
    {
        Paused = false;
        Logger.Log(LogEvents.Started, detail: $"{Watches.Count(w => w.IsEnabled)} watch(es)");
        foreach (var item in Watches)
        {
            Restart(item);
        }
    }

    public async Task PauseAsync()
    {
        Paused = true;
        await Task.WhenAll(Watches.ToList().Select(StopRunnerAsync));
        Logger.Log(LogEvents.Stopped, detail: "Monitoring paused");
        RefreshAlarms();
    }

    public async Task ShutdownAsync()
    {
        Player.Stop();
        await Task.WhenAll(Watches.ToList().Select(StopRunnerAsync));
        Logger.Log(LogEvents.Stopped, detail: "study closed");
    }

    public void ApplySettings(StudySettings settings)
    {
        var restart = settings.SkipPing != Settings.SkipPing;
        Settings = settings;
        Logger.ChangeFolder(settings.LogFolder);
        if (restart)
        {
            RestartAll();
        }
    }

    private void RestartAll()
    {
        foreach (var item in Watches)
        {
            Restart(item);
        }
    }

    private async void Restart(WatchItem item)
    {
        await StopRunnerAsync(item);
        if (Paused || !item.IsEnabled)
        {
            item.Status = WatchStatus.Stopped;
            item.Message = Paused ? "Monitoring paused" : "Disabled";
            return;
        }

        var plc = FindPlc(item.Definition.PlcIp)
                  ?? new PlcEntry { Ip = item.Definition.PlcIp, Name = item.Definition.PlcIp };
        WatchRunner runner;
        try
        {
            runner = new WatchRunner(item.Definition, plc, _readerFactory, _pinger, Settings.SkipPing);
        }
        catch (Core.Expressions.ExpressionException ex)
        {
            item.Status = WatchStatus.Error;
            item.Message = $"Expression: {ex.Message}";
            return;
        }

        runner.Updated += u => _dispatcher.BeginInvoke(() => OnUpdate(item, runner, u));
        _runners[item.Id] = runner;
        item.Value = null;
        item.Alarm = AlarmState.Idle;
        item.CommAlarm = false;
        runner.Start();
        RefreshAlarms();
    }

    private async Task StopRunnerAsync(WatchItem item)
    {
        if (_runners.Remove(item.Id, out var runner))
        {
            await runner.StopAsync();
        }
    }

    // --------------------------------------------------------------------- updates

    private void OnUpdate(WatchItem item, WatchRunner runner, WatchUpdate u)
    {
        // Ignore late events from a runner that has since been replaced or stopped.
        if (!_runners.TryGetValue(item.Id, out var current) || current != runner)
        {
            return;
        }

        var def = item.Definition;
        var plc = runner.Plc;
        var previous = item.Status;
        item.Status = u.Status;
        item.Message = u.Message;
        item.Alarm = u.Alarm;
        if (u.Value is not null)
        {
            item.Value = u.Value;
            item.LastRead = u.LastRead;
        }

        var valueText = ValueFormat.Format(u.Value, def.DataType);
        void Log(string evt, string detail = "") =>
            Logger.Log(evt, item.Title, plc.Name, plc.Ip, def.Tag, u.Value is null ? "" : valueText, detail);

        if (u.Status != previous)
        {
            switch (u.Status)
            {
                case WatchStatus.Connected:
                    _lastProblem.Remove(item.Id);
                    Log(LogEvents.Connected, $"slot {plc.Slot}");
                    break;
                case WatchStatus.Unreachable or WatchStatus.Error:
                    // A dead PLC retries every few seconds; log a problem once until it changes.
                    if (!_lastProblem.TryGetValue(item.Id, out var last) || last != u.Message)
                    {
                        _lastProblem[item.Id] = u.Message;
                        Log(u.Status == WatchStatus.Unreachable ? LogEvents.Unreachable : LogEvents.ReadError, u.Message);
                    }

                    break;
            }

            var lost = previous == WatchStatus.Connected && u.Status is WatchStatus.Unreachable or WatchStatus.Error;
            if (lost && Settings.AlarmOnConnectionLost && !item.CommAlarm)
            {
                item.CommAlarm = true;
                _soundAutoSilenced = false;
                AlarmFired?.Invoke($"Connection lost: {item.Title}", $"{plc.DisplayName}: {u.Message}");
            }
        }

        switch (u.Outcome)
        {
            case TriggerOutcome.Fired:
                Log(LogEvents.Trigger, def.Mode.Describe(def.Expression));
                _soundAutoSilenced = false;
                AlarmFired?.Invoke($"ALARM: {item.Title}", $"{plc.DisplayName}  {def.Tag} = {valueText}");
                break;
            case TriggerOutcome.Cleared:
                Log(LogEvents.Cleared, "Condition no longer met");
                break;
            case TriggerOutcome.ClearedAndStopped:
                Log(LogEvents.Cleared, "Condition no longer met; alarm stopped automatically");
                break;
        }

        RefreshAlarms();
    }

    // ---------------------------------------------------------------------- alarms

    public void Acknowledge(WatchItem item)
    {
        var acked = item.CommAlarm;
        item.CommAlarm = false;
        if (_runners.TryGetValue(item.Id, out var runner) && runner.Engine.Acknowledge())
        {
            acked = true;
            item.Alarm = runner.Engine.State;
        }

        if (acked)
        {
            var plc = FindPlc(item.Definition.PlcIp);
            Logger.Log(LogEvents.Ack, item.Title, plc?.Name ?? "", item.Definition.PlcIp, item.Definition.Tag,
                item.ValueText, item.Alarm == AlarmState.Acknowledged ? "Acknowledged; condition still active" : "Acknowledged");
        }

        RefreshAlarms();
    }

    public void AcknowledgeAll()
    {
        foreach (var item in Ringing.ToList())
        {
            Acknowledge(item);
        }
    }

    private void RefreshAlarms()
    {
        var any = Watches.Any(w => w.IsRinging);
        if (any && !_soundAutoSilenced)
        {
            Player.Start(Settings);
        }
        else if (!any)
        {
            Player.Stop();
            _soundAutoSilenced = false;
        }

        AlarmsChanged?.Invoke();
    }
}
