using Dapper;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Enms;
using Divakar.FactoryOS.Infrastructure.Database;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class GhgRepository(Db db) : IGhgRepository
{
    public async Task<IReadOnlyList<GhgActivity>> ActivitiesAsync(Guid tenantId, DateTime? from, DateTime? to, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<GhgActivity>(new CommandDefinition("""
            SELECT a.ActivityId, a.FactorId, f.Source, f.Name AS FactorName, f.Scope, f.Scope3Category, f.Method,
                   CAST(a.PeriodFrom AS datetime2) AS PeriodFrom, CAST(a.PeriodTo AS datetime2) AS PeriodTo, a.Quantity, f.Unit,
                   ISNULL(f.KgCo2ePerUnit, 0) AS KgCo2ePerUnit, a.Reference, a.Note, a.CreatedAt
            FROM enms.GhgActivities a
            JOIN enms.EmissionFactors f ON f.FactorId = a.FactorId
            WHERE a.TenantId = @tenantId AND (@from IS NULL OR a.PeriodTo >= @from) AND (@to IS NULL OR a.PeriodFrom < @to)
            ORDER BY a.PeriodFrom DESC, a.CreatedAt DESC
            """, new { tenantId, from = from?.Date, to = to?.Date }, cancellationToken: ct))];
    }

    public async Task<Guid> AddActivityAsync(Guid tenantId, SaveActivityRequest r, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT enms.GhgActivities (TenantId, FactorId, PeriodFrom, PeriodTo, Quantity, Reference, Note)
            OUTPUT inserted.ActivityId
            VALUES (@tenantId, @FactorId, @PeriodFrom, @PeriodTo, @Quantity, @Reference, @Note)
            """, new { tenantId, r.FactorId, PeriodFrom = r.PeriodFrom.Date, PeriodTo = r.PeriodTo.Date, r.Quantity, r.Reference, r.Note }, cancellationToken: ct));
    }

    public async Task DeleteActivityAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var n = await con.ExecuteAsync(new CommandDefinition("DELETE FROM enms.GhgActivities WHERE TenantId = @tenantId AND ActivityId = @id",
            new { tenantId, id }, cancellationToken: ct));
        if (n == 0)
        {
            throw AppException.NotFound("Activity");
        }
    }

    public async Task<int> UseGridRegionAsync(Guid tenantId, string region, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            UPDATE enms.EmissionFactors SET IsActive = CASE WHEN Region = @region THEN 1 ELSE 0 END, UpdatedAt = SYSUTCDATETIME()
            WHERE TenantId = @tenantId AND Source = 'electricity' AND Method = 'Location'
            """, new { tenantId, region }, cancellationToken: ct));
        return await con.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM enms.EmissionFactors
            WHERE TenantId = @tenantId AND Source = 'electricity' AND Method = 'Location' AND IsActive = 1
            """, new { tenantId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<FootprintOrder>> CompletedOrdersAsync(Guid tenantId, DateTime from, DateTime to, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<FootprintOrder>(new CommandDefinition("""
            SELECT w.WorkOrderId, w.Code, p.ProductId, p.Code AS ProductCode, p.Name AS ProductName, p.MassKg, a.AssetId, a.Name AS AssetName,
                   w.ActualStart, w.ActualEnd, w.GoodQty,
                   ISNULL((SELECT SUM(h.Rise) FROM telemetry.CounterHours h
                           JOIN plant.Tags t ON t.TagId = h.TagId AND t.AssetId = w.AssetId AND t.SensorTypeCode = 'energy_kwh'
                           WHERE h.TenantId = @tenantId
                             AND h.Hour >= DATEADD(HOUR, DATEDIFF(HOUR, 0, w.ActualStart), 0) AND h.Hour < w.ActualEnd), 0) AS Kwh
            FROM production.WorkOrders w
            JOIN production.Products p ON p.ProductId = w.ProductId
            JOIN plant.Assets a ON a.AssetId = w.AssetId
            WHERE w.TenantId = @tenantId AND w.Status = 'Completed' AND w.ActualStart IS NOT NULL AND w.ActualEnd IS NOT NULL
              AND w.ActualEnd >= @from AND w.ActualEnd < @to AND w.GoodQty > 0
            ORDER BY w.ActualEnd
            """, new { tenantId, from, to }, commandTimeout: 120, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<BomMaterial>> BomMaterialsAsync(Guid tenantId, IReadOnlyCollection<Guid> productIds, CancellationToken ct)
    {
        if (productIds.Count == 0)
        {
            return [];
        }
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<BomMaterial>(new CommandDefinition("""
            SELECT b.ProductId, i.Code AS ItemCode, i.Name AS ItemName, i.Unit, b.QtyPer, b.ScrapPct, i.KgCo2ePerUnit, i.Co2eSource, i.ItemId
            FROM production.BomLines b
            JOIN inventory.Items i ON i.ItemId = b.ItemId
            WHERE b.TenantId = @tenantId AND b.ProductId IN @productIds
            ORDER BY i.Code
            """, new { tenantId, productIds }, cancellationToken: ct))];
    }

    public async Task SetItemFactorAsync(Guid tenantId, Guid itemId, decimal? kgCo2ePerUnit, string? source, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var n = await con.ExecuteAsync(new CommandDefinition("""
            UPDATE inventory.Items SET KgCo2ePerUnit = @kgCo2ePerUnit, Co2eSource = @source WHERE TenantId = @tenantId AND ItemId = @itemId
            """, new { tenantId, itemId, kgCo2ePerUnit, source }, cancellationToken: ct));
        if (n == 0)
        {
            throw AppException.NotFound("Item");
        }
    }

    public async Task SetProductMassAsync(Guid tenantId, Guid productId, decimal? massKg, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var n = await con.ExecuteAsync(new CommandDefinition("UPDATE production.Products SET MassKg = @massKg WHERE TenantId = @tenantId AND ProductId = @productId",
            new { tenantId, productId, massKg }, cancellationToken: ct));
        if (n == 0)
        {
            throw AppException.NotFound("Product");
        }
    }
}
