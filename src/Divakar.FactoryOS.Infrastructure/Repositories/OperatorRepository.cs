using Dapper;
using Divakar.FactoryOS.Application.Production;
using Divakar.FactoryOS.Infrastructure.Database;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class OperatorRepository(Db db) : IOperatorRepository
{
    public async Task<IReadOnlyList<OperatorStats>> StatsAsync(Guid tenantId, DateTime since, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<OperatorStats>(new CommandDefinition("""
            SELECT a.AssetId,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM plant.Tags t WHERE t.AssetId = a.AssetId AND t.IsActive = 1 AND t.SensorTypeCode = 'part_count'
                                          AND (t.Address IS NULL OR t.Address NOT LIKE 'virtual:manual-%')) THEN 1 ELSE 0 END AS bit) AS CountsParts,
                   (SELECT COUNT(*) FROM production.Downtimes d
                    WHERE d.AssetId = a.AssetId AND d.ReasonCode IS NULL AND d.StartTs >= DATEADD(DAY, -2, @since)) AS PendingDowntimes,
                   w.WorkOrderId, w.Code AS WorkOrderCode, p.Name AS ProductName
            FROM plant.Assets a
            LEFT JOIN production.WorkOrders w ON w.AssetId = a.AssetId AND w.Status = 'Running'
            LEFT JOIN production.Products p ON p.ProductId = w.ProductId
            WHERE a.TenantId = @tenantId AND a.IsActive = 1
            """, new { tenantId, since }, cancellationToken: ct))];
    }

    public async Task<(Guid GoodTag, Guid ScrapTag)> CounterTagsAsync(Guid tenantId, Guid assetId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        async Task<Guid> Ensure(string code, string name, string sensor, string address) =>
            await con.ExecuteScalarAsync<Guid?>(new CommandDefinition("SELECT TagId FROM plant.Tags WHERE AssetId = @assetId AND Address = @address",
                new { assetId, address }, cancellationToken: ct))
            ?? await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
                INSERT plant.Tags (TenantId, AssetId, Code, Name, SensorTypeCode, Unit, Address) OUTPUT inserted.TagId
                VALUES (@tenantId, @assetId, @code, @name, @sensor, 'pcs', @address)
                """, new { tenantId, assetId, code, name, sensor, address }, cancellationToken: ct));
        return (await Ensure("op_good", "Good parts (entered)", "part_count", ManualCountTags.Good),
                await Ensure("op_scrap", "Scrap parts (entered)", "scrap_count", ManualCountTags.Scrap));
    }

    public async Task<(long EntryId, int TotalGood, int TotalScrap)> AddEntryAsync(Guid tenantId, CountRequest r, Guid? userId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("EXEC sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000",
            new { res = $"op-count-{r.AssetId}" }, tx, cancellationToken: ct));
        var row = await con.QuerySingleAsync<(long, int, int)>(new CommandDefinition("""
            INSERT production.OperatorEntries (TenantId, AssetId, Good, Scrap, ScrapReason, Note, EnteredBy)
            VALUES (@tenantId, @AssetId, @Good, @Scrap, @ScrapReason, @Note, @userId);
            SELECT CAST(SCOPE_IDENTITY() AS bigint), SUM(Good), SUM(Scrap) FROM production.OperatorEntries WHERE AssetId = @AssetId;
            """, new { tenantId, r.AssetId, r.Good, r.Scrap, r.ScrapReason, r.Note, userId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return row;
    }

    public async Task<IReadOnlyList<OperatorEntry>> EntriesAsync(Guid tenantId, Guid? assetId, DateTime since, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<OperatorEntry>(new CommandDefinition("""
            SELECT TOP (200) e.EntryId, e.AssetId, a.Name AS AssetName, e.Good, e.Scrap, e.ScrapReason, e.Note, u.DisplayName AS EnteredBy, e.EnteredAt
            FROM production.OperatorEntries e
            JOIN plant.Assets a ON a.AssetId = e.AssetId
            LEFT JOIN core.Users u ON u.UserId = e.EnteredBy
            WHERE e.TenantId = @tenantId AND e.EnteredAt >= @since AND (@assetId IS NULL OR e.AssetId = @assetId)
            ORDER BY e.EnteredAt DESC
            """, new { tenantId, assetId, since }, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<string>> RecentScrapReasonsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<string>(new CommandDefinition("""
            SELECT TOP (8) ScrapReason FROM production.OperatorEntries
            WHERE TenantId = @tenantId AND ScrapReason IS NOT NULL AND EnteredAt >= DATEADD(DAY, -60, SYSUTCDATETIME())
            GROUP BY ScrapReason ORDER BY COUNT(*) DESC
            """, new { tenantId }, cancellationToken: ct))];
    }
}
