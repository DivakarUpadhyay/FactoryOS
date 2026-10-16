using Dapper;
using Divakar.FactoryOS.Application.Live;
using Divakar.FactoryOS.Infrastructure.Database;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class LiveFeedRepository(Db db) : ILiveFeed
{
    /// <summary>Values stored after <paramref name="since"/> (server clock), and the newest store time seen, for the next call.</summary>
    public async Task<(IReadOnlyList<LiveChange> Changes, DateTime Marker)> ChangesSinceAsync(DateTime since, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        var rows = (await con.QueryAsync<LiveChangeRow>(new CommandDefinition("""
            SELECT l.TenantId, t.AssetId, l.TagId, l.Ts, l.Value, l.Quality, l.UpdatedAt
            FROM telemetry.Latest l JOIN plant.Tags t ON t.TagId = l.TagId
            WHERE l.UpdatedAt > @since
            """, new { since }, cancellationToken: ct))).ToList();
        return ([.. rows.Select(r => new LiveChange(r.TenantId, r.AssetId, r.TagId, r.Ts, r.Value, r.Quality, r.UpdatedAt))],
            rows.Count > 0 ? rows.Max(r => r.UpdatedAt) : since);
    }

    /// <summary>Where the feed starts: the database clock now, so the first pass sends nothing old.</summary>
    public async Task<DateTime> NowAsync(CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return await con.ExecuteScalarAsync<DateTime>(new CommandDefinition("SELECT DATEADD(SECOND, -2, SYSUTCDATETIME())", cancellationToken: ct));
    }

    private sealed record LiveChangeRow(Guid TenantId, Guid AssetId, Guid TagId, DateTime Ts, double? Value, byte Quality, DateTime UpdatedAt);

    public async Task<IReadOnlyList<NewAlarm>> AlarmsAfterAsync(long afterId, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<NewAlarm>(new CommandDefinition(
            "SELECT TenantId, AlarmId, AssetId, Severity, Message FROM alarms.Alarms WHERE AlarmId > @afterId ORDER BY AlarmId",
            new { afterId }, cancellationToken: ct))];
    }

    public async Task<long> LastAlarmIdAsync(CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return await con.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT MAX(AlarmId) FROM alarms.Alarms", cancellationToken: ct)) ?? 0;
    }

    public async Task<IReadOnlyList<AssetStatusRow>> StatusesAsync(CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<AssetStatusRow>(new CommandDefinition(
            "SELECT TenantId, AssetId, Status, LastSeenAt, LastError FROM plant.Assets WHERE IsActive = 1", cancellationToken: ct))];
    }
}

public sealed class FloorRepository(Db db) : IFloorRepository
{
    /// <summary>Each asset with the nearest Area and Line above it, and its newest active-power value.</summary>
    public async Task<IReadOnlyList<FloorAsset>> FloorAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<FloorAsset>(new CommandDefinition("""
            WITH up AS (
                SELECT a.AssetId, n.NodeId, n.ParentId, n.Level, n.Name, 0 AS Depth
                FROM plant.Assets a JOIN plant.Nodes n ON n.NodeId = a.NodeId
                WHERE a.TenantId = @tenantId AND a.IsActive = 1
                UNION ALL
                SELECT up.AssetId, p.NodeId, p.ParentId, p.Level, p.Name, up.Depth + 1
                FROM up JOIN plant.Nodes p ON p.NodeId = up.ParentId
            )
            SELECT a.AssetId, a.NodeId,
                   (SELECT TOP 1 Name FROM up WHERE up.AssetId = a.AssetId AND up.Level = 'Area' ORDER BY Depth) AS AreaName,
                   (SELECT TOP 1 Name FROM up WHERE up.AssetId = a.AssetId AND up.Level = 'Line' ORDER BY Depth) AS LineName,
                   a.Code, a.Name, a.AssetTypeCode, t.Category, a.Status, a.LayoutX, a.LayoutY,
                   pw.TagId AS PowerTagId, pw.Value AS PowerKw, a.RatedPowerKw
            FROM plant.Assets a
            JOIN plant.AssetTypes t ON t.AssetTypeCode = a.AssetTypeCode
            OUTER APPLY (SELECT TOP 1 g.TagId, l.Value FROM plant.Tags g LEFT JOIN telemetry.Latest l ON l.TagId = g.TagId
                         WHERE g.AssetId = a.AssetId AND g.SensorTypeCode IN ('active_power', 'demand_kw', 'dc_power') AND g.IsActive = 1
                         ORDER BY CASE g.SensorTypeCode WHEN 'active_power' THEN 0 WHEN 'dc_power' THEN 1 ELSE 2 END) pw
            WHERE a.TenantId = @tenantId AND a.IsActive = 1
            ORDER BY AreaName, LineName, a.Code
            """, new { tenantId }, cancellationToken: ct))];
    }
}
