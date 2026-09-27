extern alias OrdersApi;
extern alias PaymentsApi;

using System.Net.Http.Json;
using ChaosLab.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OrdersApi::Orders.Api;
using PaymentsApi::Payments.Api.Gateways;
using Shouldly;
using Xunit;

namespace ChaosLab.IntegrationTests.Flows;

// Runs Orders.Api and Payments.Api together against the shared RabbitMQ
// container, each with its own database, the way docker-compose wires them.
[Collection(InfrastructureCollection.Name)]
public class EndToEndTests : IAsyncLifetime
{
    private readonly InfrastructureFixture _infra;
    private OrdersApiFactory _orders = null!;
    private HttpClient _ordersClient = null!;

    public EndToEndTests(InfrastructureFixture infra) => _infra = infra;

    public Task InitializeAsync()
    {
        var connectionString = ConnectionStrings.ForDatabase(_infra.Sql.GetConnectionString(), DbNames.New("Orders"));
        _orders = new OrdersApiFactory(
            connectionString, _infra.Rabbit.Hostname, _infra.Rabbit.GetMappedPublicPort(5672), "chaos", "chaos");
        _ordersClient = _orders.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _ordersClient.Dispose();
        await _orders.DisposeAsync();
    }

    private PaymentsApiFactory StartPayments(IPaymentGateway gateway)
    {
        var connectionString = ConnectionStrings.ForDatabase(_infra.Sql.GetConnectionString(), DbNames.New("Payments"));
        return new PaymentsApiFactory(
            connectionString, _infra.Rabbit.Hostname, _infra.Rabbit.GetMappedPublicPort(5672), "chaos", "chaos", gateway);
    }

    private async Task<Guid> CreateOrderAsync(decimal amount = 149.90m)
    {
        var response = await _ordersClient.PostAsJsonAsync("/orders", new CreateOrderRequest(Guid.NewGuid(), amount));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<OrderAccepted>();
        return body!.Id;
    }

    private async Task<OrderStatus> ReadStatusAsync(Guid orderId)
    {
        using var scope = _orders.Services.CreateScope();
        var order = await scope.ServiceProvider.GetRequiredService<OrdersDb>().Orders.SingleAsync(o => o.Id == orderId);
        return order.Status;
    }

    [Fact]
    [Trait("Scenario", "FLW-01")]
    public async Task Order_ApprovingGateway_EndsUpPaid()
    {
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.ChargeAsync(Arg.Any<ChargeRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChargeResult(true, "primary", null));

        await using var payments = StartPayments(gateway);
        _ = payments.CreateClient(); // touching the factory boots the host and its consumers

        var orderId = await CreateOrderAsync();

        await Eventually.Until(async () => await ReadStatusAsync(orderId) != OrderStatus.Pending,
            timeout: TimeSpan.FromSeconds(20));

        (await ReadStatusAsync(orderId)).ShouldBe(OrderStatus.Paid);
    }

    [Fact]
    [Trait("Scenario", "FLW-02")]
    public async Task Order_DecliningGateway_EndsUpPaymentFailed()
    {
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.ChargeAsync(Arg.Any<ChargeRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChargeResult(false, "primary", "Card declined"));

        await using var payments = StartPayments(gateway);
        _ = payments.CreateClient();

        var orderId = await CreateOrderAsync();

        await Eventually.Until(async () => await ReadStatusAsync(orderId) != OrderStatus.Pending,
            timeout: TimeSpan.FromSeconds(20));

        (await ReadStatusAsync(orderId)).ShouldBe(OrderStatus.PaymentFailed);
    }

    [Fact]
    [Trait("Scenario", "FLW-03")]
    public async Task Order_PaymentsApiStartsLate_StaysPendingThenBecomesPaid()
    {
        var orderId = await CreateOrderAsync();

        await Settle.Briefly();
        (await ReadStatusAsync(orderId)).ShouldBe(OrderStatus.Pending);

        var gateway = Substitute.For<IPaymentGateway>();
        gateway.ChargeAsync(Arg.Any<ChargeRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChargeResult(true, "primary", null));

        await using var payments = StartPayments(gateway);
        _ = payments.CreateClient();

        await Eventually.Until(async () => await ReadStatusAsync(orderId) == OrderStatus.Paid,
            timeout: TimeSpan.FromSeconds(20));
    }

    [Fact]
    [Trait("Scenario", "ORD-09")]
    public async Task Order_BrokerUnavailable_StillAcceptedAndDeliveredOnceBrokerReturns()
    {
        await _infra.Rabbit.StopAsync();

        Guid orderId;
        try
        {
            orderId = await CreateOrderAsync();
        }
        finally
        {
            await _infra.Rabbit.StartAsync();
        }

        (await ReadStatusAsync(orderId)).ShouldBe(OrderStatus.Pending);

        var gateway = Substitute.For<IPaymentGateway>();
        gateway.ChargeAsync(Arg.Any<ChargeRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChargeResult(true, "primary", null));

        await using var payments = StartPayments(gateway);
        _ = payments.CreateClient();

        await Eventually.Until(async () => await ReadStatusAsync(orderId) == OrderStatus.Paid,
            timeout: TimeSpan.FromSeconds(30));
    }

    private sealed record OrderAccepted(Guid Id, string Status);
}
