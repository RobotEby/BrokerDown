extern alias PaymentsApi;

using System.Net;
using ChaosLab.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using PaymentsApi::Payments.Api;
using PaymentsApi::Payments.Api.Gateways;
using Shouldly;
using Xunit;

namespace ChaosLab.IntegrationTests.Endpoints;

[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Integration")]
public class PaymentsEndpointsTests : IAsyncLifetime
{
    private readonly InfrastructureFixture _infra;
    private PaymentsApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _vhost = null!;

    public PaymentsEndpointsTests(InfrastructureFixture infra) => _infra = infra;

    public async Task InitializeAsync()
    {
        _vhost = await _infra.CreateVirtualHostAsync();
        var connectionString = ConnectionStrings.ForDatabase(_infra.Sql.GetConnectionString(), DbNames.New("Payments"));
        _factory = new PaymentsApiFactory(
            connectionString, _infra.Rabbit.Hostname, _infra.Rabbit.GetMappedPublicPort(5672),
            "chaos", "chaos", Substitute.For<IPaymentGateway>(), _vhost);
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
    [Trait("Scenario", "PAY-06")]
    public async Task GetPayment_ExistingPayment_ReturnsIt()
    {
        var orderId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
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

        var response = await _client.GetAsync($"/payments/{orderId}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Scenario", "PAY-07")]
    public async Task GetPayment_NoPaymentForOrder_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/payments/{Guid.NewGuid()}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
