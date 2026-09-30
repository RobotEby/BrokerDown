using MassTransit;
using Microsoft.EntityFrameworkCore;
using Payments.Api.Gateways;
using Shared.Contracts;

namespace Payments.Api;

public class OrderCreatedConsumer(PaymentsDb db, IPaymentGateway gateway, ILogger<OrderCreatedConsumer> log)
    : IConsumer<OrderCreated>
{
    public async Task Consume(ConsumeContext<OrderCreated> ctx)
    {
        var m = ctx.Message;

        if (await db.Payments.AnyAsync(p => p.OrderId == m.OrderId, ctx.CancellationToken))
        {
            log.LogInformation("Pedido {OrderId} já processado, ignorando", m.OrderId);
            return;
        }

        var result = await gateway.ChargeAsync(new ChargeRequest(m.OrderId, m.Amount), ctx.CancellationToken);

        db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = m.OrderId,
            Amount = m.Amount,
            Status = result.Success ? PaymentStatus.Approved : PaymentStatus.Declined,
            Gateway = result.Gateway,
            FailureReason = result.FailureReason,
            CreatedAt = DateTimeOffset.UtcNow
        });

        await ctx.Publish(new PaymentProcessed(
            m.OrderId, result.Success, result.Gateway, result.FailureReason, DateTimeOffset.UtcNow));

        await db.SaveChangesAsync(ctx.CancellationToken);
    }
}
