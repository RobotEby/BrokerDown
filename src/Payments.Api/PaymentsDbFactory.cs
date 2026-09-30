using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Payments.Api;

// Schema tooling never needs a running host, broker, or real credentials.
public sealed class PaymentsDbFactory : IDesignTimeDbContextFactory<PaymentsDb>
{
    public PaymentsDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<PaymentsDb>()
        .UseSqlServer("Server=localhost;Database=PaymentsDb;Integrated Security=True;TrustServerCertificate=True")
        .Options);
}
