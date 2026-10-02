extern alias ChaosWorker;

using ChaosWorker::Chaos.Worker.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ChaosLab.IntegrationTests.Infrastructure;

// Endpoint tests run the actual auth pipeline and RabbitMQ publisher. Polling is
// disabled so safety assessments do not race the operator requests under test.
public sealed class ChaosApiFactory(InfrastructureFixture infra, string vhost, string? key, string environment = "Development")
    : WebApplicationFactory<ChaosWorker::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("Chaos:AdminApiKey", key);
        builder.UseSetting("Chaos:Enabled", "true");
        builder.UseSetting("Chaos:CooldownSeconds", "0");
        builder.UseSetting("RabbitMq:Host", infra.Rabbit.Hostname);
        builder.UseSetting("RabbitMq:Port", infra.Rabbit.GetMappedPublicPort(5672).ToString());
        builder.UseSetting("RabbitMq:VirtualHost", vhost);
        builder.UseSetting("RabbitMq:User", "chaos");
        builder.UseSetting("RabbitMq:Password", "chaos");
        builder.UseSetting("Prometheus:Url", "http://127.0.0.1:1");
        builder.ConfigureServices(services =>
        {
            var polling = services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ExperimentWorker));
            services.Remove(polling);
        });
    }
}
