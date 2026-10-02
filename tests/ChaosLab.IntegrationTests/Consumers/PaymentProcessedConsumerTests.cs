extern alias OrdersApi;

using ChaosLab.IntegrationTests.Infrastructure;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrdersApi::Orders.Api;
using OrdersApi::Orders.Api.Domain;
using Shared.Contracts;
using Shouldly;
using Xunit;

namespace ChaosLab.IntegrationTests.Consumers;

[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Integration")]
public class PaymentProcessedConsumerTests : IAsyncLifetime
{
    private readonly InfrastructureFixture _infra;
    private OrdersConsumerHarness _harness = null!;

    public PaymentProcessedConsumerTests(InfrastructureFixture infra) => _infra = infra;

    public async Task InitializeAsync()
    {
        var connectionString = ConnectionStrings.ForDatabase(_infra.Sql.GetConnectionString(), DbNames.New("Orders"));
        _harness = await OrdersConsumerHarness.StartAsync(connectionString);
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    private async Task<Order> SeedOrderAsync(OrderStatus status)
    {
        using var scope = _harness.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDb>();
        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            Amount = 149.90m,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private async Task<Order> ReloadAsync(Guid id)
    {
        using var scope = _harness.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OrdersDb>().Orders.SingleAsync(o => o.Id == id);
    }

    [Fact]
    public async Task ConcurrentTerminalResultsAndRedeliveryCannotOverwriteTheWinner()
    {
        var order = await SeedOrderAsync(OrderStatus.Pending);
        var approved = new PaymentProcessed(order.Id, true, PaymentGateway.Primary, null, DateTimeOffset.UtcNow);
        var declined = approved with { Success = false, FailureReason = "declined" };
        await Task.WhenAll(_harness.Harness.Bus.Publish(approved), _harness.Harness.Bus.Publish(declined));
        await Eventually.Until(async () => (await ReloadAsync(order.Id)).Status != OrderStatus.Pending);
        await _harness.Harness.InactivityTask;
        var winner = await ReloadAsync(order.Id);
        await Task.WhenAll(_harness.Harness.Bus.Publish(approved), _harness.Harness.Bus.Publish(declined));
        await _harness.Harness.InactivityTask;
        var final = await ReloadAsync(order.Id);
        final.Status.ShouldBe(winner.Status);
        final.FailureReason.ShouldBe(winner.FailureReason);
        if (final.Status == OrderStatus.Paid) final.FailureReason.ShouldBeNull();
        else final.FailureReason.ShouldBe("declined");
    }

    [Fact]
    [Trait("Scenario", "ORD-05")]
    public async Task Consume_SuccessfulPayment_OrderBecomesPaid()
    {
        var order = await SeedOrderAsync(OrderStatus.Pending);

        await _harness.Harness.Bus.Publish(new PaymentProcessed(order.Id, true, PaymentGateway.Primary, null, DateTimeOffset.UtcNow));

        (await _harness.Harness.Consumed.Any<PaymentProcessed>(m => m.Context.Message.OrderId == order.Id))
            .ShouldBeTrue();
        await _harness.Harness.InactivityTask;

        (await ReloadAsync(order.Id)).Status.ShouldBe(OrderStatus.Paid);
    }

    [Fact]
    [Trait("Scenario", "ORD-06")]
    public async Task Consume_FailedPayment_OrderBecomesPaymentFailedWithReason()
    {
        var order = await SeedOrderAsync(OrderStatus.Pending);

        await _harness.Harness.Bus.Publish(
            new PaymentProcessed(order.Id, false, PaymentGateway.Primary, "Card declined", DateTimeOffset.UtcNow));

        (await _harness.Harness.Consumed.Any<PaymentProcessed>(m => m.Context.Message.OrderId == order.Id))
            .ShouldBeTrue();
        await _harness.Harness.InactivityTask;

        var updated = await ReloadAsync(order.Id);
        updated.Status.ShouldBe(OrderStatus.PaymentFailed);
        updated.FailureReason.ShouldBe("Card declined");
    }

    [Fact]
    [Trait("Scenario", "ORD-07")]
    public async Task Consume_OrderNotPending_IsIgnored()
    {
        var order = await SeedOrderAsync(OrderStatus.Paid);

        await _harness.Harness.Bus.Publish(
            new PaymentProcessed(order.Id, false, PaymentGateway.Primary, "Late duplicate", DateTimeOffset.UtcNow));

        (await _harness.Harness.Consumed.Any<PaymentProcessed>(m => m.Context.Message.OrderId == order.Id))
            .ShouldBeTrue();
        await _harness.Harness.InactivityTask;

        var updated = await ReloadAsync(order.Id);
        updated.Status.ShouldBe(OrderStatus.Paid);
        updated.FailureReason.ShouldBeNull();
    }

    [Fact]
    [Trait("Scenario", "ORD-08")]
    public async Task Consume_UnknownOrderId_IsIgnoredWithoutError()
    {
        var unknownId = Guid.NewGuid();

        await _harness.Harness.Bus.Publish(new PaymentProcessed(unknownId, true, PaymentGateway.Primary, null, DateTimeOffset.UtcNow));

        (await _harness.Harness.Consumed.Any<PaymentProcessed>(m => m.Context.Message.OrderId == unknownId))
            .ShouldBeTrue();
        (await _harness.Harness.Published.Any<Fault<PaymentProcessed>>()).ShouldBeFalse();
    }
}
