extern alias PaymentsApi;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using PaymentsProgram = PaymentsApi::Program;
using PaymentsApi::Payments.Api.Infrastructure.Gateways;
using PaymentsApi::Payments.Api.Application;

namespace ChaosLab.IntegrationTests.Infrastructure;

public sealed class PaymentsApiFactory(
    string connectionString, string rabbitHost, ushort rabbitPort, string rabbitUser, string rabbitPassword,
    IPaymentGateway? gateway = null, string virtualHost = "/",
    IReadOnlyDictionary<string,string?>? settings = null, string environment = "Development")
    : WebApplicationFactory<PaymentsProgram>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("RabbitMq:VirtualHost", virtualHost);
        if (settings is not null) foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
        builder.UseSetting("ConnectionStrings:Db", connectionString);
        builder.UseSetting("RabbitMq:Host", rabbitHost);
        builder.UseSetting("RabbitMq:Port", rabbitPort.ToString());
        builder.UseSetting("RabbitMq:User", rabbitUser);
        builder.UseSetting("RabbitMq:Password", rabbitPassword);

        if (gateway is not null) builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPaymentGateway>();
            services.AddSingleton(gateway);
        });
    }
}
