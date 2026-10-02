using MassTransit;
using Microsoft.EntityFrameworkCore;
using Payments.Api.Domain;
using Shared.Contracts;
using Payments.Api.Infrastructure.Gateways;

namespace Payments.Api;

public class PaymentsDb(DbContextOptions<PaymentsDb> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<SimulatedCharge> SimulatedCharges => Set<SimulatedCharge>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<SimulatedCharge>(SimulatedChargeMapping.Configure);
        b.Entity<Payment>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.OrderId).IsUnique();
            e.Property(p => p.Amount).HasPrecision(18, 2);
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.Gateway).HasConversion(g => g.ToWireName(), s => PaymentGatewayNames.Parse(s)).HasMaxLength(50);
        });

        b.AddInboxStateEntity();
        b.AddOutboxMessageEntity();
        b.AddOutboxStateEntity();
    }
}
