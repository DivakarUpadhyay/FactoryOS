using Dapper;
using Divakar.FactoryOS.Application.QualityControl;
using Divakar.FactoryOS.Infrastructure.Database;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class InspectionRepository(Db db) : IInspectionRepository
{
    private sealed record PlanRow(Guid PlanId, Guid ProductId, string ProductName, string Name, string Trigger, int? EveryN, bool CreateNcrOnFail, bool IsActive);
    private sealed record ItemRow(Guid PlanId, int Seq, string Text, Guid? CharId, string? CharName, string? Unit, double? Lsl, double? Usl);

    public async Task<IReadOnlyList<InspectionPlan>> PlansAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var plans = await con.QueryAsync<PlanRow>(new CommandDefinition("""
            SELECT p.PlanId, p.ProductId, pr.Name AS ProductName, p.Name, p.TriggerType AS [Trigger], p.EveryN, p.CreateNcrOnFail, p.IsActive
            FROM quality.InspectionPlans p JOIN production.Products pr ON pr.ProductId = p.ProductId
            WHERE p.TenantId = @tenantId ORDER BY pr.Name, p.Name
            """, new { tenantId }, cancellationToken: ct));
        var items = (await con.QueryAsync<ItemRow>(new CommandDefinition("""
            SELECT i.PlanId, i.Seq, i.Text, i.CharId, c.Name AS CharName, c.Unit, c.Lsl, c.Usl
            FROM quality.InspectionItems i LEFT JOIN quality.Characteristics c ON c.CharId = i.CharId
            WHERE i.TenantId = @tenantId ORDER BY i.Seq
            """, new { tenantId }, cancellationToken: ct))).ToLookup(i => i.PlanId);
        return [.. plans.Select(p => new InspectionPlan(p.PlanId, p.ProductId, p.ProductName, p.Name, p.Trigger, p.EveryN, p.CreateNcrOnFail, p.IsActive,
            [.. items[p.PlanId].Select(i => new InspectionItem(i.Seq, i.Text, i.CharId, i.CharName, i.Unit, i.Lsl, i.Usl))]))];
    }

    public async Task<Guid> SavePlanAsync(Guid tenantId, Guid? id, SaveInspectionPlanRequest r, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);
        var args = new { tenantId, id, r.ProductId, r.Name, r.Trigger, r.EveryN, r.CreateNcrOnFail, r.IsActive };
        Guid planId;
        if (id is { } existing)
        {
            await con.ExecuteAsync(new CommandDefinition("""
                UPDATE quality.InspectionPlans SET ProductId = @ProductId, Name = @Name, TriggerType = @Trigger, EveryN = @EveryN,
                       CreateNcrOnFail = @CreateNcrOnFail, IsActive = @IsActive
                WHERE TenantId = @tenantId AND PlanId = @id;
                DELETE quality.InspectionItems WHERE TenantId = @tenantId AND PlanId = @id;
                """, args, tx, cancellationToken: ct));
            planId = existing;
        }
        else
        {
            planId = await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
                INSERT quality.InspectionPlans (TenantId, ProductId, Name, TriggerType, EveryN, CreateNcrOnFail, IsActive) OUTPUT inserted.PlanId
                VALUES (@tenantId, @ProductId, @Name, @Trigger, @EveryN, @CreateNcrOnFail, @IsActive)
                """, args, tx, cancellationToken: ct));
        }
        await con.ExecuteAsync(new CommandDefinition("INSERT quality.InspectionItems (TenantId, PlanId, Seq, Text, CharId) VALUES (@tenantId, @planId, @Seq, @Text, @CharId)",
            r.Items.Select((i, n) => new { tenantId, planId, Seq = n + 1, i.Text, i.CharId }), tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return planId;
    }

    private sealed record CountRow(Guid PlanId, Guid WorkOrderId, int N);

    public async Task<IReadOnlyDictionary<(Guid PlanId, Guid WorkOrderId), int>> CountsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var rows = await con.QueryAsync<CountRow>(new CommandDefinition("""
            SELECT PlanId, WorkOrderId, COUNT(*) AS N FROM quality.Inspections WHERE TenantId = @tenantId AND WorkOrderId IS NOT NULL GROUP BY PlanId, WorkOrderId
            """, new { tenantId }, cancellationToken: ct));
        return rows.ToDictionary(r => (r.PlanId, r.WorkOrderId), r => r.N);
    }

    public async Task<Guid> SaveInspectionAsync(Guid tenantId, PerformInspectionRequest r, string result, IReadOnlyList<(int Seq, double? Value, bool Passed)> rows,
        Guid userId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);
        var id = await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT quality.Inspections (TenantId, PlanId, WorkOrderId, LotNo, Result, Note, InspectedBy) OUTPUT inserted.InspectionId
            VALUES (@tenantId, @PlanId, @WorkOrderId, @LotNo, @result, @Note, @userId)
            """, new { tenantId, r.PlanId, r.WorkOrderId, r.LotNo, result, r.Note, userId }, tx, cancellationToken: ct));
        await con.ExecuteAsync(new CommandDefinition("INSERT quality.InspectionResults (TenantId, InspectionId, Seq, Value, Passed) VALUES (@tenantId, @id, @Seq, @Value, @Passed)",
            rows.Select(x => new { tenantId, id, x.Seq, x.Value, x.Passed }), tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task LinkNcrAsync(Guid tenantId, Guid inspectionId, Guid ncId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("UPDATE quality.Inspections SET NcId = @ncId WHERE TenantId = @tenantId AND InspectionId = @inspectionId",
            new { tenantId, inspectionId, ncId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<InspectionRecord>> HistoryAsync(Guid tenantId, int take, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<InspectionRecord>(new CommandDefinition($"""
            SELECT TOP ({take}) i.InspectionId, i.PlanId, p.Name AS PlanName, w.Code AS WorkOrderCode, i.LotNo, i.Result, i.NcId, u.DisplayName AS InspectorName, i.At, i.Note
            FROM quality.Inspections i
            JOIN quality.InspectionPlans p ON p.PlanId = i.PlanId
            LEFT JOIN production.WorkOrders w ON w.WorkOrderId = i.WorkOrderId
            LEFT JOIN core.Users u ON u.UserId = i.InspectedBy
            WHERE i.TenantId = @tenantId ORDER BY i.At DESC
            """, new { tenantId }, cancellationToken: ct))];
    }
}
