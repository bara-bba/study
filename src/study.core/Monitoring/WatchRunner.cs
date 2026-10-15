using Study.Core.Models;
using Study.Core.Plc;
using Study.Core.Triggers;

namespace Study.Core.Monitoring;

public enum WatchStatus
{
    Stopped,
    Pinging,
    Unreachable,
    Connecting,
    Connected,
    Error,
}

/// <summary>Snapshot raised to the UI after every state change or read.</summary>
public sealed record WatchUpdate(
    Guid WatchId,
    WatchStatus Status,
    double? Value,
    DateTime? LastRead,
    string Message,
    TriggerOutcome Outcome,
    AlarmState Alarm)
{
    /// <summary>Latest value of each extra condition's tag (same order as the definition); empty when none.</summary>
    public IReadOnlyList<double> AndValues { get; init; } = [];

    /// <summary>Whether each extra condition held on this read (same order as <see cref="AndValues"/>).</summary>
    public IReadOnlyList<bool> AndMet { get; init; } = [];
}

/// <summary>
/// Polls one watch: ping → connect → read every PollMs → feed the trigger engine.
/// On a read failure it drops the connection, waits <see cref="RetryDelay"/> and starts over
/// (re-baselining edges so a reconnect can't fake a transition).
/// </summary>
public sealed class WatchRunner
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(3);

    private readonly ITagReaderFactory _factory;
    private readonly IPinger _pinger;
    private readonly bool _skipPing;
    private readonly TimeSpan _retryDelay;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public WatchRunner(WatchDefinition watch, PlcEntry plc, ITagReaderFactory factory, IPinger pinger,
        bool skipPing, TimeSpan? retryDelay = null)
    {
        Watch = watch;
        Plc = plc;
        _factory = factory;
        _pinger = pinger;
        _skipPing = skipPing;
        _retryDelay = retryDelay ?? RetryDelay;
        try
        {
            Engine = TriggerEngine.For(watch);
            _andEvaluators = watch.AndConditions.Select(c => new AndConditionEvaluator(c)).ToList();
            _logic = watch.AndConditions.Count == 0 ? null : watch.BuildLogic();
        }
        catch (Core.Expressions.ExpressionException ex)
        {
            // A hand-edited watches.json can carry a bad expression or formula. Keep a harmless
            // engine so Acknowledge still works, and report the problem from the run loop.
            Engine = new TriggerEngine(TriggerMode.WhileTrue);
            _andEvaluators = [];
            _setupError = ex.Message;
        }
    }

    private readonly List<AndConditionEvaluator> _andEvaluators;
    private readonly Core.Expressions.LogicFormula? _logic;
    private readonly string? _setupError;

    public WatchDefinition Watch { get; }

    public PlcEntry Plc { get; }

    public TriggerEngine Engine { get; }

    public WatchStatus Status { get; private set; } = WatchStatus.Stopped;

    /// <summary>Raised from a background thread — marshal to the UI before touching controls.</summary>
    public event Action<WatchUpdate>? Updated;

    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => RunAsync(ct), ct);
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_loop is not null)
            {
                await _loop.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
        _cts = null;
        Publish(WatchStatus.Stopped, null, null, "Stopped", TriggerOutcome.None);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var poll = TimeSpan.FromMilliseconds(Math.Clamp(Watch.PollMs, WatchDefinition.MinPollMs, WatchDefinition.MaxPollMs));
        if (_setupError is not null)
        {
            // Retrying cannot fix a bad definition: report it once and wait to be stopped.
            Publish(WatchStatus.Error, null, null, $"Formula or expression: {_setupError}", TriggerOutcome.None);
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return;
        }

        var address = TagAddress.Parse(Watch.Tag, Watch.DataType);

        while (!ct.IsCancellationRequested)
        {
            if (!_skipPing)
            {
                Publish(WatchStatus.Pinging, null, null, $"Pinging {Plc.Ip}…", TriggerOutcome.None);
                if (!await _pinger.PingAsync(Plc.Ip, PingTimeout, ct).ConfigureAwait(false))
                {
                    Publish(WatchStatus.Unreachable, null, null, $"No ping reply from {Plc.Ip}", TriggerOutcome.None);
                    await Task.Delay(_retryDelay, ct).ConfigureAwait(false);
                    continue;
                }
            }

            using var reader = _factory.Create(Plc, address, ReadTimeout);
            var andReaders = new List<ITagReader>();
            try
            {
                foreach (var c in Watch.AndConditions)
                {
                    andReaders.Add(_factory.Create(Plc, TagAddress.Parse(c.Tag, c.DataType), ReadTimeout));
                }

                Publish(WatchStatus.Connecting, null, null, $"Connecting to slot {Plc.Slot}…", TriggerOutcome.None);
                await reader.InitializeAsync(ct).ConfigureAwait(false);
                foreach (var r in andReaders)
                {
                    await r.InitializeAsync(ct).ConfigureAwait(false);
                }

                Engine.ResetBaseline();
                _andEvaluators.ForEach(e => e.ResetBaseline());

                while (!ct.IsCancellationRequested)
                {
                    var started = DateTime.Now;
                    var value = await reader.ReadAsync(ct).ConfigureAwait(false);
                    var andValues = new double[andReaders.Count];
                    var andMet = new bool[andReaders.Count];
                    for (var i = 0; i < andReaders.Count; i++)
                    {
                        andValues[i] = await andReaders[i].ReadAsync(ct).ConfigureAwait(false);
                        andMet[i] = _andEvaluators[i].IsMet(andValues[i]);
                    }

                    var outcome = _logic is null
                        ? Engine.Update(value)
                        : Engine.Update(value, a => _logic.Evaluate([a, .. andMet]));
                    Publish(WatchStatus.Connected, value, started, "OK", outcome, andValues, andMet);

                    var wait = poll - (DateTime.Now - started);
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // PlcException carries a friendly message; anything else is unexpected but must
                // not kill the loop silently — show it and retry.
                var msg = ex is PlcException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
                Publish(WatchStatus.Error, null, null, msg, TriggerOutcome.None);
                await Task.Delay(_retryDelay, ct).ConfigureAwait(false);
            }
            finally
            {
                andReaders.ForEach(r => r.Dispose());
            }
        }
    }

    private void Publish(WatchStatus status, double? value, DateTime? at, string message, TriggerOutcome outcome,
        IReadOnlyList<double>? andValues = null, IReadOnlyList<bool>? andMet = null)
    {
        Status = status;
        Updated?.Invoke(new WatchUpdate(Watch.Id, status, value, at, message, outcome, Engine.State)
        {
            AndValues = andValues ?? [],
            AndMet = andMet ?? [],
        });
    }
}
