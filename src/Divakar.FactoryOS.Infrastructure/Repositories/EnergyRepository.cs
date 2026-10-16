using System.Text.Json;
using Dapper;
using Divakar.FactoryOS.Application.Energy;
using Divakar.FactoryOS.Application.Settings;
using Divakar.FactoryOS.Infrastructure.Database;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class EnergyRepository(Db db) : IEnergyRepository
{
    public async Task<IReadOnlyList<CounterDelta>> CounterDeltasAsync(Guid tenantId, IReadOnlyCollection<string> sensorTypes, DateTime from, DateTime to,
        CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<CounterDelta>(new CommandDefinition("""
            SELECT a.AssetId, a.Code AS AssetCode, a.Name AS AssetName, a.AssetTypeCode, at.Category, n.Level AS NodeLevel,
                   t.TagId, t.SensorTypeCode, CAST(ISNULL(MAX(r.Value) - MIN(r.Value), 0) AS float) AS Delta
            FROM plant.Tags t
            JOIN plant.Assets a ON a.AssetId = t.AssetId
            JOIN plant.AssetTypes at ON at.AssetTypeCode = a.AssetTypeCode
            JOIN plant.Nodes n ON n.NodeId = a.NodeId
            JOIN telemetry.Readings r ON r.TagId = t.TagId AND r.Ts >= @from AND r.Ts < @to AND r.Quality <= 1 AND r.Value IS NOT NULL
            WHERE t.TenantId = @tenantId AND t.SensorTypeCode IN @sensorTypes AND t.IsActive = 1 AND a.IsActive = 1
            GROUP BY a.AssetId, a.Code, a.Name, a.AssetTypeCode, at.Category, n.Level, t.TagId, t.SensorTypeCode
            """, new { tenantId, sensorTypes, from, to }, commandTimeout: 120, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<CounterBucket>> CounterBucketsAsync(Guid tenantId, IReadOnlyCollection<Guid> tagIds, DateTime from, DateTime to,
        int bucketMinutes, int utcOffsetMinutes, CancellationToken ct)
    {
        if (tagIds.Count == 0)
        {
            return [];
        }
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<CounterBucket>(new CommandDefinition("""
            SELECT r.TagId, b.Bucket, MAX(r.Value) AS MaxValue, MIN(r.Value) AS MinValue
            FROM telemetry.Readings r
            CROSS APPLY (SELECT DATEADD(MINUTE, (DATEDIFF(MINUTE, '2000-01-01', DATEADD(MINUTE, @offset, r.Ts)) / @bucket) * @bucket, '2000-01-01') AS Bucket) b
            WHERE r.TenantId = @tenantId AND r.TagId IN @tagIds AND r.Ts >= @from AND r.Ts < @to AND r.Value IS NOT NULL AND r.Quality <= 1
            GROUP BY r.TagId, b.Bucket
            """, new { tenantId, tagIds, from, to, bucket = bucketMinutes, offset = utcOffsetMinutes }, commandTimeout: 120, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<DemandPeak>> DemandPeaksAsync(Guid tenantId, IReadOnlyCollection<Guid> assetIds, DateTime from, DateTime to,
        CancellationToken ct)
    {
        if (assetIds.Count == 0)
        {
            return [];
        }
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<DemandPeak>(new CommandDefinition("""
            SELECT AssetId, Value AS PeakKw, Ts AS At FROM (
                SELECT t.AssetId, r.Value, r.Ts,
                       ROW_NUMBER() OVER (PARTITION BY t.AssetId ORDER BY CASE t.SensorTypeCode WHEN 'demand_kw' THEN 0 ELSE 1 END, r.Value DESC) AS Rn
                FROM plant.Tags t JOIN telemetry.Readings r ON r.TagId = t.TagId
                WHERE t.TenantId = @tenantId AND t.AssetId IN @assetIds AND t.SensorTypeCode IN ('demand_kw', 'active_power')
                  AND r.Ts >= @from AND r.Ts < @to AND r.Value IS NOT NULL AND r.Quality <= 1
            ) x WHERE Rn = 1
            """, new { tenantId, assetIds, from, to }, commandTimeout: 120, cancellationToken: ct))];
    }

    /// <summary>
    /// Minute by minute: average power, and the machine state in force (the last state reading at or before that minute,
    /// carried forward in one pass with LAST_VALUE … IGNORE NULLS, which needs SQL Server 2022).
    /// </summary>
    public async Task<IReadOnlyList<IdleRow>> IdleEnergyAsync(Guid tenantId, DateTime from, DateTime to, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<IdleRow>(new CommandDefinition("""
            WITH p AS (
                SELECT t.AssetId, DATEADD(MINUTE, DATEDIFF(MINUTE, 0, r.Ts), 0) AS M, AVG(r.Value) AS Kw
                FROM plant.Tags t
                JOIN plant.Assets a ON a.AssetId = t.AssetId
                JOIN plant.AssetTypes at ON at.AssetTypeCode = a.AssetTypeCode AND at.Category <> 'Energy'
                JOIN telemetry.Readings r ON r.TagId = t.TagId
                WHERE t.TenantId = @tenantId AND t.SensorTypeCode = 'active_power' AND t.IsActive = 1
                  AND r.Ts >= @from AND r.Ts < @to AND r.Value IS NOT NULL AND r.Quality <= 1
                GROUP BY t.AssetId, DATEADD(MINUTE, DATEDIFF(MINUTE, 0, r.Ts), 0)
            ), st AS (
                SELECT t.AssetId, DATEADD(MINUTE, DATEDIFF(MINUTE, 0, r.Ts), 0) AS M, MAX(r.Value) AS St
                FROM plant.Tags t JOIN telemetry.Readings r ON r.TagId = t.TagId
                WHERE t.TenantId = @tenantId AND t.SensorTypeCode = 'machine_state' AND t.IsActive = 1
                  AND r.Ts >= DATEADD(MINUTE, -3, @from) AND r.Ts < @to AND r.Value IS NOT NULL AND r.Quality <= 1
                GROUP BY t.AssetId, DATEADD(MINUTE, DATEDIFF(MINUTE, 0, r.Ts), 0)
            ), m AS (
                SELECT COALESCE(p.AssetId, st.AssetId) AS AssetId, COALESCE(p.M, st.M) AS M, p.Kw, st.St
                FROM p FULL JOIN st ON st.AssetId = p.AssetId AND st.M = p.M
            ), f AS (
                SELECT AssetId, Kw, LAST_VALUE(St) IGNORE NULLS OVER (PARTITION BY AssetId ORDER BY M ROWS UNBOUNDED PRECEDING) AS St FROM m
            )
            SELECT a.AssetId, a.Code AS AssetCode, a.Name AS AssetName,
                   CAST(SUM(CASE WHEN f.St IN (1, 3) THEN f.Kw ELSE 0 END) / 60.0 AS float) AS IdleKwh,
                   CAST(SUM(CASE WHEN f.St IN (1, 3) THEN 1 ELSE 0 END) / 60.0 AS float) AS IdleHours,
                   CAST(SUM(CASE WHEN f.St = 2 THEN f.Kw ELSE 0 END) / 60.0 AS float) AS RunKwh
            FROM f JOIN plant.Assets a ON a.AssetId = f.AssetId
            WHERE f.Kw IS NOT NULL AND f.St IS NOT NULL
            GROUP BY a.AssetId, a.Code, a.Name
            """, new { tenantId, from, to }, commandTimeout: 180, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<SignalStats>> SignalStatsAsync(Guid tenantId, IReadOnlyCollection<string> sensorTypes, DateTime from, DateTime to,
        CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<SignalStats>(new CommandDefinition("""
            SELECT a.AssetId, a.Name AS AssetName, a.AssetTypeCode, t.SensorTypeCode, t.Unit,
                   l.Value AS Latest, x.MinV AS Min, x.MaxV AS Max, x.AvgV AS Avg, l.Ts AS LatestAt
            FROM plant.Tags t
            JOIN plant.Assets a ON a.AssetId = t.AssetId
            LEFT JOIN telemetry.Latest l ON l.TagId = t.TagId
            OUTER APPLY (SELECT MIN(r.Value) AS MinV, MAX(r.Value) AS MaxV, AVG(r.Value) AS AvgV FROM telemetry.Readings r
                         WHERE r.TagId = t.TagId AND r.Ts >= @from AND r.Ts < @to AND r.Quality <= 1) x
            WHERE t.TenantId = @tenantId AND t.SensorTypeCode IN @sensorTypes AND t.IsActive = 1 AND a.IsActive = 1
            """, new { tenantId, sensorTypes, from, to }, commandTimeout: 120, cancellationToken: ct))];
    }
}

public sealed class SettingsRepository(Db db) : ISettingsRepository
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record Row(string TimeZone, string Currency, decimal PeakRate, decimal OffPeakRate, byte PeakStartHour, byte PeakEndHour,
        decimal? ContractKw, decimal ElectricityFactor, decimal GasFactor, Guid? MainMeterAssetId, string? Shifts, string? AlertEmails, string? AlertWebhookUrl, decimal? DemandChargePerKw);

    /// <summary>Read with the system connection: background work (worker, reports) needs a tenant's settings without a signed-in user.</summary>
    public async Task<TenantSettings?> GetAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        var r = await con.QuerySingleOrDefaultAsync<Row>(new CommandDefinition("""
            SELECT TimeZone, Currency, PeakRate, OffPeakRate, PeakStartHour, PeakEndHour, ContractKw, ElectricityFactor, GasFactor,
                   MainMeterAssetId, Shifts, AlertEmails, AlertWebhookUrl, DemandChargePerKw
            FROM core.TenantSettings WHERE TenantId = @tenantId
            """, new { tenantId }, cancellationToken: ct));
        return r is null ? null : new TenantSettings(r.TimeZone, r.Currency, r.PeakRate, r.OffPeakRate, r.PeakStartHour, r.PeakEndHour, r.ContractKw,
            r.ElectricityFactor, r.GasFactor, r.MainMeterAssetId, ShiftJson.Parse(r.Shifts), r.AlertEmails, r.AlertWebhookUrl, r.DemandChargePerKw);
    }

    public async Task SaveAsync(Guid tenantId, TenantSettings s, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            MERGE core.TenantSettings AS x USING (SELECT @tenantId AS TenantId) AS src ON x.TenantId = src.TenantId
            WHEN MATCHED THEN UPDATE SET TimeZone = @TimeZone, Currency = @Currency, PeakRate = @PeakRate, OffPeakRate = @OffPeakRate,
                PeakStartHour = @PeakStartHour, PeakEndHour = @PeakEndHour, ContractKw = @ContractKw, ElectricityFactor = @ElectricityFactor,
                GasFactor = @GasFactor, MainMeterAssetId = @MainMeterAssetId, Shifts = @shifts, AlertEmails = @AlertEmails,
                AlertWebhookUrl = @AlertWebhookUrl, DemandChargePerKw = @DemandChargePerKw, UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (TenantId, TimeZone, Currency, PeakRate, OffPeakRate, PeakStartHour, PeakEndHour, ContractKw,
                ElectricityFactor, GasFactor, MainMeterAssetId, Shifts, AlertEmails, AlertWebhookUrl, DemandChargePerKw)
                VALUES (@tenantId, @TimeZone, @Currency, @PeakRate, @OffPeakRate, @PeakStartHour, @PeakEndHour, @ContractKw,
                @ElectricityFactor, @GasFactor, @MainMeterAssetId, @shifts, @AlertEmails, @AlertWebhookUrl, @DemandChargePerKw);
            """, new
        {
            tenantId, s.TimeZone, s.Currency, s.PeakRate, s.OffPeakRate, s.PeakStartHour, s.PeakEndHour, s.ContractKw, s.ElectricityFactor,
            s.GasFactor, s.MainMeterAssetId, shifts = JsonSerializer.Serialize(s.Shifts, Web), s.AlertEmails, s.AlertWebhookUrl, s.DemandChargePerKw,
        }, cancellationToken: ct));
    }
}
