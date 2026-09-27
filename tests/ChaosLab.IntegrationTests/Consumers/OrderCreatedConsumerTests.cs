extern alias PaymentsApi;

using ChaosLab.IntegrationTests.Infrastructure;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using PaymentsApi::Payments.Api;
using PaymentsApi::Payments.Api.Gateways;
using Shared.Contracts;
using Shouldly;
using Xunit;

namespace ChaosLab.IntegrationTests.Consumers;

[Collection(InfrastructureCollection.Name)]
public class OrderCreatedConsumerTests : IAsyncLifetime
{
    private readonly InfrastructureFixture _infra;
    private readonly IPaymentGateway _gateway = Substitute.For<IPaymentGateway>();
    private PaymentsConsumerHarness _harness = null!;

    public OrderCreatedConsumerTests(InfrastructureFixture infra) => _infra = infra;

    public async Task InitializeAsync()
    {
        var connectionString = ConnectionStrings.ForDatabase(_infra.Sql.GetConnectionString(), DbNames.New("Payments"));
        _harness = await PaymentsConsumerHarness.StartAsync(connectionString, _gateway);
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    [Trait("Scenario", "PAY-01")]
    public async Task Consume_GatewayApproves_StoresApprovedPaymentAndPublishesSuccess()
    {
        var orderId = Guid.NewGuid();
        _gateway.ChargeAsync(Arg.Any<ChargeRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChargeResult(true, "primary", null));

        await _harness.Harness.Bus.Publish(new OrderCreated(orderId, Guid.NewGuid(), 149.90m, DateTimeOffset.UtcNow));

        (await _harness.Harness.Published.Any<PaymentProcessed>(
            m => m.Context.Message.OrderId == orderId && m.Context.Message.Success)).ShouldBeTrue();

        using var scope = _harness.Services.CreateScope();
        var payment = await scope.ServiceProvider.GetRequiredService<PaymentsDb>()
            .Payments.SingleAsync(p => p.OrderId == orderId);

        payment.Status.ShouldBe(PaymentStatus.Approved);
        payment.Gateway.ShouldBe("primary");
        payment.FailureReason.ShouldBeNull();
    }

    [Fact]
    [Trait("Scenario", "PAY-02")]
    public async Task Consume_GatewayDeclines_StoresDeclinedPaymentAndPublishesFailure()
    {
        var orderId = Guid.NewGuid();
        _gateway.ChargeAsync(Arg.Any<ChargeRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChargeResult(false, "primary", "Card declined"));

        await _harness.Harness.Bus.Publish(new OrderCreated(orderId, Guid.NewGuid(), 149.90m, DateTimeOffset.UtcNow));

        (await _harness.Harness.Published.Any<PaymentProcessed>(
            m => m.Context.Message.OrderId == orderId && !m.Context.Message.Success)).ShouldBeTrue();

        using var scope = _harness.Services.CreateScope();
        var payment = await scope.ServiceProvider.GetRequiredService<PaymentsDb>()
            .Payments.SingleAsync(p => p.OrderId == orderId);

        payment.Status.ShouldBe(PaymentStatus.Declined);
        payment.FailureReason.ShouldBe("Card declined");
    }

    [Fact]
    [Trait("Scenario", "PAY-03")]
    public async Task Consume_OrderAlreadyHasPayment_DoesNotChargeAgain()
    {
        var orderId = Guid.NewGuid();

        using (var scope = _harness.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDb>();
            db.Payments.Add(new Payment
            {
                Id = Guid.NewGuid(),
                OrderId = orderId,
                Amount = 149.90m,
                Status = PaymentStatus.Approved,
                Gateway = "primary",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await _harness.Harness.Bus.Publish(new OrderCreated(orderId, Guid.NewGuid(), 149.90m, DateTimeOffset.UtcNow));

        (await _harness.Harness.Consumed.Any<OrderCreated>(m => m.Context.Message.OrderId == orderId)).ShouldBeTrue();

        await _gateway.DidNotReceive().ChargeAsync(Arg.Any<ChargeRequest>(), Arg.Any<CancellationToken>());
        (await _harness.Harness.Published.Any<PaymentProcessed>(m => m.Context.Message.OrderId == orderId))
            .ShouldBeFalse();

        using var verify = _harness.Services.CreateScope();
        var count = await verify.ServiceProvider.GetRequiredService<PaymentsDb>()
            .Payments.CountAsync(p => p.OrderId == orderId);
        count.ShouldBe(1);
    }

    [Fact]
    [Trait("Scenario", "PAY-04")]
    public async Task Consume_SameMessageDeliveredTwice_IsProcessedOnce()
    {
        var orderId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        _gateway.ChargeAsync(Arg.Any<ChargeRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChargeResult(true, "primary", null));

        var message = new OrderCreated(orderId, Guid.NewGuid(), 149.90m, DateTimeOffset.UtcNow);

        await _harness.Harness.Bus.Publish(message, ctx => ctx.MessageId = messageId);
        await _harness.Harness.Bus.Publish(message, ctx => ctx.MessageId = messageId);

        (await _harness.Harness.Published.Any<PaymentProcessed>(m => m.Context.Message.OrderId == orderId))
            .ShouldBeTrue();
        await Settle.Briefly(); // give a would-be duplicate a chance to arrive and be filtered

        await _gateway.Received(1).ChargeAsync(Arg.Any<ChargeRequest>(), Arg.Any<CancellationToken>());

        using var scope = _harness.Services.CreateScope();
        var count = await scope.ServiceProvider.GetRequiredService<PaymentsDb>()
            .Payments.CountAsync(p => p.OrderId == orderId);
        count.ShouldBe(1);
    }

    [Fact]
    [Trait("Scenario", "PAY-05")]
    public async Task Consume_CommitFails_NeitherPaymentNorEventIsPersisted()
    {
        var orderId = Guid.NewGuid();
        _gateway.ChargeAsync(Arg.Any<ChargeRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChargeResult(true, "primary", null));
        _harness.Interceptor.ShouldFail = true;

        await _harness.Harness.Bus.Publish(new OrderCreated(orderId, Guid.NewGuid(), 149.90m, DateTimeOffset.UtcNow));

        (await _harness.Harness.Published.Any<Fault<OrderCreated>>()).ShouldBeTrue();
        (await _harness.Harness.Published.Any<PaymentProcessed>(m => m.Context.Message.OrderId == orderId))
            .ShouldBeFalse();

        using var scope = _harness.Services.CreateScope();
        var count = await scope.ServiceProvider.GetRequiredService<PaymentsDb>()
            .Payments.CountAsync(p => p.OrderId == orderId);
        count.ShouldBe(0);
    }
}
