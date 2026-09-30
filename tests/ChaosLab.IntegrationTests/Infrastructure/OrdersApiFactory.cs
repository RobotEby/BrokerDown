extern alias OrdersApi;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using OrdersProgram = OrdersApi::Program;

namespace ChaosLab.IntegrationTests.Infrastructure;

public sealed class OrdersApiFactory(
    string connectionString, string rabbitHost, ushort rabbitPort, string rabbitUser, string rabbitPassword,
    string virtualHost = "/", IReadOnlyDictionary<string,string?>? settings = null, string environment = "Development")
    : WebApplicationFactory<OrdersProgram>
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
    }
}
