using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Shared.Infrastructure;

public static class Database
{
    public static bool IsTransient(Exception exception) => exception switch
    {
        SqlException sql => sql.IsTransient || sql.Number is -2 or 1205,
        DbUpdateException { InnerException: SqlException sql } =>
            sql.IsTransient || sql.Number is -2 or 1205 or 2601 or 2627,
        _ => false
    };

    public static async Task InitializeAsync<T>(IServiceProvider services, CancellationToken ct,
        Func<T, CancellationToken, Task>? initialize = null) where T : DbContext
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseStartup");
        for (var attempt = 1; ; attempt++)
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<T>();
            try
            {
                if (initialize is null) await db.Database.EnsureCreatedAsync(ct);
                else await initialize(db, ct);
                return;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < 15 && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Database {Database} startup attempt {Attempt} failed", typeof(T).Name, attempt);
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }
}

public sealed class DatabaseHealthCheck<T>(IServiceScopeFactory scopes) : IHealthCheck where T : DbContext
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<T>().Database.CanConnectAsync(ct)
            ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Database is unavailable");
    }
}
