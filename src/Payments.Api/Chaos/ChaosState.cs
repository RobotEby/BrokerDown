using System.Collections.Concurrent;
using Shared.Contracts;
using Shared.Infrastructure;
using Payments.Api.Infrastructure.Gateways;
using Payments.Api.Application;

namespace Payments.Api.Chaos;

// NOTE: one local target instance; distributed experiment coordination belongs in a future deployment.
public sealed class ChaosState : IDisposable
{
    private readonly System.Diagnostics.Metrics.Meter _meter = new("ChaosLab.Chaos");
    private readonly object _sync = new();
    private readonly TimeProvider _clock;
    private readonly bool _enabled;
    private readonly ILogger<ChaosState> _logger;
    private readonly Dictionary<Guid, DateTimeOffset> _finished = [];
    private readonly ConcurrentQueue<ChaosExperimentChanged> _events = new();
    private ChaosStart? _active;
    private DateTimeOffset _started;
    private CancellationTokenSource? _delayCancellation;
    private bool _killed;

    public ChaosState(IConfiguration config, IHostEnvironment environment, TimeProvider clock, ILogger<ChaosState> logger)
    {
        (_clock, _logger) = (clock, logger);
        _enabled = config.GetValue<bool>("Chaos:Enabled") && !environment.IsProduction();
        _meter.CreateObservableGauge("chaoslab.chaos.active", () => IsActive ? 1 : 0);
    }

    public bool IsActive { get { lock (_sync) return _active is { } a && a.ExpiresAt > _clock.GetUtcNow() && _delayCancellation?.IsCancellationRequested == false; } }

    public ChaosExperimentChanged Start(ChaosStart command)
    {
        lock (_sync)
        {
            Expire();
            var now = _clock.GetUtcNow();
            foreach (var id in _finished.Where(x => x.Value < now).Select(x => x.Key).ToArray()) _finished.Remove(id);
            if (_active?.ExperimentId == command.ExperimentId)
                return new(command.ExperimentId, ChaosExperimentStatus.Started, now, "Already active; TTL unchanged");
            string? reason = !_enabled || _killed ? "Chaos disabled" :
                command.ExperimentId == Guid.Empty || !Enum.IsDefined(command.Fault) ? "Invalid experiment" :
                command.ExpiresAt <= now || command.ExpiresAt > now.AddSeconds(60) ? "Expired or excessive TTL" :
                command.LatencyMilliseconds is < 1 or > 5000 ? "Invalid latency" :
                _finished.ContainsKey(command.ExperimentId) ? "Experiment already ended" :
                _active is not null ? "Another experiment is active" : null;
            if (reason is not null) return new(command.ExperimentId, ChaosExperimentStatus.Rejected, now, reason);
            _active = command;
            _started = now;
            _delayCancellation = new CancellationTokenSource(command.ExpiresAt - now, _clock);
            _logger.LogWarning("Chaos {ExperimentId} started: {Fault}, expires {ExpiresAt}", command.ExperimentId, command.Fault, command.ExpiresAt);
            ChaosTelemetry.Experiments.Add(1, new("fault", command.Fault.ToString()), new("event", "started"));
            return new(command.ExperimentId, ChaosExperimentStatus.Started, now);
        }
    }

    public ChaosExperimentChanged? Abort(ChaosAbort command)
    {
        lock (_sync)
        {
            if (command.KillSwitch) _killed = true;
            if (command.ExperimentId is { } id) _finished[id] = _clock.GetUtcNow().AddSeconds(60);
            if (_active is { } active && (command.ExperimentId is null || command.ExperimentId == active.ExperimentId))
                return Finish(ChaosExperimentStatus.Aborted, command.KillSwitch ? "Kill switch" : "Abort requested");
            return command.ExperimentId is { } requested ? new(requested, ChaosExperimentStatus.Aborted, _clock.GetUtcNow(), "No active effect") : null;
        }
    }

    public IEnumerable<ChaosExperimentChanged> DrainEvents()
    {
        lock (_sync) Expire();
        while (_events.TryDequeue(out var item)) yield return item;
    }

    public async Task ApplyAsync(CancellationToken ct)
    {
        ChaosStart? active;
        CancellationToken abort;
        lock (_sync)
        {
            Expire();
            active = _active;
            abort = _delayCancellation?.Token ?? default;
        }
        if (active is null) return;
        ct.ThrowIfCancellationRequested();
        if (active.Fault == ChaosFault.Unavailable) throw new GatewayUnavailableException();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, abort);
        try { await Task.Delay(TimeSpan.FromMilliseconds(active.LatencyMilliseconds), _clock, linked.Token); }
        catch (OperationCanceledException) when (abort.IsCancellationRequested && !ct.IsCancellationRequested) { }
    }

    private void Expire()
    {
        if (_active is { } active && (active.ExpiresAt <= _clock.GetUtcNow() || _delayCancellation?.IsCancellationRequested == true))
            _events.Enqueue(Finish(ChaosExperimentStatus.Expired, "TTL elapsed"));
    }

    private ChaosExperimentChanged Finish(ChaosExperimentStatus status, string reason)
    {
        var active = _active!;
        _active = null;
        _delayCancellation?.Cancel();
        _delayCancellation?.Dispose();
        _delayCancellation = null;
        _finished[active.ExperimentId] = _clock.GetUtcNow().AddSeconds(60);
        ChaosTelemetry.Experiments.Add(1, new("fault", active.Fault.ToString()), new("event", status.ToString().ToLowerInvariant()));
        ChaosTelemetry.Duration.Record(Math.Max(0, (_clock.GetUtcNow() - _started).TotalSeconds),
            new("fault", active.Fault.ToString()), new("outcome", status.ToString().ToLowerInvariant()));
        _logger.LogInformation("Chaos {ExperimentId} {Status}: {Reason}", active.ExperimentId, status, reason);
        return new(active.ExperimentId, status, _clock.GetUtcNow(), reason);
    }

    public void Dispose() { _meter.Dispose(); lock (_sync) { _delayCancellation?.Cancel(); _delayCancellation?.Dispose(); } }
}
