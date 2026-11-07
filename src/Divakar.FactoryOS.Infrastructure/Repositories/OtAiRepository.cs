using Dapper;
using Divakar.FactoryOS.Application.Ai;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Infrastructure.Database;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

/// <summary>Worker side of the OT detectors: reads every company's data with the system connection.</summary>
public sealed class OtAiStore(Db db) : IOtAiStore
{
    public async Task<IReadOnlyList<EnergyHour>> EnergyHoursAsync(DateTime since, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<EnergyHour>(new CommandDefinition("""
            SELECT h.TenantId, a.AssetId, a.Name AS AssetName, h.TagId, h.Hour, h.Rise
            FROM telemetry.CounterHours h
            JOIN plant.Tags t ON t.TagId = h.TagId AND t.SensorTypeCode = 'energy_kwh' AND t.IsActive = 1
            JOIN plant.Assets a ON a.AssetId = t.AssetId AND a.IsActive = 1
            WHERE h.Hour >= @since
            """, new { since }, commandTimeout: 120, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<AnalogHour>> AnalogHoursAsync(IReadOnlyCollection<string> sensorTypes, DateTime since, DateTime until, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<AnalogHour>(new CommandDefinition("""
            SELECT t.TenantId, a.AssetId, a.Name AS AssetName, t.TagId, t.Name AS TagName, t.SensorTypeCode, t.Unit, t.MaxValue,
                   (SELECT MIN(r.Threshold) FROM alarms.Rules r
                    WHERE r.TenantId = t.TenantId AND r.IsActive = 1 AND r.SensorTypeCode = t.SensorTypeCode AND r.Operator IN ('>', '>=')
                      AND (r.AssetId = a.AssetId OR (r.AssetId IS NULL AND (r.AssetTypeCode IS NULL OR r.AssetTypeCode = a.AssetTypeCode)))) AS RuleLimit,
                   x.Hour, x.Avg
            FROM plant.Tags t
            JOIN plant.Assets a ON a.AssetId = t.AssetId AND a.IsActive = 1
            CROSS APPLY (SELECT CAST(DATEADD(HOUR, DATEDIFF(HOUR, 0, r.Ts), 0) AS datetime2(0)) AS Hour, AVG(r.Value) AS Avg
                         FROM telemetry.Readings r
                         WHERE r.TagId = t.TagId AND r.Ts >= @since AND r.Ts < @until AND r.Quality <= 1 AND r.Value IS NOT NULL
                         GROUP BY DATEADD(HOUR, DATEDIFF(HOUR, 0, r.Ts), 0)) x
            WHERE t.IsActive = 1 AND t.SensorTypeCode IN @sensorTypes
            """, new { sensorTypes, since, until }, commandTimeout: 180, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<RecentValue>> RecentValuesAsync(IReadOnlyCollection<string> sensorTypes, DateTime since, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<RecentValue>(new CommandDefinition("""
            SELECT r.TagId, r.Ts, r.Value
            FROM telemetry.Readings r
            JOIN plant.Tags t ON t.TagId = r.TagId AND t.IsActive = 1 AND t.SensorTypeCode IN @sensorTypes
            WHERE r.Ts >= @since AND r.Quality <= 1 AND r.Value IS NOT NULL
            """, new { sensorTypes, since }, commandTimeout: 120, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<IdleCandidate>> IdleCandidatesAsync(DateTime now, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<IdleCandidate>(new CommandDefinition("""
            SELECT a.TenantId, a.AssetId, a.Name AS AssetName, CAST(ls.Value AS int) AS State,
                   ISNULL((SELECT MAX(r.Ts) FROM telemetry.Readings r
                           WHERE r.TagId = st.TagId AND r.Ts >= DATEADD(HOUR, -24, @now) AND (r.Value IS NULL OR r.Value NOT IN (1, 3))),
                          DATEADD(HOUR, -24, @now)) AS IdleSince,
                   lp.Value AS PowerKw, a.RatedPowerKw
            FROM plant.Assets a
            JOIN plant.Tags st ON st.AssetId = a.AssetId AND st.SensorTypeCode = 'machine_state' AND st.IsActive = 1
            JOIN telemetry.Latest ls ON ls.TagId = st.TagId AND ls.Value IN (1, 3) AND ls.Ts >= DATEADD(MINUTE, -10, @now)
            CROSS APPLY (SELECT TOP 1 l.Value FROM plant.Tags pt JOIN telemetry.Latest l ON l.TagId = pt.TagId
                         WHERE pt.AssetId = a.AssetId AND pt.SensorTypeCode = 'active_power' AND pt.IsActive = 1
                           AND l.Ts >= DATEADD(MINUTE, -10, @now) AND l.Value IS NOT NULL
                         ORDER BY l.Ts DESC) lp
            WHERE a.IsActive = 1
            """, new { now }, commandTimeout: 60, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<Tuning>> AllTuningAsync(CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<Tuning>(new CommandDefinition(
            "SELECT TenantId, TagKey, Kind, Sensitivity, RealCount, FalseCount, CAST(NULL AS nvarchar(200)) AS Name FROM ai.TagTuning",
            cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<OpenInsight>> OpenInsightsAsync(CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<OpenInsight>(new CommandDefinition(
            "SELECT InsightId, TenantId, AssetId, TagId, Kind, Source, LastSeenAt FROM ai.Insights WHERE ClosedAt IS NULL", cancellationToken: ct))];
    }

    public async Task<bool> UpsertAsync(NewInsight i, DateTime now, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return await con.ExecuteScalarAsync<bool>(new CommandDefinition("""
            UPDATE ai.Insights SET LastSeenAt = @now, Score = @Score, Severity = @Severity, Title = @Title, Detail = @Detail, Evidence = @Evidence
            WHERE AssetId = @AssetId AND Kind = @Kind AND ClosedAt IS NULL AND ((TagId IS NULL AND @TagId IS NULL) OR TagId = @TagId);
            IF @@ROWCOUNT > 0
                SELECT CAST(0 AS bit);
            -- an operator called this a false alarm today: do not raise it again straight away (the tuning already moved)
            ELSE IF EXISTS (SELECT 1 FROM ai.Insights WHERE AssetId = @AssetId AND Kind = @Kind AND Feedback = 'False'
                                AND ((TagId IS NULL AND @TagId IS NULL) OR TagId = @TagId) AND FeedbackAt > DATEADD(HOUR, -24, @now))
                SELECT CAST(0 AS bit);
            ELSE
            BEGIN
                INSERT ai.Insights (TenantId, AssetId, TagId, Kind, Severity, Title, Detail, Score, Evidence, Source, DetectedAt, LastSeenAt)
                SELECT @TenantId, @AssetId, @TagId, @Kind, @Severity, @Title, @Detail, @Score, @Evidence, @Source, @now, @now
                WHERE EXISTS (SELECT 1 FROM plant.Assets WHERE AssetId = @AssetId AND TenantId = @TenantId);
                SELECT CAST(@@ROWCOUNT AS bit);
            END
            """, new { i.TenantId, i.AssetId, i.TagId, i.Kind, i.Severity, i.Title, i.Detail, i.Score, i.Evidence, i.Source, now }, cancellationToken: ct));
    }

    public async Task CloseAsync(long insightId, DateTime now, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("UPDATE ai.Insights SET ClosedAt = @now WHERE InsightId = @insightId AND ClosedAt IS NULL",
            new { insightId, now }, cancellationToken: ct));
    }
}

public sealed class OtAiRepository(Db db) : IOtAiRepository
{
    private sealed record KeyRow(Guid Key, string Kind);

    private const string Select = """
        SELECT i.InsightId, i.AssetId, a.Name AS AssetName, i.TagId, t.Name AS TagName, i.Kind, i.Severity, i.Title, i.Detail, i.Score, i.Evidence, i.Source,
               i.DetectedAt, i.LastSeenAt, i.ClosedAt, i.Feedback, i.FeedbackNote
        FROM ai.Insights i
        JOIN plant.Assets a ON a.AssetId = i.AssetId
        LEFT JOIN plant.Tags t ON t.TagId = i.TagId
        """;

    public async Task<IReadOnlyList<Insight>> InsightsAsync(Guid tenantId, bool openOnly, int max, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Insight>(new CommandDefinition($"""
            {Select}
            WHERE i.TenantId = @tenantId AND (@openOnly = 0 OR i.ClosedAt IS NULL)
            ORDER BY CASE WHEN i.ClosedAt IS NULL THEN 0 ELSE 1 END, CASE i.Severity WHEN 'Critical' THEN 0 WHEN 'Warning' THEN 1 ELSE 2 END, i.DetectedAt DESC
            OFFSET 0 ROWS FETCH NEXT @max ROWS ONLY
            """, new { tenantId, openOnly, max }, cancellationToken: ct))];
    }

    public async Task<Insight?> InsightAsync(Guid tenantId, long id, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.QuerySingleOrDefaultAsync<Insight>(new CommandDefinition($"{Select} WHERE i.TenantId = @tenantId AND i.InsightId = @id",
            new { tenantId, id }, cancellationToken: ct));
    }

    public async Task FeedbackAsync(Guid tenantId, long id, string verdict, Guid userId, string? note, double sensitivity, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);
        var key = await con.QuerySingleOrDefaultAsync<KeyRow>(new CommandDefinition("""
            UPDATE ai.Insights SET Feedback = @verdict, FeedbackBy = @userId, FeedbackAt = SYSUTCDATETIME(), FeedbackNote = @note,
                   ClosedAt = CASE WHEN @verdict = 'False' THEN ISNULL(ClosedAt, SYSUTCDATETIME()) ELSE ClosedAt END
            OUTPUT ISNULL(inserted.TagId, inserted.AssetId) AS [Key], inserted.Kind
            WHERE TenantId = @tenantId AND InsightId = @id AND Feedback IS NULL
            """, new { tenantId, id, verdict, userId, note }, tx, cancellationToken: ct)) ?? throw AppException.NotFound("Insight");
        await con.ExecuteAsync(new CommandDefinition("""
            MERGE ai.TagTuning AS t
            USING (SELECT @key AS TagKey, @kind AS Kind) AS s ON t.TagKey = s.TagKey AND t.Kind = s.Kind
            WHEN MATCHED THEN UPDATE SET Sensitivity = @sensitivity, RealCount = t.RealCount + @real, FalseCount = t.FalseCount + (1 - @real),
                                         UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (TenantId, TagKey, Kind, Sensitivity, RealCount, FalseCount) VALUES (@tenantId, @key, @kind, @sensitivity, @real, 1 - @real);
            """, new { tenantId, key = key.Key, kind = key.Kind, sensitivity, real = verdict == "Real" ? 1 : 0 }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task CloseAsync(Guid tenantId, long id, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var n = await con.ExecuteAsync(new CommandDefinition(
            "UPDATE ai.Insights SET ClosedAt = SYSUTCDATETIME() WHERE TenantId = @tenantId AND InsightId = @id AND ClosedAt IS NULL",
            new { tenantId, id }, cancellationToken: ct));
        if (n == 0)
        {
            throw AppException.NotFound("Open insight");
        }
    }

    public async Task<IReadOnlyList<Tuning>> TuningAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Tuning>(new CommandDefinition("""
            SELECT u.TenantId, u.TagKey, u.Kind, u.Sensitivity, u.RealCount, u.FalseCount,
                   COALESCE(a2.Name + N' · ' + t.Name, a.Name) AS Name
            FROM ai.TagTuning u
            LEFT JOIN plant.Tags t ON t.TagId = u.TagKey
            LEFT JOIN plant.Assets a2 ON a2.AssetId = t.AssetId
            LEFT JOIN plant.Assets a ON a.AssetId = u.TagKey
            WHERE u.TenantId = @tenantId
            ORDER BY u.UpdatedAt DESC
            """, new { tenantId }, cancellationToken: ct))];
    }
}
