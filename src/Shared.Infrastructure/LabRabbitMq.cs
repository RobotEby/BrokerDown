using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Shared.Infrastructure;

public static class LabRabbitMq
{
    private const string DeployTopologyFlag = "--deploy-topology";
    private const string DeployTopologyOnlyKey = "RabbitMq:DeployTopologyOnly";

    // Translates the command-line flag into configuration before the bus is built.
    public static void ApplyDeployTopologyFlag(this IConfiguration cfg, string[] args)
    {
        if (args.Contains(DeployTopologyFlag)) cfg[DeployTopologyOnlyKey] = "true";
    }

    public static void AddLabRabbitMq(this IBusRegistrationConfigurator x, IConfiguration cfg) =>
        x.UsingRabbitMq((ctx, bus) =>
        {
            bus.Host(cfg["RabbitMq:Host"], cfg.GetValue<ushort>("RabbitMq:Port", 5672), cfg["RabbitMq:VirtualHost"] ?? "/", h =>
            {
                h.Username(cfg["RabbitMq:User"]!);
                h.Password(cfg["RabbitMq:Password"]!);
            });
            bus.DeployTopologyOnly = cfg.GetValue<bool>(DeployTopologyOnlyKey);
            bus.ConfigureEndpoints(ctx);
        });

    // Returns true when the process was started with --deploy-topology and must exit.
    public static async Task<bool> DeployTopologyIfRequestedAsync(this WebApplication app, string[] args)
    {
        if (!app.Configuration.GetValue<bool>(DeployTopologyOnlyKey)) return false;
        await app.Services.GetRequiredService<IBusControl>().DeployAsync(app.Lifetime.ApplicationStopping);
        return args.Contains(DeployTopologyFlag);
    }
}
