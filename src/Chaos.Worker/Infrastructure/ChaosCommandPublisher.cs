using MassTransit;
using Shared.Contracts;

namespace Chaos.Worker.Infrastructure;

public sealed class ChaosCommandPublisher(IBus bus, TimeProvider clock, ILogger<ChaosCommandPublisher> logger)
{
    public async Task StartAsync(ChaosStart start, CancellationToken ct)
    {
        var ttl = start.ExpiresAt - clock.GetUtcNow();
        if (ttl <= TimeSpan.Zero) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await bus.Publish(start, x => { x.TimeToLive = ttl; x.CorrelationId = start.ExperimentId; }, timeout.Token);
            logger.LogInformation("Chaos {ExperimentId} dispatched: {Fault}", start.ExperimentId, start.Fault);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        { logger.LogWarning(ex, "Chaos {ExperimentId} dispatch unconfirmed; awaiting ACK or TTL", start.ExperimentId); }
    }

    public async Task AbortAsync(ChaosAbort abort, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { await bus.Publish(abort, x => x.CorrelationId = abort.ExperimentId, timeout.Token); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        { logger.LogWarning(ex, "Abort pending for {ExperimentId}; target TTL remains enforced", abort.ExperimentId); }
    }
}
