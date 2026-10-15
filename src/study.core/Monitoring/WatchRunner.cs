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
    AlarmState Alarm);

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
        Engine = TriggerEngine.For(watch);
    }

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
            try
            {
                Publish(WatchStatus.Connecting, null, null, $"Connecting to slot {Plc.Slot}…", TriggerOutcome.None);
                await reader.InitializeAsync(ct).ConfigureAwait(false);
                Engine.ResetBaseline();

                while (!ct.IsCancellationRequested)
                {
                    var started = DateTime.Now;
                    var value = await reader.ReadAsync(ct).ConfigureAwait(false);
                    var outcome = Engine.Update(value);
                    Publish(WatchStatus.Connected, value, started, "OK", outcome);

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
        }
    }

    private void Publish(WatchStatus status, double? value, DateTime? at, string message, TriggerOutcome outcome)
    {
        Status = status;
        Updated?.Invoke(new WatchUpdate(Watch.Id, status, value, at, message, outcome, Engine.State));
    }
}
