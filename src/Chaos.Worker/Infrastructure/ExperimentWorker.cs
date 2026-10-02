using MassTransit;
using Shared.Contracts;
using Shared.Infrastructure;

namespace Chaos.Worker.Infrastructure;

public sealed class ExperimentChangedConsumer(ChaosCoordinator coordinator) : IConsumer<ChaosExperimentChanged>
{
    public Task Consume(ConsumeContext<ChaosExperimentChanged> context)
    {
        Telemetry.EnrichMessageActivity(context);
        coordinator.Observe(context.Message);
        return Task.CompletedTask;
    }
}

public sealed class ExperimentWorker(ChaosCoordinator coordinator, PrometheusMonitor monitor, ChaosOptions options, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollSeconds), clock);
        do { await coordinator.TickAsync(await monitor.EvaluateAsync(stoppingToken), stoppingToken); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
