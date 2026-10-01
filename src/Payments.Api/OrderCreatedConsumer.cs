using Payments.Api.Domain;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Payments.Api.Infrastructure.Gateways;
using Payments.Api.Application;
using Shared.Contracts;

namespace Payments.Api;

public class OrderCreatedConsumer(PaymentsDb db, IPaymentGateway gateway, ILogger<OrderCreatedConsumer> log)
    : IConsumer<OrderCreated>
{
    public async Task Consume(ConsumeContext<OrderCreated> ctx)
    {
        var m = ctx.Message;
        System.Diagnostics.Activity.Current?.SetTag("order.id", m.OrderId.ToString());
        using var scope = log.BeginScope(new Dictionary<string, object?>
        { ["OrderId"] = m.OrderId, ["MessageId"] = ctx.MessageId, ["CorrelationId"] = ctx.CorrelationId });
        if (m.OrderId == Guid.Empty || m.CustomerId == Guid.Empty || !Money.IsValid(m.Amount))
            throw new ArgumentException("OrderCreated contains an invalid customer, order or monetary amount");

        if (await db.Payments.SingleOrDefaultAsync(p => p.OrderId == m.OrderId, ctx.CancellationToken) is { } existing)
        {
            if (existing.Amount != m.Amount) throw new ArgumentException("OrderId was reused with a different amount");
            log.LogInformation("Order {OrderId} already processed, ignoring", m.OrderId);
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
            m.OrderId, result.Success, result.Gateway, result.FailureReason, DateTimeOffset.UtcNow),
            context => context.CorrelationId = m.OrderId, ctx.CancellationToken);

        await db.SaveChangesAsync(ctx.CancellationToken);
        PaymentTelemetry.Completed.Add(1, new("status", result.Success ? "approved" : "declined"), new("gateway", result.Gateway.ToWireName()));
        log.LogInformation("Payment for {OrderId}: {Success} via {Gateway}", m.OrderId, result.Success, result.Gateway);
    }
}
