using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Contracts;

namespace Payments.Api.Gateways;

public sealed class SimulatedCharge
{
    public Guid OrderId { get; set; }
    public decimal Amount { get; set; }
    public bool Success { get; set; }
    public string Gateway { get; set; } = "";
    public string? FailureReason { get; set; }
    public DateTimeOffset CompletedAt { get; set; }

    public static void Configure(EntityTypeBuilder<SimulatedCharge> entity)
    {
        entity.ToTable("SimulatedCharges");
        entity.HasKey(x => x.OrderId);
        entity.Property(x => x.Amount).HasPrecision(18, 2);
        entity.Property(x => x.Gateway).HasMaxLength(50);
    }
}

// A separate connection/transaction models a gateway charge surviving consumer rollback.
// PaymentsDb owns this table's migrations. Never EnsureCreated/Migrate this context.
public sealed class SimulatedGatewayDb(DbContextOptions<SimulatedGatewayDb> options) : DbContext(options)
{
    public DbSet<SimulatedCharge> Charges => Set<SimulatedCharge>();
    protected override void OnModelCreating(ModelBuilder builder) => builder.Entity<SimulatedCharge>(SimulatedCharge.Configure);
}

public sealed class ChargeLedger(IDbContextFactory<SimulatedGatewayDb> factory)
{
    public async Task<ChargeResult?> FindAsync(ChargeRequest request, CancellationToken ct)
    {
        Validate(request);
        await using var db = await factory.CreateDbContextAsync(ct);
        var charge = await db.Charges.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == request.OrderId, ct);
        if (charge is null) return null;
        if (charge.Amount != request.Amount)
            throw new ArgumentException("An idempotency key cannot be reused with a different amount");
        return new ChargeResult(charge.Success, charge.Gateway, charge.FailureReason);
    }

    public async Task<ChargeResult> RecordAsync(ChargeRequest request, ChargeResult result, CancellationToken ct)
    {
        Validate(request);
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Charges.Add(new SimulatedCharge
        {
            OrderId = request.OrderId, Amount = request.Amount, Success = result.Success,
            Gateway = result.Gateway, FailureReason = result.FailureReason, CompletedAt = DateTimeOffset.UtcNow
        });
        try
        {
            // This INSERT is the simulated financial effect; there is no external charge after it.
            await db.SaveChangesAsync(ct);
            return result;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            return await FindAsync(request, ct) ?? throw new InvalidOperationException("Idempotent result disappeared", ex);
        }
    }

    public static void Validate(ChargeRequest request)
    {
        if (request.OrderId == Guid.Empty || !Money.IsValid(request.Amount))
            throw new ArgumentException("Charge requires an order id and a valid monetary amount");
    }
}
