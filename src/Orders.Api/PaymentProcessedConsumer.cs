using MassTransit;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts;

namespace Orders.Api;

public class PaymentProcessedConsumer(OrdersDb db, ILogger<PaymentProcessedConsumer> log)
    : IConsumer<PaymentProcessed>
{
    public async Task Consume(ConsumeContext<PaymentProcessed> ctx)
    {
        var m = ctx.Message;
        var createdAt = await db.Orders.Where(o => o.Id == m.OrderId).Select(o => (DateTimeOffset?)o.CreatedAt)
            .FirstOrDefaultAsync(ctx.CancellationToken);
        var status = m.Success ? OrderStatus.Paid : OrderStatus.PaymentFailed;
        var updated = await db.Orders.Where(o => o.Id == m.OrderId && o.Status == OrderStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, status)
                .SetProperty(o => o.FailureReason, m.Success ? null : m.FailureReason), ctx.CancellationToken);
        if (updated != 0)
        {
            OrdersTelemetry.Duration.Record((DateTimeOffset.UtcNow - createdAt!.Value).TotalSeconds,
                new KeyValuePair<string, object?>("status", m.Success ? "paid" : "payment_failed"));
            log.LogInformation("Pedido {OrderId} -> {Status} (gateway {Gateway})", m.OrderId, status, m.Gateway);
        }
    }
}
