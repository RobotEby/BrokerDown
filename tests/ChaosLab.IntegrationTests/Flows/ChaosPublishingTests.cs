extern alias ChaosWorker;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using ChaosLab.IntegrationTests.Infrastructure;
using ChaosWorker::Chaos.Worker;
using ChaosWorker::Chaos.Worker.Application;
using ChaosWorker::Chaos.Worker.Domain;
using ChaosWorker::Chaos.Worker.Infrastructure;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shared.Contracts;
using Shouldly;
using Xunit;

namespace ChaosLab.IntegrationTests.Flows;

[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ChaosPublishingTests(InfrastructureFixture infra)
{
    [Fact]
    public async Task DeferredDispatchKeepsHttpTraceAndExperimentCorrelation()
    {
        var vhost = await infra.CreateVirtualHostAsync();
        try
        {
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await using var probe = await RabbitMqProbe<ChaosStart>.StartAsync(infra.Rabbit.Hostname,
                infra.Rabbit.GetMappedPublicPort(5672), "chaos", "chaos", vhost);
            await using var factory = new ChaosApiFactory(infra, vhost, key);
            using var client = factory.CreateClient();
            using var traces = new TraceCapture();
            var traceId = ActivityTraceId.CreateRandom();
            client.DefaultRequestHeaders.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
            client.DefaultRequestHeaders.Add("X-Chaos-Api-Key", key);
            using var response = await client.PostAsJsonAsync("/chaos/experiments", new { fault = "Latency" });
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            var coordinator = factory.Services.GetRequiredService<ChaosCoordinator>();
            var run = coordinator.Current!;
            await coordinator.TickAsync(new(true, true, "safe", 20, 0, 1, DateTimeOffset.UtcNow), default);
            (await probe.WaitAsync(TimeSpan.FromSeconds(10))).ExperimentId.ShouldBe(run.ExperimentId);
            probe.CorrelationId.ShouldBe(run.ExperimentId);
            probe.TraceId.ShouldBe(traceId);
            traces.Completed.Single(a => a.OperationName == "chaos.dispatch").TraceId.ShouldBe(traceId);
        }
        finally { await infra.DeleteVirtualHostAsync(vhost); }
    }

    [Fact]
    public async Task UnreachableBrokerCannotExtendTtlOrPreventLocalAbort()
    {
        // A real bus with incorrect credentials exercises publish failure without
        // stopping the shared broker used by the other integration scenarios.
        var bus = Bus.Factory.CreateUsingRabbitMq(c => c.Host(infra.Rabbit.Hostname,
            infra.Rabbit.GetMappedPublicPort(5672), "/", h => { h.Username("denied"); h.Password("denied"); }));
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Should.ThrowAsync<RabbitMqConnectionException>(() => bus.StartAsync(startup.Token));
            var clock = new FakeTimeProvider();
            var options = new ChaosOptions { Enabled = true, CooldownSeconds = 0 };
            var environment = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Development" };
            var state = new ExperimentState(options, new(options), environment, clock, NullLogger<ExperimentState>.Instance);
            var coordinator = new ChaosCoordinator(state, new ChaosCommandPublisher(bus, clock, NullLogger<ChaosCommandPublisher>.Instance));
            coordinator.Request(new(ChaosFault.Latency));
            await coordinator.TickAsync(new(true, true, "safe", 20, 0, 1, clock.GetUtcNow()), default);
            coordinator.Current!.Status.ShouldBe(ExperimentStatus.Starting);
            await coordinator.AbortAsync(true, default);
            coordinator.Current.Status.ShouldBe(ExperimentStatus.AbortRequested);
            coordinator.Snapshot().KillSwitch.ShouldBeTrue();
            clock.Advance(TimeSpan.FromSeconds(30));
            await coordinator.TickAsync(new(false, false, "offline", 0, 0, null, clock.GetUtcNow()), default);
            coordinator.Current.Status.ShouldBe(ExperimentStatus.Expired);
        }
        finally { await bus.StopAsync(); }
    }
}
