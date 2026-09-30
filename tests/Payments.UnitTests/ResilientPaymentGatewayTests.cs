using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Payments.Api.Gateways;
using Shouldly;
using Xunit;

namespace Payments.UnitTests;

[Trait("Category", "Unit")]
public class ResilientPaymentGatewayTests
{
    private static readonly ChargeRequest Request = new(Guid.NewGuid(), 149.90m);
    private static readonly ChargeResult Approved = new(true, "primary", null);
    private static ResilientPaymentGateway Create(IPaymentGateway primary, IPaymentGateway fallback,
        ResilienceOptions? options = null, TimeProvider? clock = null,
        Func<ChargeRequest, CancellationToken, Task<ChargeResult?>>? reconcile = null) =>
        new(primary, fallback, reconcile ?? ((_, _) => Task.FromResult<ChargeResult?>(null)),
            options ?? new ResilienceOptions { RetryDelayMilliseconds = 1 },
            NullLogger<ResilientPaymentGateway>.Instance, clock);

    [Fact]
    public async Task TransientFailure_Retries_ThenSucceeds()
    {
        var primary = new Gateway((call, _) => call < 3
            ? Task.FromException<ChargeResult>(new GatewayUnavailableException()) : Task.FromResult(Approved));
        var fallback = new Gateway((_, _) => Task.FromResult(new ChargeResult(true, "fallback", null)));
        (await Create(primary, fallback).ChargeAsync(Request, default)).ShouldBe(Approved);
        primary.Calls.ShouldBe(3);
        fallback.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Decline_DoesNotRetryOrFallback()
    {
        var primary = new Gateway((_, _) => Task.FromResult(new ChargeResult(false, "primary", "declined")));
        var fallback = new Gateway((_, _) => Task.FromResult(Approved));
        (await Create(primary, fallback).ChargeAsync(Request, default)).Success.ShouldBeFalse();
        primary.Calls.ShouldBe(1);
        fallback.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Timeout_UsesFallback_AndOriginalCancellationDoesNot()
    {
        var primary = new Gateway(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Approved; });
        var fallback = new Gateway((_, _) => Task.FromResult(new ChargeResult(true, "fallback", null)));
        var gateway = Create(primary, fallback, new ResilienceOptions { TimeoutMilliseconds = 20, RetryDelayMilliseconds = 1 });
        (await gateway.ChargeAsync(Request, default)).Gateway.ShouldBe("fallback");
        primary.Calls.ShouldBe(3);
        fallback.Calls.ShouldBe(1);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => gateway.ChargeAsync(Request, cancelled.Token));
        fallback.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Circuit_OpensThenRecovers_AfterBreakDuration()
    {
        var clock = new FakeTimeProvider();
        var failed = true;
        var primary = new Gateway((_, _) => failed ? Task.FromException<ChargeResult>(new GatewayUnavailableException()) : Task.FromResult(Approved));
        var fallback = new Gateway((_, _) => Task.FromResult(new ChargeResult(true, "fallback", null)));
        var gateway = Create(primary, fallback, new ResilienceOptions { Retries = 0, MinimumThroughput = 2 }, clock);
        await gateway.ChargeAsync(Request, default);
        await gateway.ChargeAsync(Request, default);
        await gateway.ChargeAsync(Request, default);
        primary.Calls.ShouldBe(2);
        failed = false;
        clock.Advance(TimeSpan.FromSeconds(11));
        (await gateway.ChargeAsync(Request, default)).Gateway.ShouldBe("primary");
        primary.Calls.ShouldBe(3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqlProviderCancellation_PreservesTimeoutAndCallerCancellation(bool callerCancels)
    {
        var primary = new SimulatedPrimaryGateway(new ChargeLedger(new CancelledSqlFactory()), new ConfigurationBuilder().Build());
        var fallback = new Gateway((_, _) => Task.FromResult(new ChargeResult(true, "fallback", null)));
        var gateway = Create(primary, fallback, new ResilienceOptions { Retries = 0, TimeoutMilliseconds = callerCancels ? 5000 : 20 });
        using var caller = new CancellationTokenSource();
        if (callerCancels)
        {
            caller.CancelAfter(20);
            await Should.ThrowAsync<OperationCanceledException>(() => gateway.ChargeAsync(Request, caller.Token));
            fallback.Calls.ShouldBe(0);
        }
        else
        {
            (await gateway.ChargeAsync(Request, caller.Token)).Gateway.ShouldBe("fallback");
            fallback.Calls.ShouldBe(1);
        }
    }

    [Fact]
    public async Task AmbiguousTimeout_ReconcilesCommittedCharge_WithoutFallback()
    {
        var primary = new Gateway((_, _) => Task.FromException<ChargeResult>(new Polly.Timeout.TimeoutRejectedException()));
        var fallback = new Gateway((_, _) => Task.FromResult(Approved));
        var gateway = Create(primary, fallback, new ResilienceOptions { Retries = 0 },
            reconcile: (_, _) => Task.FromResult<ChargeResult?>(Approved));
        (await gateway.ChargeAsync(Request, default)).ShouldBe(Approved);
        fallback.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task PermanentFailure_DoesNotRetryOrFallback()
    {
        var primary = new Gateway((_, _) => Task.FromException<ChargeResult>(new ArgumentException("mismatched amount")));
        var fallback = new Gateway((_, _) => Task.FromResult(Approved));
        await Should.ThrowAsync<ArgumentException>(() => Create(primary, fallback).ChargeAsync(Request, default));
        primary.Calls.ShouldBe(1);
        fallback.Calls.ShouldBe(0);
    }

    private sealed class CancelledSqlFactory : IDbContextFactory<SimulatedGatewayDb>
    {
        public SimulatedGatewayDb CreateDbContext() => throw new NotSupportedException();
        public async Task<SimulatedGatewayDb> CreateDbContextAsync(CancellationToken ct = default)
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { throw new DbUpdateException("Operation cancelled by user"); }
            throw new InvalidOperationException("Unreachable");
        }
    }

    private sealed class Gateway(Func<int, CancellationToken, Task<ChargeResult>> action) : IPaymentGateway
    {
        public int Calls { get; private set; }
        public Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken ct) => action(++Calls, ct);
    }
}
