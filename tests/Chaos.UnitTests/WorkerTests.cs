using Chaos.Worker.Domain;
using Chaos.Worker.Application;
using Chaos.Worker.Infrastructure;
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
        var options = new ChaosOptions { Enabled = true, CooldownSeconds = cooldown };
        return new(new ExperimentState(options, new(options), env, clock, NullLogger<ExperimentState>.Instance),
            new ChaosCommandPublisher(Substitute.For<IBus>(), clock, NullLogger<ChaosCommandPublisher>.Instance));
    }
    private static MetricsAssessment Safe(FakeTimeProvider clock) => new(true, true, "safe", 20, 0, 0.5, clock.GetUtcNow());

    [Fact]
    public async Task RequestWaitsForMetricsAndAcknowledgement_ThenAbortsOnTelemetryLoss()
    {
        var clock = new FakeTimeProvider();
        var worker = Create(clock);
        var run = worker.Request(new(ChaosFault.Unavailable));
        await worker.TickAsync(Safe(clock) with { EnoughTraffic = false }, default);
        worker.Current!.Status.ShouldBe(ExperimentStatus.Waiting);
        await worker.TickAsync(Safe(clock), default);
        worker.Current!.Status.ShouldBe(ExperimentStatus.Starting);
        worker.Observe(new(run.ExperimentId, ChaosExperimentStatus.Started, clock.GetUtcNow()));
        worker.Current!.Status.ShouldBe(ExperimentStatus.Active);
        await worker.TickAsync(Safe(clock) with { Healthy = false }, default);
        worker.Current!.Status.ShouldBe(ExperimentStatus.AbortRequested);
        worker.Observe(new(run.ExperimentId, ChaosExperimentStatus.Aborted, clock.GetUtcNow()));
        worker.Current!.Status.ShouldBe(ExperimentStatus.Aborted);
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
        worker.Current!.Status.ShouldBe(ExperimentStatus.AbortRequested);
        clock.Advance(TimeSpan.FromSeconds(20));
        await worker.TickAsync(Safe(clock), default);
        worker.Current!.Status.ShouldBe(ExperimentStatus.Expired);
    }

    [Fact]
    public async Task UnsafeMetricsRejectPendingRequest_ProductionCannotStart()
    {
        var clock = new FakeTimeProvider();
        var worker = Create(clock);
        worker.Request(new(ChaosFault.Unavailable));
        clock.Advance(TimeSpan.FromSeconds(61));
        await worker.TickAsync(Safe(clock) with { Healthy = false }, default);
        worker.Current!.Status.ShouldBe(ExperimentStatus.Rejected);
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
        var metrics = await new PrometheusMonitor(http, new(new()), new FakeTimeProvider()).EvaluateAsync(default);
        metrics.Healthy.ShouldBeFalse();
    }

    [Theory]
    [InlineData(-1, 30, 2000)]
    [InlineData(0, 0, 2000)]
    [InlineData(0, 61, 2000)]
    [InlineData(0, 30, 0)]
    [InlineData(0, 30, 5001)]
    public void InvalidRequestLeavesStateUntouched(int fault, int duration, int latency)
    {
        var worker = Create(new());
        Should.Throw<ArgumentException>(() => worker.Request(new((ChaosFault)fault, duration, latency)));
        worker.Current.ShouldBeNull();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void InvalidSafetyValuesCannotEnableExperiments(double value)
    {
        Should.Throw<InvalidOperationException>(() => ExperimentValidator.Validate(new ChaosOptions { MaximumFailureRatio = value }));
        Should.Throw<InvalidOperationException>(() => ExperimentValidator.Validate(new ChaosOptions { MaximumP95Seconds = value }));
        var policy = new ChaosSafetyPolicy(new());
        policy.Assess(2, value, 0, 1, DateTimeOffset.UtcNow).Healthy.ShouldBeFalse();
        policy.Assess(2, 20, value, 1, DateTimeOffset.UtcNow).Healthy.ShouldBeFalse();
        policy.Assess(2, 20, 0, value, DateTimeOffset.UtcNow).Healthy.ShouldBeFalse();
    }

    [Fact]
    public async Task ConcurrentRequestsAcceptOnlyOne_AbortStartsCooldown_AndOldAckCannotReactivate()
    {
        var clock = new FakeTimeProvider();
        var worker = Create(clock, 60);
        clock.Advance(TimeSpan.FromSeconds(60));
        var accepted = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            try { return worker.Request(new(ChaosFault.Latency)); }
            catch (InvalidOperationException) { return null; }
        })));
        var first = accepted.OfType<ExperimentRun>().Single();
        await worker.AbortAsync(false, default);
        worker.Current!.Status.ShouldBe(ExperimentStatus.Aborted);
        worker.Observe(new(first.ExperimentId, ChaosExperimentStatus.Started, clock.GetUtcNow()));
        worker.Current.Status.ShouldBe(ExperimentStatus.Aborted);
        clock.Advance(TimeSpan.FromSeconds(59));
        Should.Throw<InvalidOperationException>(() => worker.Request(new(ChaosFault.Latency)));
        clock.Advance(TimeSpan.FromSeconds(1));
        var next = worker.Request(new(ChaosFault.Unavailable));
        worker.Observe(new(first.ExperimentId, ChaosExperimentStatus.Aborted, clock.GetUtcNow()));
        worker.Current.ShouldBe(next);
    }

    [Fact]
    public async Task ExpiredOrInvalidAcknowledgementCannotActivate()
    {
        var clock = new FakeTimeProvider();
        var worker = Create(clock);
        var run = worker.Request(new(ChaosFault.Latency));
        await worker.TickAsync(Safe(clock), default);
        Should.Throw<ArgumentException>(() => worker.Observe(new(run.ExperimentId, ChaosExperimentStatus.Unknown, clock.GetUtcNow())));
        worker.Current!.Status.ShouldBe(ExperimentStatus.Starting);
        clock.Advance(TimeSpan.FromSeconds(30));
        worker.Observe(new(run.ExperimentId, ChaosExperimentStatus.Started, clock.GetUtcNow()));
        worker.Current.Status.ShouldBe(ExperimentStatus.Expired);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("                                ")]
    public void AdministrativeKeyCannotDefaultToAnInsecureValue(string? key) =>
        Should.Throw<InvalidOperationException>(() => new ChaosAdminKey(key));

    private sealed class ResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
