extern alias PaymentsApi;

using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentsApi::Payments.Api;
using PaymentsApi::Payments.Api.Gateways;

namespace ChaosLab.IntegrationTests.Infrastructure;

// Hosts OrderCreatedConsumer on the in-memory transport, backed by a real SQL
// Server database, so its outbox and inbox behavior can be observed directly,
// without needing a real broker.
public sealed class PaymentsConsumerHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    public ITestHarness Harness { get; }
    public IServiceProvider Services => _provider;
    public ToggleableFailureInterceptor Interceptor { get; }

    private PaymentsConsumerHarness(ServiceProvider provider, ITestHarness harness, ToggleableFailureInterceptor interceptor)
    {
        _provider = provider;
        Harness = harness;
        Interceptor = interceptor;
    }

    public static async Task<PaymentsConsumerHarness> StartAsync(string connectionString, IPaymentGateway gateway)
    {
        var interceptor = new ToggleableFailureInterceptor();
        var services = new ServiceCollection();

        services.AddSingleton(interceptor);
        services.AddDbContext<PaymentsDb>(o => o.UseSqlServer(connectionString).AddInterceptors(interceptor));
        services.AddSingleton(gateway);

        services.AddMassTransitTestHarness(x =>
        {
            x.AddConsumer<OrderCreatedConsumer>();
            x.AddEntityFrameworkOutbox<PaymentsDb>(o =>
            {
                o.UseSqlServer();
                o.UseBusOutbox();
            });
        });

        var provider = services.BuildServiceProvider(validateScopes: true);

        using (var scope = provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PaymentsDb>().Database.EnsureCreatedAsync();

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        return new PaymentsConsumerHarness(provider, harness, interceptor);
    }

    public async ValueTask DisposeAsync()
    {
        await Harness.Stop();
        await _provider.DisposeAsync();
    }
}
