using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Payments.Api;

public static class PaymentsDatabase
{
    public static async Task InitializeAsync(PaymentsDb db, bool adoptLegacy, CancellationToken ct)
    {
        if (await db.Database.CanConnectAsync(ct) && !await db.GetService<IHistoryRepository>().ExistsAsync(ct))
        {
            var columns = await db.Database.SqlQueryRaw<SchemaColumn>("""
                SELECT TABLE_SCHEMA + '.' + TABLE_NAME AS TableName, COLUMN_NAME AS ColumnName,
                    DATA_TYPE + CASE
                    WHEN DATA_TYPE IN ('nvarchar', 'varchar', 'varbinary', 'char', 'nchar', 'binary')
                        THEN '(' + CASE WHEN CHARACTER_MAXIMUM_LENGTH = -1 THEN 'max' ELSE CAST(CHARACTER_MAXIMUM_LENGTH AS varchar) END + ')'
                    WHEN DATA_TYPE IN ('decimal', 'numeric')
                        THEN '(' + CAST(NUMERIC_PRECISION AS varchar) + ',' + CAST(NUMERIC_SCALE AS varchar) + ')'
                    ELSE '' END AS StoreType,
                    CAST(CASE WHEN IS_NULLABLE = 'YES' THEN 1 ELSE 0 END AS bit) AS Nullable
                FROM INFORMATION_SCHEMA.COLUMNS
                """).ToListAsync(ct);
            if (columns.Count > 0)
            {
                if (!adoptLegacy)
                    throw new InvalidOperationException("Legacy Payments database has no migration history. Back it up, stop writers, then run Payments.Api --adopt-legacy-database.");

                var expected = db.Model.GetRelationalModel().Tables.Where(t => t.Name != "SimulatedCharges")
                    .SelectMany(t => t.Columns.Select(c => new SchemaColumn
                    {
                        TableName = (t.Schema ?? "dbo") + "." + t.Name, ColumnName = c.Name,
                        StoreType = c.StoreType.Replace("rowversion", "timestamp"), Nullable = c.IsNullable
                    })).Select(Signature).Order().ToArray();
                if (!expected.SequenceEqual(columns.Select(Signature).Order()))
                    throw new InvalidOperationException("Unknown legacy Payments schema; migration adoption refused. No data was changed.");
                var uniqueIndexes = await db.Database.SqlQueryRaw<int>("""
                    SELECT COUNT(*) AS Value FROM sys.indexes
                    WHERE is_unique = 1 AND
                    ((object_id = OBJECT_ID('dbo.Payments') AND name = 'IX_Payments_OrderId') OR
                     (object_id = OBJECT_ID('dbo.InboxState') AND name = 'AK_InboxState_MessageId_ConsumerId'))
                    """).SingleAsync(ct);
                if (uniqueIndexes != 2) throw new InvalidOperationException("Legacy idempotency indexes are missing; adoption refused.");

                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var history = db.GetService<IHistoryRepository>();
                await db.Database.ExecuteSqlRawAsync(history.GetCreateScript(), ct);
                var initial = db.Database.GetMigrations().First();
                await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(initial, "9.0.1")), ct);
                await tx.CommitAsync(ct);
            }
        }
        await db.Database.MigrateAsync(ct);
    }

    private static string Signature(SchemaColumn c) => $"{c.TableName}|{c.ColumnName}|{c.StoreType}|{c.Nullable}";
    private sealed class SchemaColumn
    {
        public string TableName { get; set; } = "";
        public string ColumnName { get; set; } = "";
        public string StoreType { get; set; } = "";
        public bool Nullable { get; set; }
    }
}
