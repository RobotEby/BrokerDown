extern alias OrdersApi;
extern alias PaymentsApi;

using System.Net;
using System.Net.Http.Json;
using ChaosLab.IntegrationTests.Infrastructure;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrdersApi::Orders.Api;
using PaymentsApi::Payments.Api;
using PaymentsApi::Payments.Api.Chaos;
using Shared.Contracts;
using Shouldly;
using Xunit;

namespace ChaosLab.IntegrationTests.Flows;

[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Category", "Chaos")]
public sealed class ChaosPaymentTests(InfrastructureFixture infra)
{
    [Theory]
    [InlineData(ChaosFault.Unavailable, true)]
    [InlineData(ChaosFault.Latency, false)]
    public async Task ControlledFault_UsesFallback_AndRecovers(ChaosFault fault, bool abort)
    {
        var vhost = await infra.CreateVirtualHostAsync();
        try
        {
            await using var payments = new PaymentsApiFactory(
                ConnectionStrings.ForDatabase(infra.Sql.GetConnectionString(), DbNames.New("Payments")),
                infra.Rabbit.Hostname, infra.Rabbit.GetMappedPublicPort(5672), "chaos", "chaos", virtualHost: vhost,
                settings: new Dictionary<string,string?>
                {
                    ["Chaos:Enabled"] = "true", ["Gateway:LatencyMilliseconds"] = "0",
                    ["Resilience:TimeoutMilliseconds"] = "50", ["Resilience:RetryDelayMilliseconds"] = "1"
                });
            using var paymentsClient = payments.CreateClient();
            await paymentsClient.WaitUntilReadyAsync();
            await using var orders = new OrdersApiFactory(
                ConnectionStrings.ForDatabase(infra.Sql.GetConnectionString(), DbNames.New("Orders")),
                infra.Rabbit.Hostname, infra.Rabbit.GetMappedPublicPort(5672), "chaos", "chaos", vhost);
            using var client = orders.CreateClient();
            await client.WaitUntilReadyAsync();
            var state = payments.Services.GetRequiredService<ChaosState>();
            var bus = payments.Services.GetRequiredService<IBus>();
            var id = Guid.NewGuid();
            await bus.Publish(new ChaosStart(id, fault, DateTimeOffset.UtcNow.AddSeconds(5), 500));
            await Eventually.Until(() => Task.FromResult(state.IsActive), description: "fault applied by Payments");
            var first = await CreateAndPay(client, payments);
            first.Gateway.ShouldBe("fallback");
            if (abort) await bus.Publish(new ChaosAbort(id));
            await Eventually.Until(() => Task.FromResult(!state.IsActive), description: "fault ended by abort or local TTL");
            var recovered = await CreateAndPay(client, payments);
            recovered.Gateway.ShouldBe("primary");
            using var scope = payments.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDb>();
            (await db.Payments.CountAsync()).ShouldBe(2);
            (await db.SimulatedCharges.CountAsync()).ShouldBe(2);
            (await db.SimulatedCharges.Select(c => c.OrderId).Distinct().CountAsync()).ShouldBe(2);
        }
        finally { await infra.DeleteVirtualHostAsync(vhost); }
    }

    private static async Task<Payment> CreateAndPay(HttpClient client, PaymentsApiFactory payments)
    {
        using var response = await client.PostAsJsonAsync("/orders", new CreateOrderRequest(Guid.NewGuid(), 30m));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var order = await response.Content.ReadFromJsonAsync<Accepted>();
        await Eventually.Until(async () =>
        {
            var status = await client.GetFromJsonAsync<Accepted>($"/orders/{order!.Id}");
            return status?.Status == "Paid";
        }, TimeSpan.FromSeconds(30), description: "order paid during or after chaos");
        using var scope = payments.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDb>().Payments.AsNoTracking().SingleAsync(p => p.OrderId == order!.Id);
    }
    private sealed record Accepted(Guid Id, string Status);
}
