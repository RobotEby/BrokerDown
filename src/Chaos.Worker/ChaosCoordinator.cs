using Chaos.Worker.Application;
using Chaos.Worker.Domain;
using Chaos.Worker.Infrastructure;
using Shared.Contracts;
using Shared.Infrastructure;
using System.Diagnostics;

namespace Chaos.Worker;

public sealed class ChaosCoordinator(ExperimentState state, ChaosCommandPublisher publisher)
{
    public ExperimentSnapshot Snapshot() => state.Snapshot();
    public ExperimentRun? Current => state.Snapshot().Current;

    public ExperimentRun Request(ExperimentRequest request)
    {
        using var activity = Telemetry.Activities.StartActivity("chaos.request");
        ExperimentValidator.Validate(request);
        var run = state.Request(request);
        activity?.SetTag("chaos.experiment.id", run.ExperimentId.ToString());
        return run;
    }

    public async Task AbortAsync(bool killSwitch, CancellationToken ct)
    {
        using var activity = Telemetry.Activities.StartActivity("chaos.abort");
        var abort = state.Abort(killSwitch);
        activity?.SetTag("chaos.experiment.id", abort.ExperimentId?.ToString()).SetTag("chaos.kill_switch", killSwitch);
        await publisher.AbortAsync(abort, ct);
    }
    public void Observe(ChaosExperimentChanged change) => state.Observe(change);

    public async Task TickAsync(MetricsAssessment metrics, CancellationToken ct)
    {
        var decision = state.Tick(metrics);
        if (decision.Start is null && decision.Abort is null) return;
        using var activity = Telemetry.Activities.StartActivity("chaos.dispatch", ActivityKind.Internal, decision.Parent);
        activity?.SetTag("chaos.experiment.id", (decision.Start?.ExperimentId ?? decision.Abort?.ExperimentId)?.ToString());
        if (decision.Start is { } start) await publisher.StartAsync(start, ct);
        if (decision.Abort is { } abort) await publisher.AbortAsync(abort, ct);
    }
}
