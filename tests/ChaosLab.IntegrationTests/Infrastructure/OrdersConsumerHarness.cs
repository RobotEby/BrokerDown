extern alias OrdersApi;

using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrdersApi::Orders.Api;

namespace ChaosLab.IntegrationTests.Infrastructure;

// Hosts PaymentProcessedConsumer on the in-memory transport, backed by a real
// SQL Server database, so its outbox and inbox behavior can be observed
// directly, without needing a real broker.
public sealed class OrdersConsumerHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    public ITestHarness Harness { get; }
    public IServiceProvider Services => _provider;

    private OrdersConsumerHarness(ServiceProvider provider, ITestHarness harness)
    {
        _provider = provider;
        Harness = harness;
    }

    public static async Task<OrdersConsumerHarness> StartAsync(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddDbContext<OrdersDb>(o => o.UseSqlServer(connectionString));

        services.AddMassTransitTestHarness(x =>
        {
            x.AddConsumer<PaymentProcessedConsumer>();
            x.AddEntityFrameworkOutbox<OrdersDb>(o =>
            {
                o.UseSqlServer();
                o.UseBusOutbox();
            });
        });

        var provider = services.BuildServiceProvider(validateScopes: true);

        using (var scope = provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<OrdersDb>().Database.EnsureCreatedAsync();

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        return new OrdersConsumerHarness(provider, harness);
    }

    public async ValueTask DisposeAsync()
    {
        await Harness.Stop();
        await _provider.DisposeAsync();
    }
}
