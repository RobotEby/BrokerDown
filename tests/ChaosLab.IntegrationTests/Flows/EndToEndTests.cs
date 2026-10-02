extern alias OrdersApi;
extern alias PaymentsApi;

using Shared.Contracts;
using System.Net.Http.Json;
using System.Net;
using System.Diagnostics;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using PaymentsApi::Payments.Api;
using PaymentsApi::Payments.Api.Domain;
using Xunit.Abstractions;
using ChaosLab.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OrdersApi::Orders.Api;
using OrdersApi::Orders.Api.Domain;
using PaymentsApi::Payments.Api.Infrastructure.Gateways;
using PaymentsApi::Payments.Api.Application;
using Shouldly;
using Xunit;

namespace ChaosLab.IntegrationTests.Flows;

// Runs Orders.Api and Payments.Api together against the shared RabbitMQ
// container, each with its own database, the way docker-compose wires them.
[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Integration")]
public class EndToEndTests : IAsyncLifetime
{
    private readonly InfrastructureFixture _infra;
    private OrdersApiFactory _orders = null!;
    private string _vhost = null!;
    private readonly ITestOutputHelper _output;
    private HttpClient _ordersClient = null!;
    private OrderStatus? _lastStatus;

    public EndToEndTests(InfrastructureFixture infra, ITestOutputHelper output) => (_infra, _output) = (infra, output);

    public async Task InitializeAsync()
    {
        _vhost = await _infra.CreateVirtualHostAsync();
        var connectionString = ConnectionStrings.ForDatabase(_infra.Sql.GetConnectionString(), DbNames.New("Orders"));
        _orders = new OrdersApiFactory(
            connectionString, _infra.Rabbit.Hostname, _infra.Rabbit.GetMappedPublicPort(5672), "chaos", "chaos", _vhost);
        _ordersClient = _orders.CreateClient();
        await _ordersClient.WaitUntilReadyAsync();
    }

    public async Task DisposeAsync()
    {
        _ordersClient.Dispose();
        await _orders.DisposeAsync();
        await _infra.DeleteVirtualHostAsync(_vhost);
    }

    private PaymentsApiFactory StartPayments(IPaymentGateway? gateway)
    {
        var connectionString = ConnectionStrings.ForDatabase(_infra.Sql.GetConnectionString(), DbNames.New("Payments"));
        return new PaymentsApiFactory(
            connectionString, _infra.Rabbit.Hostname, _infra.Rabbit.GetMappedPublicPort(5672), "chaos", "chaos", gateway, _vhost);
    }

    private async Task<Guid> CreateOrderAsync(decimal amount = 149.90m)
    {
        var response = await _ordersClient.PostAsJsonAsync("/orders", new CreateOrderRequest(Guid.NewGuid(), amount));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<OrderAccepted>();
        return body!.Id;
    }

    private async Task<OrderStatus> ReadStatusAsync(Guid orderId)
    {
        using var scope = _orders.Services.CreateScope();
        var order = await scope.ServiceProvider.GetRequiredService<OrdersDb>().Orders.SingleAsync(o => o.Id == orderId);
        _lastStatus = order.Status;
        return order.Status;
    }

    [Fact]
    [Trait("Scenario", "FLW-01")]
    public async Task Order_ApprovingGateway_EndsUpPaid()
    {
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.ChargeAsync(Arg.Any<ChargeRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChargeResult(true, PaymentGateway.Primary, null));

        await using var payments = StartPayments(gateway);
        using var paymentsClient = payments.CreateClient();
        await paymentsClient.WaitUntilReadyAsync();

        using var traces = new TraceCapture();
        var traceId = ActivityTraceId.CreateRandom();
        _ordersClient.DefaultRequestHeaders.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        var orderId = await CreateOrderAsync();

        await Eventually.Until(async () => await ReadStatusAsync(orderId) != OrderStatus.Pending,
            timeout: TimeSpan.FromSeconds(60), diagnostic: () => $"Order {orderId}: {_lastStatus}");

        (await ReadStatusAsync(orderId)).ShouldBe(OrderStatus.Paid);
        await AssertOrderTrace(traces, orderId, traceId);
    }

    [Fact]
    [Trait("Scenario", "FLW-02")]
    public async Task Order_DecliningGateway_EndsUpPaymentFailed()
    {
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.ChargeAsync(Arg.Any<ChargeRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChargeResult(false, PaymentGateway.Primary, "Card declined"));

        await using var payments = StartPayments(gateway);
        using var paymentsClient = payments.CreateClient();
        await paymentsClient.WaitUntilReadyAsync();

        var orderId = await CreateOrderAsync();

        await Eventually.Until(async () => await ReadStatusAsync(orderId) != OrderStatus.Pending,
            timeout: TimeSpan.FromSeconds(60), diagnostic: () => $"Order {orderId}: {_lastStatus}");

        (await ReadStatusAsync(orderId)).ShouldBe(OrderStatus.PaymentFailed);
    }

    [Fact]
    [Trait("Scenario", "FLW-03")]
    public async Task Order_PaymentsApiStartsLate_StaysPendingThenBecomesPaid()
    {
        // A subscriber's durable topology must exist before any publisher emits events.
        await using (var topology = new PaymentsApiFactory(
            ConnectionStrings.ForDatabase(_infra.Sql.GetConnectionString(), DbNames.New("Topology")),
            _infra.Rabbit.Hostname, _infra.Rabbit.GetMappedPublicPort(5672), "chaos", "chaos",
            virtualHost: _vhost, settings: new Dictionary<string,string?> { ["RabbitMq:DeployTopologyOnly"] = "true" }))
        {
            using var topologyClient = topology.CreateClient();
        }
        var orderId = await CreateOrderAsync();

        (await ReadStatusAsync(orderId)).ShouldBe(OrderStatus.Pending);

        var gateway = Substitute.For<IPaymentGateway>();
        gateway.ChargeAsync(Arg.Any<ChargeRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChargeResult(true, PaymentGateway.Primary, null));

        await using var payments = StartPayments(gateway);
        using var paymentsClient = payments.CreateClient();
        await paymentsClient.WaitUntilReadyAsync();

        await Eventually.Until(async () => await ReadStatusAsync(orderId) == OrderStatus.Paid,
            timeout: TimeSpan.FromSeconds(60), diagnostic: () => $"Order {orderId}: {_lastStatus}");
    }

    [Fact]
    [Trait("Category", "Chaos")]
    [Trait("Scenario", "ORD-09")]
    public async Task Order_BrokerUnavailable_StillAcceptedAndDeliveredOnceBrokerReturns()
    {
        await using var payments = StartPayments(null);
        using var paymentsClient = payments.CreateClient();
        await paymentsClient.WaitUntilReadyAsync();
        var port = _infra.Rabbit.GetMappedPublicPort(5672);
        var total = Stopwatch.StartNew();
        using var traces = new TraceCapture();
        var traceId = ActivityTraceId.CreateRandom();
        _ordersClient.DefaultRequestHeaders.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        await _infra.Rabbit.StopAsync();
        Guid orderId;
        try
        {
            orderId = await CreateOrderAsync();
            (await ReadStatusAsync(orderId)).ShouldBe(OrderStatus.Pending);
            using var scope = _orders.Services.CreateScope();
            (await scope.ServiceProvider.GetRequiredService<OrdersDb>().Set<OutboxMessage>().CountAsync())
                .ShouldBeGreaterThan(0);
        }
        finally
        {
            await _infra.Rabbit.StartAsync();
            await _infra.WaitForRabbitAsync();
        }
        _infra.Rabbit.GetMappedPublicPort(5672).ShouldBe(port);
        var recovery = Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(120);
        await Task.WhenAll(_ordersClient.WaitUntilReadyAsync(), paymentsClient.WaitUntilReadyAsync()).WaitAsync(budget);
        await Eventually.Until(async () => await ReadStatusAsync(orderId) == OrderStatus.Paid,
            budget - recovery.Elapsed, description: "Order paid after real broker recovery", diagnostic: () => $"Order {orderId}: {_lastStatus}");
        using var verify = payments.Services.CreateScope();
        (await verify.ServiceProvider.GetRequiredService<PaymentsDb>().Payments.CountAsync(p => p.OrderId == orderId))
            .ShouldBe(1);
        (await verify.ServiceProvider.GetRequiredService<PaymentsDb>().Set<SimulatedCharge>().CountAsync(p => p.OrderId == orderId))
            .ShouldBe(1);
        await Eventually.Until(async () =>
        {
            using var scope = _orders.Services.CreateScope();
            return !await scope.ServiceProvider.GetRequiredService<OrdersDb>().Set<OutboxMessage>().AnyAsync();
        }, description: "Orders outbox delivered");
        await AssertOrderTrace(traces, orderId, traceId);
        _output.WriteLine($"Broker port {port}; ready-to-Paid {recovery.Elapsed}; entire outage/recovery {total.Elapsed}");
    }

    private static async Task AssertOrderTrace(TraceCapture capture, Guid orderId, ActivityTraceId traceId)
    {
        await Eventually.Until(() => Task.FromResult(capture.Completed.Count(a =>
            a.Kind == ActivityKind.Consumer && (string?)a.GetTagItem("order.id") == orderId.ToString()) >= 2),
            description: "both consumers complete correlated spans");
        var spans = capture.Completed.Where(a => (string?)a.GetTagItem("order.id") == orderId.ToString()).ToArray();
        spans.ShouldNotBeEmpty();
        foreach (var span in spans) span.TraceId.ShouldBe(traceId);
        foreach (var consumer in spans.Where(a => a.Kind == ActivityKind.Consumer))
            consumer.GetTagItem("messaging.message.correlation_id").ShouldBe(orderId.ToString());
    }

    private sealed record OrderAccepted(Guid Id, string Status);
}
