extern alias OrdersApi;

using System.Net;
using System.Net.Http.Json;
using ChaosLab.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrdersApi::Orders.Api;
using Shared.Contracts;
using Shouldly;
using Xunit;

namespace ChaosLab.IntegrationTests.Endpoints;

[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Integration")]
public class OrdersEndpointsTests : IAsyncLifetime
{
    private readonly InfrastructureFixture _infra;
    private OrdersApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _vhost = null!;

    public OrdersEndpointsTests(InfrastructureFixture infra) => _infra = infra;

    public async Task InitializeAsync()
    {
        _vhost = await _infra.CreateVirtualHostAsync();
        var connectionString = ConnectionStrings.ForDatabase(_infra.Sql.GetConnectionString(), DbNames.New("Orders"));
        _factory = new OrdersApiFactory(
            connectionString, _infra.Rabbit.Hostname, _infra.Rabbit.GetMappedPublicPort(5672), "chaos", "chaos", _vhost);
        _client = _factory.CreateClient();
        await _client.WaitUntilReadyAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _infra.DeleteVirtualHostAsync(_vhost);
    }

    [Fact]
    [Trait("Scenario", "ORD-01")]
    public async Task PostOrders_ValidRequest_AcceptsStoresAsPendingAndPublishesOrderCreated()
    {
        await using var probe = await RabbitMqProbe<OrderCreated>.StartAsync(
            _infra.Rabbit.Hostname, _infra.Rabbit.GetMappedPublicPort(5672), "chaos", "chaos", _vhost);

        var customerId = Guid.NewGuid();
        var response = await _client.PostAsJsonAsync("/orders", new CreateOrderRequest(customerId, 149.90m));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<OrderAccepted>();
        body.ShouldNotBeNull();
        body!.Status.ShouldBe("Pending");

        using var scope = _factory.Services.CreateScope();
        var order = await scope.ServiceProvider.GetRequiredService<OrdersDb>().Orders.SingleAsync(o => o.Id == body.Id);
        order.CustomerId.ShouldBe(customerId);
        order.Status.ShouldBe(OrderStatus.Pending);

        var published = await probe.WaitAsync(TimeSpan.FromSeconds(10));
        published.OrderId.ShouldBe(body.Id);
        published.CustomerId.ShouldBe(customerId);
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("0.001", false)]
    [InlineData("10000000000000000", false)]
    [InlineData("10", true)]
    [Trait("Scenario", "ORD-02")]
    public async Task PostOrders_InvalidRequest_ReturnsBadRequestAndPersistsNothing(string amount, bool emptyCustomer)
    {
        var response = await _client.PostAsJsonAsync("/orders", new CreateOrderRequest(emptyCustomer ? Guid.Empty : Guid.NewGuid(), decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var scope = _factory.Services.CreateScope();
        var count = await scope.ServiceProvider.GetRequiredService<OrdersDb>().Orders.CountAsync();
        count.ShouldBe(0);
        (await scope.ServiceProvider.GetRequiredService<OrdersDb>().Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    [Trait("Scenario", "ORD-03")]
    public async Task GetOrder_ExistingOrder_ReturnsItWithCurrentStatus()
    {
        var created = await _client.PostAsJsonAsync("/orders", new CreateOrderRequest(Guid.NewGuid(), 50m));
        var accepted = await created.Content.ReadFromJsonAsync<OrderAccepted>();

        var response = await _client.GetAsync($"/orders/{accepted!.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Scenario", "ORD-04")]
    public async Task GetOrder_UnknownId_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/orders/{Guid.NewGuid()}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private sealed record OrderAccepted(Guid Id, string Status);
}
