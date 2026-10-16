using Dapper;
using Divakar.FactoryOS.Application.Production;
using Divakar.FactoryOS.Infrastructure.Database;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class PlanningRepository(Db db) : IPlanningRepository
{
    public async Task<IReadOnlyList<BomLine>> BomAsync(Guid tenantId, Guid productId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<BomLine>(new CommandDefinition("""
            SELECT b.ItemId, i.Code AS ItemCode, i.Name AS ItemName, i.Unit, b.QtyPer, b.ScrapPct,
                   ISNULL((SELECT SUM(m.Quantity) FROM inventory.Movements m WHERE m.ItemId = b.ItemId), 0) AS OnHand
            FROM production.BomLines b JOIN inventory.Items i ON i.ItemId = b.ItemId
            WHERE b.TenantId = @tenantId AND b.ProductId = @productId
            ORDER BY i.Code
            """, new { tenantId, productId }, cancellationToken: ct))];
    }

    public async Task SaveBomAsync(Guid tenantId, Guid productId, IReadOnlyList<BomLineInput> lines, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("DELETE production.BomLines WHERE TenantId = @tenantId AND ProductId = @productId",
            new { tenantId, productId }, tx, cancellationToken: ct));
        await con.ExecuteAsync(new CommandDefinition("""
            INSERT production.BomLines (TenantId, ProductId, ItemId, QtyPer, ScrapPct) VALUES (@tenantId, @productId, @ItemId, @QtyPer, @ScrapPct)
            """, lines.Select(l => new { tenantId, productId, l.ItemId, l.QtyPer, l.ScrapPct }), tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<RoutingStep>> RoutingAsync(Guid tenantId, Guid productId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<RoutingStep>(new CommandDefinition("""
            SELECT r.Seq, r.Name, r.AssetId, a.Name AS AssetName, r.StdCycleSec, r.SetupMinutes
            FROM production.RoutingSteps r LEFT JOIN plant.Assets a ON a.AssetId = r.AssetId
            WHERE r.TenantId = @tenantId AND r.ProductId = @productId ORDER BY r.Seq
            """, new { tenantId, productId }, cancellationToken: ct))];
    }

    public async Task SaveRoutingAsync(Guid tenantId, Guid productId, IReadOnlyList<RoutingStepInput> steps, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("DELETE production.RoutingSteps WHERE TenantId = @tenantId AND ProductId = @productId",
            new { tenantId, productId }, tx, cancellationToken: ct));
        await con.ExecuteAsync(new CommandDefinition("""
            INSERT production.RoutingSteps (TenantId, ProductId, Seq, Name, AssetId, StdCycleSec, SetupMinutes)
            VALUES (@tenantId, @productId, @Seq, @Name, @AssetId, @StdCycleSec, @SetupMinutes)
            """, steps.Select((s, i) => new { tenantId, productId, Seq = (i + 1) * 10, s.Name, s.AssetId, s.StdCycleSec, s.SetupMinutes }), tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task CopyRoutingAsync(Guid tenantId, Guid workOrderId, Guid productId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            INSERT production.WorkOrderSteps (TenantId, WorkOrderId, Seq, Name, AssetId, StdCycleSec)
            SELECT TenantId, @workOrderId, Seq, Name, AssetId, StdCycleSec FROM production.RoutingSteps WHERE TenantId = @tenantId AND ProductId = @productId
            """, new { tenantId, workOrderId, productId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<WorkOrderStep>> StepsAsync(Guid tenantId, Guid workOrderId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<WorkOrderStep>(new CommandDefinition("""
            SELECT s.Seq, s.Name, s.AssetId, a.Name AS AssetName, s.StdCycleSec, s.Status, s.GoodQty, s.ScrapQty, s.StartedAt, s.DoneAt
            FROM production.WorkOrderSteps s LEFT JOIN plant.Assets a ON a.AssetId = s.AssetId
            WHERE s.TenantId = @tenantId AND s.WorkOrderId = @workOrderId ORDER BY s.Seq
            """, new { tenantId, workOrderId }, cancellationToken: ct))];
    }

    public async Task SetStepAsync(Guid tenantId, Guid workOrderId, int seq, string status, int? good, int? scrap, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            UPDATE production.WorkOrderSteps
            SET Status = @status, GoodQty = COALESCE(@good, GoodQty), ScrapQty = COALESCE(@scrap, ScrapQty),
                StartedAt = CASE WHEN @status = 'Running' THEN SYSUTCDATETIME() ELSE StartedAt END,
                DoneAt = CASE WHEN @status = 'Done' THEN SYSUTCDATETIME() ELSE DoneAt END
            WHERE TenantId = @tenantId AND WorkOrderId = @workOrderId AND Seq = @seq
            """, new { tenantId, workOrderId, seq, status, good, scrap }, cancellationToken: ct));
    }

    private sealed record IssuedRow(Guid ItemId, decimal Qty);

    public async Task<IReadOnlyDictionary<Guid, decimal>> IssuedAsync(Guid tenantId, Guid workOrderId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var rows = await con.QueryAsync<IssuedRow>(new CommandDefinition("""
            SELECT ItemId, -SUM(Quantity) AS Qty FROM inventory.Movements
            WHERE TenantId = @tenantId AND WorkOrderId = @workOrderId AND Type = 'Issue' GROUP BY ItemId
            """, new { tenantId, workOrderId }, cancellationToken: ct));
        return rows.ToDictionary(r => r.ItemId, r => r.Qty);
    }

    public async Task<IReadOnlyList<OpenLot>> OpenLotsAsync(Guid tenantId, Guid itemId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<OpenLot>(new CommandDefinition("""
            SELECT l.LotId, l.LotNo, x.OnHand FROM inventory.Lots l
            CROSS APPLY (SELECT ISNULL(SUM(m.Quantity), 0) AS OnHand FROM inventory.Movements m WHERE m.LotId = l.LotId) x
            WHERE l.TenantId = @tenantId AND l.ItemId = @itemId AND l.Status = 'Available' AND x.OnHand > 0
            ORDER BY l.CreatedAt, l.LotNo
            """, new { tenantId, itemId }, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<Guid>> ItemIdsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Guid>(new CommandDefinition("SELECT ItemId FROM inventory.Items WHERE TenantId = @tenantId", new { tenantId }, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<Guid>> AssetIdsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Guid>(new CommandDefinition("SELECT AssetId FROM plant.Assets WHERE TenantId = @tenantId", new { tenantId }, cancellationToken: ct))];
    }
}
