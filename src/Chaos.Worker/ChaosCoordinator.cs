using MassTransit;
using Shared.Contracts;
using Shared.Infrastructure;

namespace Chaos.Worker;

public record ExperimentRequest(ChaosFault Fault, int DurationSeconds = 30, int LatencyMilliseconds = 2000);
public record ExperimentRun(Guid ExperimentId, ChaosFault Fault, int DurationSeconds, int LatencyMilliseconds,
    string Status, DateTimeOffset RequestedAt, DateTimeOffset? DispatchedAt = null, DateTimeOffset? ExpiresAt = null, string? Reason = null);

// NOTE: one worker instance and one active experiment; state is deliberately process-local.
public sealed class ChaosCoordinator
{
    private readonly object _sync = new();
    private readonly IBus _bus;
    private readonly ChaosOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ChaosCoordinator> _logger;
    private readonly bool _enabled;
    private bool _killed;
    private ExperimentRun? _current;
    private MetricsAssessment? _metrics;
    private DateTimeOffset _cooldownUntil;

    public ChaosCoordinator(IBus bus, ChaosOptions options, IHostEnvironment environment, TimeProvider clock, ILogger<ChaosCoordinator> logger)
    {
        (_bus, _options, _clock, _logger) = (bus, options, clock, logger);
        _enabled = options.Enabled && !environment.IsProduction();
        _cooldownUntil = clock.GetUtcNow().AddSeconds(options.CooldownSeconds);
    }

    public object Snapshot()
    {
        lock (_sync) return new { Enabled = _enabled, KillSwitch = _killed, CooldownUntil = _cooldownUntil,
            Current = _current, Metrics = _metrics };
    }
    public ExperimentRun? Current { get { lock (_sync) return _current; } }

    public ExperimentRun Request(ExperimentRequest request)
    {
        if (!Enum.IsDefined(request.Fault) || request.DurationSeconds is < 1 or > 60 || request.LatencyMilliseconds is < 1 or > 5000)
            throw new ArgumentException("Fault, duration (1–60s), or latency (1–5000ms) is invalid");
        lock (_sync)
        {
            if (!_enabled || _killed) throw new InvalidOperationException("Chaos is disabled");
            if (_current is { } current && !Terminal(current.Status)) throw new InvalidOperationException("Another experiment is pending or active");
            if (_clock.GetUtcNow() < _cooldownUntil) throw new InvalidOperationException("Cooldown is active");
            return _current = new(Guid.NewGuid(), request.Fault, request.DurationSeconds, request.LatencyMilliseconds,
                "waiting", _clock.GetUtcNow());
        }
    }

    public async Task AbortAsync(bool killSwitch, CancellationToken ct)
    {
        Guid? id;
        lock (_sync)
        {
            _killed |= killSwitch;
            id = _current?.ExperimentId;
            if (_current is { } current && !Terminal(current.Status))
            {
                if (current.Status == "waiting") End("aborted", "Cancelled before dispatch");
                else _current = current with { Status = "abort_requested", Reason = killSwitch ? "Kill switch" : "Abort requested" };
            }
        }
        await PublishAbortAsync(killSwitch ? null : id, killSwitch, ct);
    }

    public void Observe(ChaosExperimentChanged change)
    {
        lock (_sync)
        {
            if (_current is not { } current || current.ExperimentId != change.ExperimentId || Terminal(current.Status)) return;
            if (change.Status == "started" && current.Status == "starting") _current = current with { Status = "active" };
            else if (change.Status is "aborted" or "expired" or "rejected") End(change.Status, change.Reason);
        }
    }

    public async Task TickAsync(MetricsAssessment metrics, CancellationToken ct)
    {
        ChaosStart? start = null;
        ChaosAbort? abort = null;
        lock (_sync)
        {
            _metrics = metrics;
            if (_current is not { } current || Terminal(current.Status)) return;
            var now = _clock.GetUtcNow();
            if (current.Status == "waiting")
            {
                if (now >= current.RequestedAt.AddSeconds(_options.WaitSeconds)) End("rejected", metrics.Reason);
                else if (metrics.Healthy && metrics.EnoughTraffic && !_killed)
                {
                    _current = current with { Status = "starting", DispatchedAt = now, ExpiresAt = now.AddSeconds(current.DurationSeconds) };
                    start = new(current.ExperimentId, current.Fault, _current.ExpiresAt.Value, current.LatencyMilliseconds);
                }
            }
            else if (now >= current.ExpiresAt)
                End("expired", "Target TTL elapsed; no further effect is permitted");
            else if (!metrics.Healthy || _killed || current.Status == "abort_requested" ||
                (current.Status == "starting" && now >= current.DispatchedAt!.Value.AddSeconds(_options.AcknowledgementSeconds)))
            {
                _current = current with { Status = "abort_requested", Reason = !metrics.Healthy ? metrics.Reason : "Abort or acknowledgement timeout" };
                abort = new(current.ExperimentId, _killed);
            }
        }
        if (start is not null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await _bus.Publish(start, x => x.TimeToLive = start.ExpiresAt - _clock.GetUtcNow(), timeout.Token);
                _logger.LogInformation("Chaos {ExperimentId} dispatched: {Fault}", start.ExperimentId, start.Fault);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { _logger.LogWarning(ex, "Chaos {ExperimentId} dispatch unconfirmed; awaiting ACK or TTL", start.ExperimentId); }
        }
        if (abort is not null) await PublishAbortAsync(abort.ExperimentId, abort.KillSwitch, ct);
    }

    private async Task PublishAbortAsync(Guid? id, bool kill, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { await _bus.Publish(new ChaosAbort(id, kill), timeout.Token); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        { _logger.LogWarning(ex, "Abort pending for {ExperimentId}; target TTL remains enforced", id); }
    }

    private void End(string status, string? reason)
    {
        _current = _current! with { Status = status, Reason = reason };
        _cooldownUntil = _clock.GetUtcNow().AddSeconds(_options.CooldownSeconds);
        _logger.LogInformation("Chaos {ExperimentId} {Status}: {Reason}", _current.ExperimentId, status, reason);
        ChaosTelemetry.Experiments.Add(1, new("fault", _current.Fault.ToString()), new("event", status));
    }
    private static bool Terminal(string status) => status is "aborted" or "expired" or "rejected";
}

public sealed class ExperimentChangedConsumer(ChaosCoordinator coordinator) : IConsumer<ChaosExperimentChanged>
{
    public Task Consume(ConsumeContext<ChaosExperimentChanged> context) { coordinator.Observe(context.Message); return Task.CompletedTask; }
}

public sealed class ExperimentWorker(ChaosCoordinator coordinator, PrometheusMonitor monitor, ChaosOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollSeconds));
        do { await coordinator.TickAsync(await monitor.EvaluateAsync(stoppingToken), stoppingToken); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
