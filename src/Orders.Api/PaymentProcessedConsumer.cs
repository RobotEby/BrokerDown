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
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == m.OrderId, ctx.CancellationToken);

        if (order is null || order.Status != OrderStatus.Pending) return;

        order.Status = m.Success ? OrderStatus.Paid : OrderStatus.PaymentFailed;
        order.FailureReason = m.FailureReason;
        await db.SaveChangesAsync(ctx.CancellationToken);

        log.LogInformation("Pedido {OrderId} -> {Status} (gateway {Gateway})", order.Id, order.Status, m.Gateway);
    }
}
