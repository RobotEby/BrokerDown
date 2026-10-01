using MassTransit;
using System.Diagnostics;
using Shared.Contracts;

namespace Payments.Api.Chaos;

// Control must remain available independently of a SQL transaction or a payment's retry.
public sealed class ChaosControlConsumer(ChaosState state) : IConsumer<ChaosStart>, IConsumer<ChaosAbort>
{
    public Task Consume(ConsumeContext<ChaosStart> context)
    {
        Activity.Current?.SetTag("chaos.experiment.id", context.Message.ExperimentId.ToString());
        return context.Publish(state.Start(context.Message), c => c.CorrelationId = context.Message.ExperimentId, context.CancellationToken);
    }

    public Task Consume(ConsumeContext<ChaosAbort> context)
    {
        Activity.Current?.SetTag("chaos.experiment.id", context.Message.ExperimentId?.ToString())
            .SetTag("chaos.kill_switch", context.Message.KillSwitch);
        return state.Abort(context.Message) is { } changed
            ? context.Publish(changed, c => c.CorrelationId = changed.ExperimentId, context.CancellationToken) : Task.CompletedTask;
    }
}

public sealed class ChaosExpiryService(ChaosState state, IBus bus, ILogger<ChaosExpiryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            foreach (var changed in state.DrainEvents())
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                try { await bus.Publish(changed, c => c.CorrelationId = changed.ExperimentId, timeout.Token); }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                { logger.LogWarning(ex, "Chaos {ExperimentId} ended locally; notification unavailable", changed.ExperimentId); }
            }
    }
}
