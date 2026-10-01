extern alias PaymentsApi;

using ChaosLab.IntegrationTests.Infrastructure;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaymentsApi::Payments.Api;
using PaymentsApi::Payments.Api.Domain;
using PaymentsApi::Payments.Api.Infrastructure.Gateways;
using PaymentsApi::Payments.Api.Application;
using Shared.Contracts;
using Shouldly;
using Xunit;

namespace ChaosLab.IntegrationTests.Flows;

[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Integration")]
public sealed class GatewayPersistenceTests(InfrastructureFixture infra)
{
    private string Connection() => ConnectionStrings.ForDatabase(infra.Sql.GetConnectionString(), DbNames.New("Ledger"));
    private static PaymentsDb Db(string connection) => new(new DbContextOptionsBuilder<PaymentsDb>().UseSqlServer(connection).Options);
    private static ChargeLedger Ledger(string connection) => new(new GatewayFactory(connection));
    private static IConfiguration Config(double failureRate = 0) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
    { ["Gateway:FailureRate"] = failureRate.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Gateway:LatencyMilliseconds"] = "0" }).Build();

    [Fact]
    public async Task ConcurrentGateways_AndRestart_ReturnOneDurableCharge()
    {
        var connection = Connection();
        await using var db = Db(connection);
        await PaymentsDatabase.InitializeAsync(db, default);
        var ledger = Ledger(connection);
        IPaymentGateway primary = new SimulatedPrimaryGateway(ledger, Config());
        IPaymentGateway fallback = new SimulatedFallbackGateway(ledger, Config());
        var request = new ChargeRequest(Guid.NewGuid(), 149.90m);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => (i % 2 == 0 ? primary : fallback).ChargeAsync(request, default)));
        results.Distinct().Count().ShouldBe(1);
        (await db.SimulatedCharges.CountAsync()).ShouldBe(1);
        (await new SimulatedPrimaryGateway(Ledger(connection), Config()).ChargeAsync(request, default)).ShouldBe(results[0]);
        await Should.ThrowAsync<ArgumentException>(() => primary.ChargeAsync(request with { Amount = 100m }, default));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task SimulatedGateway_PreservesApprovalOrDecline(double rate, bool approved)
    {
        var connection = Connection();
        await using var db = Db(connection);
        await PaymentsDatabase.InitializeAsync(db, default);
        var request = new ChargeRequest(Guid.NewGuid(), 20m);
        var result = await new SimulatedPrimaryGateway(Ledger(connection), Config(rate)).ChargeAsync(request, default);
        result.Success.ShouldBe(approved);
        (await new SimulatedFallbackGateway(Ledger(connection), Config()).ChargeAsync(request, default)).ShouldBe(result);
    }

    [Fact]
    public async Task ChargeSurvivesConsumerRollback_RedeliveryCompletesWithoutAnotherCharge()
    {
        var connection = Connection();
        var gateway = new SimulatedPrimaryGateway(Ledger(connection), Config());
        await using var harness = await PaymentsConsumerHarness.StartAsync(connection, gateway);
        var message = new OrderCreated(Guid.NewGuid(), Guid.NewGuid(), 100m, DateTimeOffset.UtcNow);
        var id = Guid.NewGuid();
        harness.Interceptor.ShouldFail = true;
        await harness.Harness.Bus.Publish(message, c => c.MessageId = id);
        await Eventually.Until(() => harness.Harness.Published.Any<Fault<OrderCreated>>(), TimeSpan.FromSeconds(30),
            description: "consumer fault published");
        (await harness.Harness.Consumed.Any<PaymentProcessed>()).ShouldBeFalse();
        await using (var db = Db(connection))
        {
            (await db.SimulatedCharges.CountAsync()).ShouldBe(1);
            (await db.Payments.CountAsync()).ShouldBe(0);
        }
        harness.Interceptor.ShouldFail = false;
        await harness.Harness.Bus.Publish(message, c => c.MessageId = id);
        await Eventually.Until(async () =>
        {
            await using var db = Db(connection);
            return await db.Payments.AnyAsync(p => p.OrderId == message.OrderId);
        }, description: "redelivered payment committed");
        await Eventually.Until(() => harness.Harness.Consumed.Any<PaymentProcessed>(), TimeSpan.FromSeconds(30),
            description: "PaymentProcessed consumed after redelivery");
        await using var verify = Db(connection);
        (await verify.SimulatedCharges.CountAsync()).ShouldBe(1);
        (await verify.Payments.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task DistinctMessagesForSameOrder_DoNotDuplicateTheFinancialEffect()
    {
        var connection = Connection();
        var gateway = new SimulatedPrimaryGateway(Ledger(connection), Config());
        await using var harness = await PaymentsConsumerHarness.StartAsync(connection, gateway);
        var message = new OrderCreated(Guid.NewGuid(), Guid.NewGuid(), 100m, DateTimeOffset.UtcNow);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => harness.Harness.Bus.Publish(message)));
        await Eventually.Until(async () =>
        {
            await using var db = Db(connection);
            return await db.Set<MassTransit.EntityFrameworkCoreIntegration.InboxState>().CountAsync(i => i.Delivered != null) == 4;
        }, TimeSpan.FromSeconds(30), description: "all distinct duplicate deliveries committed");
        await using var verify = Db(connection);
        (await verify.SimulatedCharges.CountAsync()).ShouldBe(1);
        (await verify.Payments.CountAsync()).ShouldBe(1);
        harness.Harness.Consumed.Select<PaymentProcessed>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task RealSqlDeadlockRetriesConsumer_WithoutDuplicatingChargeOrResult()
    {
        var connection = Connection();
        var failure = new DeadlockOnceInterceptor(connection);
        await using var harness = await PaymentsConsumerHarness.StartAsync(connection,
            new SimulatedPrimaryGateway(Ledger(connection), Config()), failure);
        await using var db = Db(connection);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE DeadlockProbe (Id int NOT NULL PRIMARY KEY, Value int NOT NULL); INSERT INTO DeadlockProbe VALUES (1,0),(2,0)");
        var message = new OrderCreated(Guid.NewGuid(), Guid.NewGuid(), 100m, DateTimeOffset.UtcNow);
        await harness.Harness.Bus.Publish(message);
        await Eventually.Until(() => harness.Harness.Consumed.Any<PaymentProcessed>(m => m.Context.Message.OrderId == message.OrderId),
            TimeSpan.FromSeconds(30), description: "consumer recovers after SQL deadlock");
        failure.SqlError.ShouldBe(1205);
        (await db.Payments.CountAsync()).ShouldBe(1);
        (await db.SimulatedCharges.CountAsync()).ShouldBe(1);
        harness.Harness.Consumed.Select<PaymentProcessed>().Count().ShouldBe(1);
        (await harness.Harness.Published.Any<Fault<OrderCreated>>()).ShouldBeFalse();
    }

    [Fact]
    public async Task PreviousMigrationsAndLowercaseGatewayValuesRemainReadable()
    {
        var connection = Connection();
        await using var db = Db(connection);
        await PaymentsDatabase.InitializeAsync(db, default);
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Payments (Id, OrderId, Amount, Status, Gateway, CreatedAt) VALUES ({Guid.NewGuid()}, {id}, {10m}, N'Approved', N'primary', {DateTimeOffset.UtcNow})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO SimulatedCharges (OrderId, Amount, Success, Gateway, CompletedAt) VALUES ({id}, {10m}, {true}, N'primary', {DateTimeOffset.UtcNow})");
        // Initial migrations are unchanged: reopening exercises the existing schema/data path.
        await using var reopened = Db(connection);
        await PaymentsDatabase.InitializeAsync(reopened, default);
        reopened.Database.HasPendingModelChanges().ShouldBeFalse();
        (await reopened.Database.GetAppliedMigrationsAsync()).Count().ShouldBe(2);
        (await reopened.Payments.SingleAsync()).Gateway.ShouldBe(PaymentGateway.Primary);
        (await Ledger(connection).FindAsync(new(id, 10m), default))!.Gateway.ShouldBe(PaymentGateway.Primary);
    }

    private sealed class GatewayFactory(string connection) : IDbContextFactory<SimulatedGatewayDb>
    {
        public SimulatedGatewayDb CreateDbContext() => new(new DbContextOptionsBuilder<SimulatedGatewayDb>().UseSqlServer(connection).Options);
    }
}
