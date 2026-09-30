using System.Text.Json.Serialization;
using Chaos.Worker;
using MassTransit;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shared.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
var options = cfg.GetSection("Chaos").Get<ChaosOptions>() ?? new();
if (options.PollSeconds < 1 || options.CooldownSeconds < 0 || options.WaitSeconds < 1 || options.AcknowledgementSeconds < 1 ||
    options.MinimumOrders < 1 || options.MaximumFailureRatio is < 0 or > 1 || options.MaximumP95Seconds <= 0)
    throw new InvalidOperationException("Invalid Chaos safety settings");
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddLabTelemetry(cfg, "chaos-worker");
builder.Services.AddHttpClient<PrometheusMonitor>(c =>
{
    c.BaseAddress = new Uri(cfg["Prometheus:Url"] ?? "http://localhost:9090");
    c.Timeout = TimeSpan.FromSeconds(3);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) });
builder.Services.AddSingleton<ChaosCoordinator>();
builder.Services.AddHostedService<ExperimentWorker>();
builder.Services.AddMassTransit(x =>
{
    x.SetKebabCaseEndpointNameFormatter();
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
app.MapGet("/health", () => Results.Ok("ok"));
app.MapHealthChecks("/health/ready", new HealthCheckOptions { ResultStatusCodes = { [HealthStatus.Degraded] = 503 } });
app.MapGet("/chaos", (ChaosCoordinator chaos) => Results.Ok(chaos.Snapshot()));
app.MapPost("/chaos/experiments", (ExperimentRequest request, ChaosCoordinator chaos) =>
{
    try { return Results.Accepted("/chaos", chaos.Request(request)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
app.MapPost("/chaos/abort", async (ChaosCoordinator chaos, CancellationToken ct) =>
{ await chaos.AbortAsync(false, ct); return Results.Accepted("/chaos", chaos.Snapshot()); });
app.MapPost("/chaos/kill-switch", async (ChaosCoordinator chaos, CancellationToken ct) =>
{ await chaos.AbortAsync(true, ct); return Results.Accepted("/chaos", chaos.Snapshot()); });
app.Run();

public partial class Program { }
public sealed class PrometheusHealthCheck(PrometheusMonitor monitor) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await monitor.IsReadyAsync(ct) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Prometheus unavailable");
}
