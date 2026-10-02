using Chaos.Worker.Application;
using Chaos.Worker.Domain;
using Chaos.Worker.Infrastructure;
using System.Text.Json.Serialization;
using Chaos.Worker;
using MassTransit;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shared.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
builder.Services.AddSingleton(new ChaosAdminKey(cfg["Chaos:AdminApiKey"]));
builder.Services.AddAuthentication(ChaosAdminAuthentication.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ChaosAdminAuthentication>(ChaosAdminAuthentication.SchemeName, _ => { });
builder.Services.AddAuthorizationBuilder().AddPolicy(ChaosAdminAuthentication.PolicyName,
    policy => policy.RequireAuthenticatedUser().RequireClaim("permission", "chaos.admin"));
builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter<Shared.Contracts.ChaosFault>()));
var options = cfg.GetSection("Chaos").Get<ChaosOptions>() ?? new();
ExperimentValidator.Validate(options);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddLabTelemetry(cfg, "chaos-worker");
builder.Services.AddHttpClient<PrometheusMonitor>(c =>
{
    c.BaseAddress = new Uri(cfg["Prometheus:Url"] ?? "http://localhost:9090");
    c.Timeout = TimeSpan.FromSeconds(3);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) });
builder.Services.AddSingleton<ChaosSafetyPolicy>();
builder.Services.AddSingleton<ExperimentState>();
builder.Services.AddSingleton<ChaosCommandPublisher>();
builder.Services.AddSingleton<ChaosCoordinator>();
builder.Services.AddHostedService<ExperimentWorker>();
builder.Services.AddMassTransit(x =>
{
    x.SetKebabCaseEndpointNameFormatter();
    x.AddConsumeObserver<MessageFaultTelemetry>();
    x.AddConsumer<ExperimentChangedConsumer>();
    x.UsingRabbitMq((ctx, bus) =>
    {
        bus.Host(cfg["RabbitMq:Host"] ?? "localhost", cfg.GetValue<ushort>("RabbitMq:Port", 5672), cfg["RabbitMq:VirtualHost"] ?? "/", h =>
        { h.Username(cfg["RabbitMq:User"]!); h.Password(cfg["RabbitMq:Password"]!); });
        bus.ConfigureEndpoints(ctx);
    });
});
builder.Services.AddHealthChecks().AddCheck<PrometheusHealthCheck>("prometheus");
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health", () => Results.Ok("ok"));
app.MapHealthChecks("/health/ready", new HealthCheckOptions { ResultStatusCodes = { [HealthStatus.Degraded] = 503 } });
var admin = app.MapGroup("/chaos").RequireAuthorization(ChaosAdminAuthentication.PolicyName);
admin.MapGet("", (ChaosCoordinator chaos) => Results.Ok(chaos.Snapshot()));
admin.MapPost("/experiments", (ExperimentRequest request, ChaosCoordinator chaos) =>
{
    try { return Results.Accepted("/chaos", chaos.Request(request)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
admin.MapPost("/abort", async (ChaosCoordinator chaos, CancellationToken ct) =>
{ await chaos.AbortAsync(false, ct); return Results.Accepted("/chaos", chaos.Snapshot()); });
admin.MapPost("/kill-switch", async (ChaosCoordinator chaos, CancellationToken ct) =>
{ await chaos.AbortAsync(true, ct); return Results.Accepted("/chaos", chaos.Snapshot()); });
app.Run();

public partial class Program { }
public sealed class PrometheusHealthCheck(PrometheusMonitor monitor) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await monitor.IsReadyAsync(ct) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Prometheus unavailable");
}
