using System.Text.Json;
using Dapper;
using Divakar.FactoryOS.Application.Energy;
using Divakar.FactoryOS.Application.Settings;
using Divakar.FactoryOS.Infrastructure.Database;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class EnergyRepository(Db db) : IEnergyRepository
{
    private sealed record TagMeta(Guid AssetId, string AssetCode, string AssetName, string AssetTypeCode, string Category, string NodeLevel,
        Guid TagId, string SensorTypeCode);

    private sealed record HourRise(Guid TagId, DateTime Hour, double Rise);

    private static DateTime FloorHour(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);

    private static DateTime CeilHour(DateTime t) => t.Ticks % TimeSpan.TicksPerHour == 0 ? t : FloorHour(t).AddHours(1);

    /// <summary>
    /// How much each counter rose in each UTC hour of [from, to): whole hours from the roll-ups (telemetry.CounterHours),
    /// the partial hours at either end from the raw readings. A rise is the sum of increases between readings, so a counter
    /// that drops (replaced, reset, rolled over, gateway restarted) adds nothing.
    /// </summary>
    private static async Task<List<HourRise>> HourlyRisesAsync(System.Data.Common.DbConnection con, Guid tenantId, IReadOnlyCollection<Guid> tagIds,
        DateTime from, DateTime to, CancellationToken ct)
    {
        var result = new List<HourRise>();
        if (tagIds.Count == 0 || from >= to)
        {
            return result;
        }
        var fullFrom = CeilHour(from);
        var fullTo = FloorHour(to);
        if (fullFrom < fullTo)
        {
            result.AddRange(await con.QueryAsync<HourRise>(new CommandDefinition("""
                SELECT TagId, Hour, Rise FROM telemetry.CounterHours
                WHERE TenantId = @tenantId AND TagId IN @tagIds AND Hour >= @fullFrom AND Hour < @fullTo
                """, new { tenantId, tagIds, fullFrom, fullTo }, commandTimeout: 120, cancellationToken: ct)));
        }
        // raw edges: [from, fullFrom) counts only rises inside the window; [fullTo, to) continues from the last reading before it
        var edges = fullFrom < fullTo
            ? new[] { (From: from, To: fullFrom, Lead: false), (From: fullTo, To: to, Lead: true) }
            : [(From: from, To: to, Lead: false)];
        foreach (var e in edges.Where(e => e.From < e.To))
        {
            result.AddRange(await con.QueryAsync<HourRise>(new CommandDefinition("""
                WITH steps AS (
                    SELECT r.TagId, r.Ts, r.Value - LAG(r.Value) OVER (PARTITION BY r.TagId ORDER BY r.Ts) AS Step
                    FROM telemetry.Readings r
                    WHERE r.TenantId = @tenantId AND r.TagId IN @tagIds AND r.Ts >= @start AND r.Ts < @to AND r.Value IS NOT NULL AND r.Quality <= 1)
                SELECT TagId, CAST(DATEADD(HOUR, DATEDIFF(HOUR, CAST('2000-01-01' AS datetime2(0)), Ts), CAST('2000-01-01' AS datetime2(0))) AS datetime2(0)) AS Hour,
                       SUM(CASE WHEN Step > 0 THEN Step ELSE 0 END) AS Rise
                FROM steps
                WHERE Ts >= @from
                GROUP BY TagId, CAST(DATEADD(HOUR, DATEDIFF(HOUR, CAST('2000-01-01' AS datetime2(0)), Ts), CAST('2000-01-01' AS datetime2(0))) AS datetime2(0))
                """, new { tenantId, tagIds, start = e.Lead ? e.From.AddHours(-1) : e.From, from = e.From, to = e.To }, commandTimeout: 120, cancellationToken: ct)));
        }
        return result;
    }

    public async Task<IReadOnlyList<CounterDelta>> CounterDeltasAsync(Guid tenantId, IReadOnlyCollection<string> sensorTypes, DateTime from, DateTime to,
        CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var tags = (await con.QueryAsync<TagMeta>(new CommandDefinition("""
            SELECT a.AssetId, a.Code AS AssetCode, a.Name AS AssetName, a.AssetTypeCode, at.Category, n.Level AS NodeLevel, t.TagId, t.SensorTypeCode
            FROM plant.Tags t
            JOIN plant.Assets a ON a.AssetId = t.AssetId
            JOIN plant.AssetTypes at ON at.AssetTypeCode = a.AssetTypeCode
            JOIN plant.Nodes n ON n.NodeId = a.NodeId
            WHERE t.TenantId = @tenantId AND t.SensorTypeCode IN @sensorTypes AND t.IsActive = 1 AND a.IsActive = 1
            """, new { tenantId, sensorTypes }, cancellationToken: ct))).ToList();
        var rises = (await HourlyRisesAsync(con, tenantId, [.. tags.Select(t => t.TagId)], from, to, ct))
            .GroupBy(r => r.TagId).ToDictionary(g => g.Key, g => g.Sum(r => r.Rise));
        // only counters that reported in the period, as before
        return [.. tags.Where(t => rises.ContainsKey(t.TagId)).Select(t => new CounterDelta(t.AssetId, t.AssetCode, t.AssetName, t.AssetTypeCode,
            t.Category, t.NodeLevel, t.TagId, t.SensorTypeCode, rises[t.TagId]))];
    }

    public async Task<IReadOnlyList<CounterBucket>> CounterBucketsAsync(Guid tenantId, IReadOnlyCollection<Guid> tagIds, DateTime from, DateTime to,
        int bucketMinutes, int utcOffsetMinutes, CancellationToken ct)
    {
        if (tagIds.Count == 0)
        {
            return [];
        }
        await using var con = await db.OpenAsync(ct);
        if (bucketMinutes % 60 == 0 && utcOffsetMinutes % 60 == 0)
        {
            // hourly rises grouped into local buckets; each bucket reports the running total before (min) and after (max) it,
            // which is what Consumption() turns back into use per bucket
            var epoch = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var buckets = new List<CounterBucket>();
            foreach (var tag in (await HourlyRisesAsync(con, tenantId, tagIds, from, to, ct)).GroupBy(r => r.TagId))
            {
                double total = 0;
                foreach (var b in tag.GroupBy(r => epoch.AddMinutes(Math.Floor((r.Hour.AddMinutes(utcOffsetMinutes) - epoch).TotalMinutes / bucketMinutes) * bucketMinutes))
                             .OrderBy(g => g.Key))
                {
                    var before = total;
                    total += b.Sum(r => r.Rise);
                    buckets.Add(new CounterBucket(tag.Key, DateTime.SpecifyKind(b.Key, DateTimeKind.Unspecified), total, before));
                }
            }
            return buckets;
        }
        // time zones off the whole hour: buckets of the running total of rises, straight from the readings
        return [.. await con.QueryAsync<CounterBucket>(new CommandDefinition("""
            WITH steps AS (
                SELECT r.TagId, r.Ts, r.Value - LAG(r.Value) OVER (PARTITION BY r.TagId ORDER BY r.Ts) AS Step
                FROM telemetry.Readings r
                WHERE r.TenantId = @tenantId AND r.TagId IN @tagIds AND r.Ts >= @from AND r.Ts < @to AND r.Value IS NOT NULL AND r.Quality <= 1
            ), total AS (
                SELECT TagId, Ts, SUM(CASE WHEN Step > 0 THEN Step ELSE 0 END) OVER (PARTITION BY TagId ORDER BY Ts ROWS UNBOUNDED PRECEDING) AS Total
                FROM steps)
            SELECT x.TagId, b.Bucket, MAX(x.Total) AS MaxValue, MIN(x.Total) AS MinValue
            FROM total x
            CROSS APPLY (SELECT DATEADD(MINUTE, (DATEDIFF(MINUTE, '2000-01-01', DATEADD(MINUTE, @offset, x.Ts)) / @bucket) * @bucket, '2000-01-01') AS Bucket) b
            GROUP BY x.TagId, b.Bucket
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
