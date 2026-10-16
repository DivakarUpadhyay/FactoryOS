using Dapper;
using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Application.Energy;
using Divakar.FactoryOS.Infrastructure.Database;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class LoadShedRepository(Db db) : ILoadShedRepository
{
    public async Task<IReadOnlyList<LoadShedRule>> RulesAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<LoadShedRule>(new CommandDefinition("""
            SELECT r.RuleId, r.Name, r.AssetId, a.Name AS AssetName, r.Priority, r.ShedAboveKw, r.RestoreBelowKw, r.Action, r.WriteTagId, t.Name AS WriteTagName,
                   r.ShedValue, r.RestoreValue, r.IsActive,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM energy.ShedEvents e WHERE e.RuleId = r.RuleId AND e.RestoredAt IS NULL) THEN 1 ELSE 0 END AS bit) AS ShedNow
            FROM energy.LoadShedRules r JOIN plant.Assets a ON a.AssetId = r.AssetId LEFT JOIN plant.Tags t ON t.TagId = r.WriteTagId
            WHERE r.TenantId = @tenantId ORDER BY r.Priority, r.Name
            """, new { tenantId }, cancellationToken: ct))];
    }

    public async Task<Guid> SaveRuleAsync(Guid tenantId, Guid? id, SaveLoadShedRuleRequest r, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var args = new { tenantId, id, r.Name, r.AssetId, r.Priority, r.ShedAboveKw, r.RestoreBelowKw, r.Action, r.WriteTagId, r.ShedValue, r.RestoreValue, r.IsActive };
        if (id is not null)
        {
            await con.ExecuteAsync(new CommandDefinition("""
                UPDATE energy.LoadShedRules SET Name = @Name, AssetId = @AssetId, Priority = @Priority, ShedAboveKw = @ShedAboveKw, RestoreBelowKw = @RestoreBelowKw,
                       Action = @Action, WriteTagId = @WriteTagId, ShedValue = @ShedValue, RestoreValue = @RestoreValue, IsActive = @IsActive
                WHERE TenantId = @tenantId AND RuleId = @id
                """, args, cancellationToken: ct));
            return id.Value;
        }
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT energy.LoadShedRules (TenantId, Name, AssetId, Priority, ShedAboveKw, RestoreBelowKw, Action, WriteTagId, ShedValue, RestoreValue, IsActive)
            OUTPUT inserted.RuleId
            VALUES (@tenantId, @Name, @AssetId, @Priority, @ShedAboveKw, @RestoreBelowKw, @Action, @WriteTagId, @ShedValue, @RestoreValue, @IsActive)
            """, args, cancellationToken: ct));
    }

    public async Task DeleteRuleAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("DELETE energy.LoadShedRules WHERE TenantId = @tenantId AND RuleId = @id", new { tenantId, id }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ShedEvent>> EventsAsync(Guid tenantId, int take, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<ShedEvent>(new CommandDefinition($"""
            SELECT TOP ({take}) e.EventId, e.RuleId, r.Name AS RuleName, a.Name AS AssetName, e.DemandKw, e.ShedAt, e.RestoredAt, e.Result
            FROM energy.ShedEvents e JOIN energy.LoadShedRules r ON r.RuleId = e.RuleId JOIN plant.Assets a ON a.AssetId = r.AssetId
            WHERE e.TenantId = @tenantId ORDER BY e.EventId DESC
            """, new { tenantId }, cancellationToken: ct))];
    }

    public async Task<bool> TagOnAssetAsync(Guid tenantId, Guid tagId, Guid assetId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM plant.Tags WHERE TenantId = @tenantId AND TagId = @tagId AND AssetId = @assetId", new { tenantId, tagId, assetId }, cancellationToken: ct)) > 0;
    }

    /// <summary>Each company's demand now: the main meters (configured, else plant-level meters), demand tag preferred over power.</summary>
    public async Task<IReadOnlyList<TenantDemand>> DemandsAsync(CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<TenantDemand>(new CommandDefinition("""
            WITH meters AS (
                SELECT a.TenantId, a.AssetId
                FROM plant.Assets a
                JOIN plant.Nodes n ON n.NodeId = a.NodeId
                LEFT JOIN core.TenantSettings s ON s.TenantId = a.TenantId
                WHERE a.IsActive = 1 AND a.AssetTypeCode = 'energy-meter'
                  AND EXISTS (SELECT 1 FROM energy.LoadShedRules r WHERE r.TenantId = a.TenantId AND r.IsActive = 1)
                  AND ((s.MainMeterAssetId IS NOT NULL AND a.AssetId = s.MainMeterAssetId) OR (s.MainMeterAssetId IS NULL AND n.Level IN ('Site', 'Plant')))
            ), v AS (
                SELECT m.TenantId, x.Value, x.Ts FROM meters m
                CROSS APPLY (SELECT TOP 1 l.Value, l.Ts FROM plant.Tags t JOIN telemetry.Latest l ON l.TagId = t.TagId
                             WHERE t.AssetId = m.AssetId AND t.SensorTypeCode IN ('demand_kw', 'active_power') AND l.Quality <= 1 AND l.Value IS NOT NULL
                             ORDER BY CASE t.SensorTypeCode WHEN 'demand_kw' THEN 0 ELSE 1 END) x
            )
            SELECT TenantId, CAST(SUM(Value) AS float) AS DemandKw, MIN(Ts) AS Ts FROM v GROUP BY TenantId
            """, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<ShedRuleState>> StatesAsync(CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<ShedRuleState>(new CommandDefinition("""
            SELECT r.TenantId, r.RuleId, r.Name, r.AssetId, a.Name AS AssetName, r.Priority, CAST(r.ShedAboveKw AS float) AS ShedAboveKw,
                   CAST(r.RestoreBelowKw AS float) AS RestoreBelowKw, r.Action, r.WriteTagId, r.ShedValue, r.RestoreValue, e.EventId AS OpenEventId, e.ShedAt
            FROM energy.LoadShedRules r
            JOIN plant.Assets a ON a.AssetId = r.AssetId
            LEFT JOIN energy.ShedEvents e ON e.RuleId = r.RuleId AND e.RestoredAt IS NULL
            WHERE r.IsActive = 1
            """, cancellationToken: ct))];
    }

    public async Task<long> OpenEventAsync(Guid tenantId, Guid ruleId, double demandKw, string result, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return await con.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT energy.ShedEvents (TenantId, RuleId, DemandKw, Result) OUTPUT inserted.EventId VALUES (@tenantId, @ruleId, @demandKw, @result)
            """, new { tenantId, ruleId, demandKw, result = result.Length > 300 ? result[..300] : result }, cancellationToken: ct));
    }

    public async Task CloseEventAsync(long eventId, string result, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            UPDATE energy.ShedEvents SET RestoredAt = SYSUTCDATETIME(), Result = LEFT(CONCAT(Result, ' / restore: ', @result), 300) WHERE EventId = @eventId
            """, new { eventId, result }, cancellationToken: ct));
    }
}

/// <summary>Finds one asset's device binding through the ingestion store (system connection).</summary>
public sealed class AssetBindingSource(IIngestionStore store) : IAssetBindingSource
{
    private static readonly string[] Writable = ["modbus-tcp", "mqtt"];

    public async Task<AssetBinding?> BindingAsync(Guid assetId, CancellationToken ct) =>
        (await store.ActiveBindingsAsync(Writable, ct)).FirstOrDefault(b => b.AssetId == assetId);
}
