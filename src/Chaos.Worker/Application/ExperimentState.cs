using System.Diagnostics;
using Chaos.Worker.Domain;
using Shared.Contracts;
using Shared.Infrastructure;

namespace Chaos.Worker.Application;

// NOTE: one worker instance and one active experiment; state is deliberately process-local.
public sealed class ExperimentState
{
    private readonly object _sync = new();
    private readonly ChaosOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ExperimentState> _logger;
    private readonly bool _enabled;
    private bool _killed;
    private ExperimentRun? _current;
    private MetricsAssessment? _metrics;
    private DateTimeOffset _cooldownUntil;
    private ActivityContext _requestContext;

    public ExperimentState(ChaosOptions options, ChaosSafetyPolicy safety, IHostEnvironment environment, TimeProvider clock, ILogger<ExperimentState> logger)
    {
        (_options, _clock, _logger) = (options, clock, logger);
        _enabled = safety.IsEnabled(environment.EnvironmentName);
        _cooldownUntil = clock.GetUtcNow().AddSeconds(options.CooldownSeconds);
    }

    public ExperimentSnapshot Snapshot()
    {
        lock (_sync) return new(_enabled, _killed, _cooldownUntil, _current, _metrics);
    }

    public ExperimentRun Request(ExperimentRequest request)
    {
        lock (_sync)
        {
            if (!_enabled || _killed) throw new InvalidOperationException("Chaos is disabled");
            if (_current is { } current && !Terminal(current.Status)) throw new InvalidOperationException("Another experiment is pending or active");
            if (_clock.GetUtcNow() < _cooldownUntil) throw new InvalidOperationException("Cooldown is active");
            _requestContext = Activity.Current?.Context ?? default;
            return _current = new(Guid.NewGuid(), request.Fault, request.DurationSeconds, request.LatencyMilliseconds,
                ExperimentStatus.Waiting, _clock.GetUtcNow());
        }
    }

    public ChaosAbort Abort(bool killSwitch)
    {
        Guid? id;
        lock (_sync)
        {
            _killed |= killSwitch;
            id = _current?.ExperimentId;
            if (_current is { } current && !Terminal(current.Status))
            {
                if (current.Status == ExperimentStatus.Waiting) End(ExperimentStatus.Aborted, "Cancelled before dispatch");
                else _current = current with { Status = ExperimentStatus.AbortRequested, Reason = killSwitch ? "Kill switch" : "Abort requested" };
            }
        }
        return new(killSwitch ? null : id, killSwitch);
    }

    public void Observe(ChaosExperimentChanged change)
    {
        if (!Enum.IsDefined(change.Status) || change.Status == ChaosExperimentStatus.Unknown)
            throw new ArgumentException("Invalid chaos notification status");
        lock (_sync)
        {
            if (_current is not { } current || current.ExperimentId != change.ExperimentId || Terminal(current.Status)) return;
            if (change.Status == ChaosExperimentStatus.Started && current.Status == ExperimentStatus.Starting)
            {
                if (_clock.GetUtcNow() >= current.ExpiresAt) End(ExperimentStatus.Expired, "Target TTL elapsed before acknowledgement");
                else _current = current with { Status = ExperimentStatus.Active };
            }
            else if (change.Status == ChaosExperimentStatus.Aborted) End(ExperimentStatus.Aborted, change.Reason);
            else if (change.Status == ChaosExperimentStatus.Expired) End(ExperimentStatus.Expired, change.Reason);
            else if (change.Status == ChaosExperimentStatus.Rejected) End(ExperimentStatus.Rejected, change.Reason);
        }
    }

    public (ChaosStart? Start, ChaosAbort? Abort, ActivityContext Parent) Tick(MetricsAssessment metrics)
    {
        ChaosStart? start = null;
        ChaosAbort? abort = null;
        lock (_sync)
        {
            _metrics = metrics;
            if (_current is not { } current || Terminal(current.Status)) return default;
            var now = _clock.GetUtcNow();
            if (current.Status == ExperimentStatus.Waiting)
            {
                if (now >= current.RequestedAt.AddSeconds(_options.WaitSeconds)) End(ExperimentStatus.Rejected, metrics.Reason);
                else if (metrics.Healthy && metrics.EnoughTraffic && !_killed)
                {
                    _current = current with { Status = ExperimentStatus.Starting, DispatchedAt = now, ExpiresAt = now.AddSeconds(current.DurationSeconds) };
                    start = new(current.ExperimentId, current.Fault, _current.ExpiresAt.Value, current.LatencyMilliseconds);
                }
            }
            else if (now >= current.ExpiresAt)
                End(ExperimentStatus.Expired, "Target TTL elapsed; no further effect is permitted");
            else if (!metrics.Healthy || _killed || current.Status == ExperimentStatus.AbortRequested ||
                (current.Status == ExperimentStatus.Starting && now >= current.DispatchedAt!.Value.AddSeconds(_options.AcknowledgementSeconds)))
            {
                _current = current with { Status = ExperimentStatus.AbortRequested, Reason = !metrics.Healthy ? metrics.Reason : "Abort or acknowledgement timeout" };
                abort = new(current.ExperimentId, _killed);
            }
            return (start, abort, _requestContext);
        }
    }

    private void End(ExperimentStatus status, string? reason)
    {
        _current = _current! with { Status = status, Reason = reason };
        _cooldownUntil = _clock.GetUtcNow().AddSeconds(_options.CooldownSeconds);
        _logger.LogInformation("Chaos {ExperimentId} {Status}: {Reason}", _current.ExperimentId, status, reason);
        ChaosTelemetry.Experiments.Add(1, new("fault", _current.Fault.ToString()), new("event", status.ToString().ToLowerInvariant()));
    }
    private static bool Terminal(ExperimentStatus status) => status is ExperimentStatus.Aborted or ExperimentStatus.Expired or ExperimentStatus.Rejected;
}
