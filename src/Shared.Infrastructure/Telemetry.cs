using System.Diagnostics;
using System.Diagnostics.Metrics;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using MassTransit.Logging;

namespace Shared.Infrastructure;

public static class Telemetry
{
    public static readonly ActivitySource Activities = new("ChaosLab");

    public static IServiceCollection AddLabTelemetry(this IServiceCollection services, IConfiguration config, string serviceName)
    {
        services.Configure<LoggerFactoryOptions>(o => o.ActivityTrackingOptions =
            ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId);
        services.AddHealthChecks();
        services.AddHostedService<HealthTelemetry>();
        services.AddOpenTelemetry().ConfigureResource(r => r.AddService(serviceName, serviceInstanceId: Guid.NewGuid().ToString("N")))
            .WithMetrics(m =>
            {
                m.AddMeter("ChaosLab.Orders", "ChaosLab.Payments", "ChaosLab.Chaos", "ChaosLab.Health", "ChaosLab.Messaging", "Microsoft.AspNetCore.Hosting");
                m.AddView("chaoslab.order.duration", new ExplicitBucketHistogramConfiguration
                { Boundaries = [0.1, 0.25, 0.5, 1, 2, 5, 10, 30, 60, 120] });
                if (Uri.TryCreate(config["Telemetry:MetricsEndpoint"], UriKind.Absolute, out var endpoint))
                    m.AddOtlpExporter((o, reader) =>
                    {
                        o.Endpoint = endpoint;
                        o.Protocol = OtlpExportProtocol.HttpProtobuf;
                        o.TimeoutMilliseconds = 3000;
                        reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 5000;
                        reader.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative;
                    });
            })
            .WithTracing(t =>
            {
                t.AddSource("Microsoft.AspNetCore", "System.Net.Http", DiagnosticHeaders.DefaultListenerName, Activities.Name);
                if (Uri.TryCreate(config["Telemetry:TracesEndpoint"], UriKind.Absolute, out var endpoint))
                    t.AddOtlpExporter(o =>
                    {
                        o.Endpoint = endpoint;
                        o.Protocol = OtlpExportProtocol.HttpProtobuf;
                        o.TimeoutMilliseconds = 3000;
                    });
            });
        return services;
    }
}

internal sealed class HealthTelemetry(HealthCheckService checks, ILogger<HealthTelemetry> logger) : BackgroundService
{
    private readonly Meter _meter = new("ChaosLab.Health");
    private int _healthy;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _meter.CreateObservableGauge("chaoslab.service.heartbeat", () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
        _meter.CreateObservableGauge("chaoslab.service.ready", () => Volatile.Read(ref _healthy));
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var health = await checks.CheckHealthAsync(timeout.Token);
                Volatile.Write(ref _healthy, health.Status == HealthStatus.Healthy ? 1 : 0);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                Volatile.Write(ref _healthy, 0);
                logger.LogWarning(ex, "Readiness probe failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
    public override void Dispose() { _meter.Dispose(); base.Dispose(); }
}

public sealed class MessageFaultTelemetry(ILogger<MessageFaultTelemetry> logger) : IConsumeObserver
{
    private static readonly Meter Meter = new("ChaosLab.Messaging");
    private static readonly Counter<long> Errors = Meter.CreateCounter<long>("chaoslab.messaging.errors");
    public Task PreConsume<T>(ConsumeContext<T> context) where T : class
    {
        Activity.Current?.SetTag("messaging.message.id", context.MessageId?.ToString())
            .SetTag("messaging.message.correlation_id", context.CorrelationId?.ToString());
        return Task.CompletedTask;
    }
    public Task PostConsume<T>(ConsumeContext<T> context) where T : class => Task.CompletedTask;
    public Task ConsumeFault<T>(ConsumeContext<T> context, Exception exception) where T : class
    {
        Errors.Add(1, new KeyValuePair<string, object?>("message_type", typeof(T).Name));
        logger.LogError(exception, "Message {MessageId} ({MessageType}), correlation {CorrelationId} failed",
            context.MessageId, typeof(T).Name, context.CorrelationId);
        return Task.CompletedTask;
    }
}
