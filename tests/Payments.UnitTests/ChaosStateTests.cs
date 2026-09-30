using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Payments.Api.Chaos;
using Payments.Api.Gateways;
using Shared.Contracts;
using Shouldly;
using Xunit;

namespace Payments.UnitTests;

[Trait("Category", "Unit")]
public sealed class ChaosStateTests
{
    private static ChaosState Create(FakeTimeProvider clock, string environment = "Development", bool enabled = true) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Chaos:Enabled"] = enabled.ToString() }).Build(),
            new EnvironmentStub(environment), clock, NullLogger<ChaosState>.Instance);

    [Fact]
    public async Task TtlExpiresWithoutBrokerOrWorker_DuplicateDoesNotExtendIt()
    {
        var clock = new FakeTimeProvider();
        using var state = Create(clock);
        var command = new ChaosStart(Guid.NewGuid(), ChaosFault.Unavailable, clock.GetUtcNow().AddSeconds(10));
        state.Start(command).Status.ShouldBe("started");
        await Should.ThrowAsync<GatewayUnavailableException>(() => state.ApplyAsync(default));
        clock.Advance(TimeSpan.FromSeconds(9));
        state.Start(command with { ExpiresAt = clock.GetUtcNow().AddSeconds(60) }).Status.ShouldBe("started");
        clock.Advance(TimeSpan.FromSeconds(2));
        await state.ApplyAsync(default);
        state.IsActive.ShouldBeFalse();
        state.DrainEvents().Single().Status.ShouldBe("expired");
        state.Start(command with { ExpiresAt = clock.GetUtcNow().AddSeconds(10) }).Status.ShouldBe("rejected");
    }

    [Fact]
    public async Task AbortInterruptsLatency_AndKillSwitchPreventsNewExperiments()
    {
        var clock = new FakeTimeProvider();
        using var state = Create(clock);
        var command = new ChaosStart(Guid.NewGuid(), ChaosFault.Latency, clock.GetUtcNow().AddSeconds(30), 5000);
        state.Start(command);
        var delay = state.ApplyAsync(default);
        delay.IsCompleted.ShouldBeFalse();
        state.Abort(new(command.ExperimentId)).ShouldNotBeNull();
        await delay.WaitAsync(TimeSpan.FromSeconds(2));
        state.Abort(new(null, true));
        state.Start(command with { ExperimentId = Guid.NewGuid() }).Status.ShouldBe("rejected");
    }

    [Fact]
    public async Task OriginalCancellationIsPropagated()
    {
        var clock = new FakeTimeProvider();
        using var state = Create(clock);
        state.Start(new(Guid.NewGuid(), ChaosFault.Latency, clock.GetUtcNow().AddSeconds(30)));
        using var ct = new CancellationTokenSource();
        var delay = state.ApplyAsync(ct.Token);
        ct.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => delay);
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Development", false)]
    public void DisabledOrProduction_RejectsStart(string environment, bool enabled)
    {
        var clock = new FakeTimeProvider();
        using var state = Create(clock, environment, enabled);
        state.Start(new(Guid.NewGuid(), ChaosFault.Unavailable, clock.GetUtcNow().AddSeconds(30))).Status.ShouldBe("rejected");
    }

    [Fact]
    public void AbortedBeforeDelivery_Expired_AndConcurrentCommandsAreRejected()
    {
        var clock = new FakeTimeProvider();
        using var state = Create(clock);
        var command = new ChaosStart(Guid.NewGuid(), ChaosFault.Unavailable, clock.GetUtcNow().AddSeconds(30));
        state.Abort(new(command.ExperimentId));
        state.Start(command).Status.ShouldBe("rejected");
        state.Start(command with { ExperimentId = Guid.NewGuid(), ExpiresAt = clock.GetUtcNow() }).Status.ShouldBe("rejected");
        state.Start(command with { ExperimentId = Guid.NewGuid() }).Status.ShouldBe("started");
        state.Start(command with { ExperimentId = Guid.NewGuid() }).Status.ShouldBe("rejected");
    }

    private sealed class EnvironmentStub(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
