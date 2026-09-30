using Microsoft.EntityFrameworkCore;

namespace Payments.Api;

public static class PaymentsDatabase
{
    public static Task InitializeAsync(PaymentsDb db, CancellationToken ct) => db.Database.MigrateAsync(ct);
}
