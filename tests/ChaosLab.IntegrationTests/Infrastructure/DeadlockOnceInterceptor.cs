using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ChaosLab.IntegrationTests.Infrastructure;

// Induce a real SQL deadlock after the charge has committed. The consumer's
// transaction is the low-priority victim; no fabricated provider exceptions.
public sealed class DeadlockOnceInterceptor(string connectionString) : SaveChangesInterceptor
{
    private int _attempts;
    public int SqlError { get; private set; }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        var db = eventData.Context!;
        if (!db.ChangeTracker.Entries().Any(e => e.Metadata.ClrType.Name == "Payment" && e.State == EntityState.Added) ||
            Interlocked.Increment(ref _attempts) != 1) return result;
        db.Database.SetCommandTimeout(20);
        await db.Database.ExecuteSqlRawAsync("SET DEADLOCK_PRIORITY LOW; UPDATE DeadlockProbe SET Value = Value + 1 WHERE Id = 1", ct);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = new SqlCommand("UPDATE DeadlockProbe SET Value = Value + 1 WHERE Id = 2", connection, transaction);
        command.CommandTimeout = 20;
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = "UPDATE DeadlockProbe SET Value = Value + 1 WHERE Id = 1";
        var blocked = command.ExecuteNonQueryAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE DeadlockProbe SET Value = Value + 1 WHERE Id = 2", ct);
            throw new InvalidOperationException("SQL did not select the consumer transaction as the deadlock victim");
        }
        catch (SqlException ex) { SqlError = ex.Number; throw; }
        finally
        {
            await blocked;
            await transaction.CommitAsync(ct);
        }
    }
}
