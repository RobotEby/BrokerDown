using System.Net;
using System.Text;
using Chaos.Worker;
using MassTransit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shared.Contracts;
using Shouldly;
using Xunit;

namespace Chaos.UnitTests;

[Trait("Category", "Unit")]
public sealed class WorkerTests
{
    private static ChaosCoordinator Create(FakeTimeProvider clock, int cooldown = 0, string environment = "Development")
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environment);
        return new(Substitute.For<IBus>(), new ChaosOptions { Enabled = true, CooldownSeconds = cooldown }, env, clock,
            NullLogger<ChaosCoordinator>.Instance);
    }
    private static MetricsAssessment Safe(FakeTimeProvider clock) => new(true, true, "safe", 20, 0, 0.5, clock.GetUtcNow());

    [Fact]
    public async Task RequestWaitsForMetricsAndAcknowledgement_ThenAbortsOnTelemetryLoss()
    {
        var clock = new FakeTimeProvider();
        var worker = Create(clock);
        var run = worker.Request(new(ChaosFault.Unavailable));
        await worker.TickAsync(Safe(clock) with { EnoughTraffic = false }, default);
        worker.Current!.Status.ShouldBe("waiting");
        await worker.TickAsync(Safe(clock), default);
        worker.Current!.Status.ShouldBe("starting");
        worker.Observe(new(run.ExperimentId, "started", clock.GetUtcNow()));
        worker.Current!.Status.ShouldBe("active");
        await worker.TickAsync(Safe(clock) with { Healthy = false }, default);
        worker.Current!.Status.ShouldBe("abort_requested");
        worker.Observe(new(run.ExperimentId, "aborted", clock.GetUtcNow()));
        worker.Current!.Status.ShouldBe("aborted");
    }

    [Fact]
    public async Task CooldownSingleExecutionAndKillSwitchAreEnforced()
    {
        var clock = new FakeTimeProvider();
        var worker = Create(clock, 60);
        Should.Throw<InvalidOperationException>(() => worker.Request(new(ChaosFault.Latency)));
        clock.Advance(TimeSpan.FromSeconds(61));
        worker.Request(new(ChaosFault.Latency));
        Should.Throw<InvalidOperationException>(() => worker.Request(new(ChaosFault.Latency)));
        await worker.AbortAsync(true, default);
        clock.Advance(TimeSpan.FromMinutes(2));
        Should.Throw<InvalidOperationException>(() => worker.Request(new(ChaosFault.Latency)));
    }

    [Fact]
    public async Task MissingAcknowledgementRequestsAbort_AndTtlEndsUnconfirmedExperiment()
    {
        var clock = new FakeTimeProvider();
        var worker = Create(clock);
        worker.Request(new(ChaosFault.Latency));
        await worker.TickAsync(Safe(clock), default);
        clock.Advance(TimeSpan.FromSeconds(11));
        await worker.TickAsync(Safe(clock), default);
        worker.Current!.Status.ShouldBe("abort_requested");
        clock.Advance(TimeSpan.FromSeconds(20));
        await worker.TickAsync(Safe(clock), default);
        worker.Current!.Status.ShouldBe("expired");
    }

    [Fact]
    public async Task UnsafeMetricsRejectPendingRequest_ProductionCannotStart()
    {
        var clock = new FakeTimeProvider();
        var worker = Create(clock);
        worker.Request(new(ChaosFault.Unavailable));
        clock.Advance(TimeSpan.FromSeconds(61));
        await worker.TickAsync(Safe(clock) with { Healthy = false }, default);
        worker.Current!.Status.ShouldBe("rejected");
        Should.Throw<InvalidOperationException>(() => Create(clock, environment: "Production").Request(new(ChaosFault.Latency)));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "unavailable")]
    [InlineData(HttpStatusCode.OK, "{}")]
    [InlineData(HttpStatusCode.OK, "{\"status\":\"success\",\"data\":{\"result\":[]}}")]
    [InlineData(HttpStatusCode.OK, "{\"status\":\"success\",\"data\":{\"result\":[{\"value\":[]}]}}")]
    public async Task MissingOrMalformedPrometheusDataCannotStartChaos(HttpStatusCode status, string body)
    {
        using var http = new HttpClient(new ResponseHandler(status, body)) { BaseAddress = new("http://prometheus") };
        var metrics = await new PrometheusMonitor(http, new(), new FakeTimeProvider()).EvaluateAsync(default);
        metrics.Healthy.ShouldBeFalse();
    }

    private sealed class ResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
