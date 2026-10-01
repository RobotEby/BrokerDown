using System.Text;
using System.Diagnostics.Metrics;
using OpenTelemetry.Metrics;
using ChaosLab.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Shared.Infrastructure;
using Shouldly;
using Xunit;

namespace ChaosLab.IntegrationTests.Flows;

[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TelemetryExportTests
{
    [Fact]
    public async Task MetricsAndTracesReachTheirIndependentOtlpEndpoints()
    {
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var metricsReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        await using var receiver = builder.Build();
        receiver.Urls.Add("http://127.0.0.1:0");
        receiver.MapPost("/v1/traces", async (HttpContext context) =>
        {
            context.Request.ContentType.ShouldBe("application/x-protobuf");
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            received.TrySetResult(body.ToArray());
            context.Response.ContentType = "application/x-protobuf";
        });
        receiver.MapPost("/v1/metrics", async (HttpContext context) =>
        {
            context.Request.ContentType.ShouldBe("application/x-protobuf");
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            metricsReceived.TrySetResult(body.ToArray());
            context.Response.ContentType = "application/x-protobuf";
        });
        await receiver.StartAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telemetry:TracesEndpoint"] = receiver.Urls.Single() + "/v1/traces",
            ["Telemetry:MetricsEndpoint"] = receiver.Urls.Single() + "/v1/metrics"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLabTelemetry(config, "otlp-validation");
        await using var provider = services.BuildServiceProvider();
        var tracer = provider.GetRequiredService<TracerProvider>();
        var metrics = provider.GetRequiredService<MeterProvider>();
        using var meter = new Meter("ChaosLab.Orders");
        meter.CreateCounter<long>("validation.counter").Add(1);
        using (var activity = Telemetry.Activities.StartActivity("validation.otlp-span")) activity.ShouldNotBeNull();
        tracer.ForceFlush(5000).ShouldBeTrue();
        metrics.ForceFlush(5000).ShouldBeTrue();
        var payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Encoding.UTF8.GetString(payload).ShouldContain("validation.otlp-span");
        Encoding.UTF8.GetString(payload).ShouldContain("otlp-validation");
        Encoding.UTF8.GetString(await metricsReceived.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldContain("validation.counter");
    }
}
