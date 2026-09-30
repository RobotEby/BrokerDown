using Microsoft.Extensions.Diagnostics.HealthChecks;
using MassTransit;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Payments.Api;
using Payments.Api.Gateways;
using Payments.Api.Chaos;

var builder = WebApplication.CreateBuilder(args.Where(a => a is not "--deploy-topology" and not "--adopt-legacy-database").ToArray());
var cfg = builder.Configuration;
builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);
builder.Services.AddLabTelemetry(cfg, "payments-api");
if (args.Contains("--deploy-topology")) cfg["RabbitMq:DeployTopologyOnly"] = "true";

builder.Services.AddDbContext<PaymentsDb>(o => o.UseSqlServer(cfg.GetConnectionString("Db")));
builder.Services.AddDbContextFactory<SimulatedGatewayDb>(o => o.UseSqlServer(cfg.GetConnectionString("Db")));
builder.Services.AddSingleton<ChargeLedger>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ChaosState>();
builder.Services.AddHostedService<ChaosExpiryService>();
builder.Services.AddSingleton(sp => new SimulatedPrimaryGateway(sp.GetRequiredService<ChargeLedger>(), cfg,
    sp.GetRequiredService<ChaosState>().ApplyAsync));
builder.Services.AddSingleton<SimulatedFallbackGateway>();
builder.Services.AddSingleton<IPaymentGateway>(sp => new ResilientPaymentGateway(
    sp.GetRequiredService<SimulatedPrimaryGateway>(), sp.GetRequiredService<SimulatedFallbackGateway>(),
    sp.GetRequiredService<ChargeLedger>().FindAsync, cfg.GetSection("Resilience").Get<ResilienceOptions>() ?? new(),
    sp.GetRequiredService<ILogger<ResilientPaymentGateway>>()));

builder.Services.AddMassTransit(x =>
{
    x.SetKebabCaseEndpointNameFormatter();
    x.AddConsumeObserver<MessageFaultTelemetry>();
    x.AddConsumer<OrderCreatedConsumer, OrderCreatedConsumerDefinition>();
    x.AddConsumer<ChaosControlConsumer>();

    x.AddEntityFrameworkOutbox<PaymentsDb>(o =>
    {
        o.UseSqlServer();
        o.UseBusOutbox();
        o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
    });

    x.UsingRabbitMq((ctx, bus) =>
    {
        bus.Host(cfg["RabbitMq:Host"], cfg.GetValue<ushort>("RabbitMq:Port", 5672), cfg["RabbitMq:VirtualHost"] ?? "/", h =>
        {
            h.Username(cfg["RabbitMq:User"]!);
            h.Password(cfg["RabbitMq:Password"]!);
        });
        bus.DeployTopologyOnly = cfg.GetValue<bool>("RabbitMq:DeployTopologyOnly");
        bus.ConfigureEndpoints(ctx);
    });
});

builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck<PaymentsDb>>("database");
await using var app = builder.Build();
if (cfg.GetValue<bool>("RabbitMq:DeployTopologyOnly"))
{
    await app.Services.GetRequiredService<IBusControl>().DeployAsync(app.Lifetime.ApplicationStopping);
    if (args.Contains("--deploy-topology")) return;
}
await Database.InitializeAsync<PaymentsDb>(app.Services, app.Lifetime.ApplicationStopping,
    (db, ct) => PaymentsDatabase.InitializeAsync(db, args.Contains("--adopt-legacy-database"), ct));
if (args.Contains("--adopt-legacy-database")) return;

app.MapGet("/health", () => Results.Ok("ok"));
app.MapHealthChecks("/health/ready", new HealthCheckOptions { ResultStatusCodes = { [HealthStatus.Degraded] = 503 } });

app.MapGet("/payments/{orderId:guid}", async (Guid orderId, PaymentsDb db, CancellationToken ct) =>
    await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.OrderId == orderId, ct) is { } p
        ? Results.Ok(new { p.OrderId, p.Amount, Status = p.Status.ToString(), p.Gateway, p.FailureReason, p.CreatedAt })
        : Results.NotFound());

app.Run();

public partial class Program { }
