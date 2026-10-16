using Dapper;
using Divakar.FactoryOS.Application.Alarms;
using Divakar.FactoryOS.Infrastructure.Database;
using Microsoft.Data.SqlClient;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class AlarmRepository(Db db) : IAlarmRepository
{
    /// <summary>Tags a rule applies to: its sensor type on one asset, every asset of a type, or every asset.</summary>
    internal const string RuleTagJoin = """
        JOIN plant.Tags t ON t.TenantId = r.TenantId AND t.SensorTypeCode = r.SensorTypeCode AND t.IsActive = 1
        JOIN plant.Assets a ON a.AssetId = t.AssetId AND a.IsActive = 1
                           AND (r.AssetId IS NULL OR a.AssetId = r.AssetId) AND (r.AssetTypeCode IS NULL OR a.AssetTypeCode = r.AssetTypeCode)
        """;

    public async Task<IReadOnlyList<Rule>> RulesAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Rule>(new CommandDefinition($"""
            SELECT r.RuleId, r.Name, r.SensorTypeCode, r.AssetId, ra.Name AS AssetName, r.AssetTypeCode, r.Operator, r.Threshold, r.DurationSec,
                   r.Severity, r.Notify, r.CreateWorkOrder, r.IsActive,
                   (SELECT COUNT(*) FROM alarms.Rules r2 {RuleTagJoin.Replace("r.", "r2.")} WHERE r2.RuleId = r.RuleId) AS Matches,
                   (SELECT COUNT(*) FROM alarms.Alarms x WHERE x.RuleId = r.RuleId AND x.ClearedAt IS NULL) AS OpenAlarms
            FROM alarms.Rules r LEFT JOIN plant.Assets ra ON ra.AssetId = r.AssetId
            WHERE r.TenantId = @tenantId
            ORDER BY r.Severity DESC, r.Name
            """, new { tenantId }, cancellationToken: ct))];
    }

    public async Task<Guid> SaveRuleAsync(Guid tenantId, Guid? id, SaveRuleRequest r, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var args = new
        {
            tenantId, id, r.Name, r.SensorTypeCode, r.AssetId, r.AssetTypeCode, r.Operator, r.Threshold, r.DurationSec, r.Severity, r.Notify,
            r.CreateWorkOrder, r.IsActive,
        };
        if (id is not null)
        {
            await con.ExecuteAsync(new CommandDefinition("""
                UPDATE alarms.Rules SET Name = @Name, SensorTypeCode = @SensorTypeCode, AssetId = @AssetId, AssetTypeCode = @AssetTypeCode,
                       Operator = @Operator, Threshold = @Threshold, DurationSec = @DurationSec, Severity = @Severity, Notify = @Notify,
                       CreateWorkOrder = @CreateWorkOrder, IsActive = @IsActive
                WHERE TenantId = @tenantId AND RuleId = @id;
                DELETE alarms.Pending WHERE RuleId = @id;
                """, args, cancellationToken: ct));
            return id.Value;
        }
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT alarms.Rules (TenantId, Name, SensorTypeCode, AssetId, AssetTypeCode, Operator, Threshold, DurationSec, Severity, Notify, CreateWorkOrder, IsActive)
            OUTPUT inserted.RuleId
            VALUES (@tenantId, @Name, @SensorTypeCode, @AssetId, @AssetTypeCode, @Operator, @Threshold, @DurationSec, @Severity, @Notify, @CreateWorkOrder, @IsActive)
            """, args, cancellationToken: ct));
    }

    public async Task DeleteRuleAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            UPDATE alarms.Alarms SET State = 'Cleared', ClearedAt = SYSUTCDATETIME() WHERE TenantId = @tenantId AND RuleId = @id AND ClearedAt IS NULL;
            UPDATE alarms.Alarms SET RuleId = NULL WHERE TenantId = @tenantId AND RuleId = @id;   -- history stays, without the rule
            DELETE alarms.Rules WHERE TenantId = @tenantId AND RuleId = @id;
            """, new { tenantId, id }, cancellationToken: ct));
    }

    private const string AlarmSelect = """
        SELECT al.AlarmId, al.Kind, al.RuleId, r.Name AS RuleName, al.AssetId, a.Name AS AssetName, al.TagId, al.Severity, al.Message, al.Value,
               al.State, al.RaisedAt, al.AckAt, u.DisplayName AS AckByName, al.AckComment, al.ClearedAt
        FROM alarms.Alarms al
        JOIN plant.Assets a ON a.AssetId = al.AssetId
        LEFT JOIN alarms.Rules r ON r.RuleId = al.RuleId
        LEFT JOIN core.Users u ON u.UserId = al.AckBy
        """;

    public async Task<IReadOnlyList<Alarm>> AlarmsAsync(Guid tenantId, string? state, DateTime? from, DateTime? to, Guid? assetId, int take, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Alarm>(new CommandDefinition($"""
            {AlarmSelect}
            WHERE al.TenantId = @tenantId
              AND (@state IS NULL OR (@state = 'open' AND al.ClearedAt IS NULL) OR al.State = @state)
              AND (@from IS NULL OR al.RaisedAt >= @from) AND (@to IS NULL OR al.RaisedAt < @to)
              AND (@assetId IS NULL OR al.AssetId = @assetId)
            ORDER BY CASE WHEN al.ClearedAt IS NULL THEN 0 ELSE 1 END,
                     CASE al.Severity WHEN 'Critical' THEN 0 WHEN 'Warning' THEN 1 ELSE 2 END, al.RaisedAt DESC
            OFFSET 0 ROWS FETCH NEXT @take ROWS ONLY
            """, new { tenantId, state, from, to, assetId, take }, cancellationToken: ct))];
    }

    public async Task<Alarm?> AlarmAsync(Guid tenantId, long id, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.QuerySingleOrDefaultAsync<Alarm>(new CommandDefinition(
            $"{AlarmSelect} WHERE al.TenantId = @tenantId AND al.AlarmId = @id", new { tenantId, id }, cancellationToken: ct));
    }

    public async Task AcknowledgeAsync(Guid tenantId, long id, Guid userId, string? comment, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            UPDATE alarms.Alarms SET State = 'Acknowledged', AckAt = SYSUTCDATETIME(), AckBy = @userId, AckComment = @comment
            WHERE TenantId = @tenantId AND AlarmId = @id AND State = 'Active'
            """, new { tenantId, id, userId, comment }, cancellationToken: ct));
    }

    public async Task<AlarmSummary> SummaryAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.QuerySingleAsync<AlarmSummary>(new CommandDefinition("""
            SELECT COUNT(*) AS Active,
                   ISNULL(SUM(CASE WHEN Severity = 'Critical' THEN 1 ELSE 0 END), 0) AS Critical,
                   ISNULL(SUM(CASE WHEN Severity = 'Warning' THEN 1 ELSE 0 END), 0) AS Warning,
                   ISNULL(SUM(CASE WHEN Severity = 'Info' THEN 1 ELSE 0 END), 0) AS Info,
                   ISNULL(SUM(CASE WHEN State = 'Active' THEN 1 ELSE 0 END), 0) AS Unacknowledged,
                   (SELECT MAX(AlarmId) FROM alarms.Alarms WHERE TenantId = @tenantId) AS LatestId,
                   (SELECT TOP 1 Message FROM alarms.Alarms WHERE TenantId = @tenantId ORDER BY AlarmId DESC) AS LatestMessage
            FROM alarms.Alarms WHERE TenantId = @tenantId AND ClearedAt IS NULL
            """, new { tenantId }, cancellationToken: ct));
    }

    public async Task<bool> SensorTypeExistsAsync(string code, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM plant.SensorTypes WHERE SensorTypeCode = @code",
            new { code }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> AssetExistsAsync(Guid tenantId, Guid assetId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM plant.Assets WHERE TenantId = @tenantId AND AssetId = @assetId",
            new { tenantId, assetId }, cancellationToken: ct)) > 0;
    }
}

public sealed class AlarmStore(Db db) : IAlarmStore
{
    public async Task<IReadOnlyList<RuleTag>> RuleTagsAsync(CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<RuleTag>(new CommandDefinition($"""
            SELECT r.TenantId, r.RuleId, r.Name AS RuleName, r.Operator, r.Threshold, r.DurationSec, r.Severity, r.Notify, r.CreateWorkOrder,
                   a.AssetId, a.Name AS AssetName, t.TagId, t.Name AS TagName, t.Unit, l.Value, l.Ts, p.Since AS PendingSince,
                   (SELECT TOP 1 x.AlarmId FROM alarms.Alarms x WHERE x.RuleId = r.RuleId AND x.TagId = t.TagId AND x.ClearedAt IS NULL) AS OpenAlarmId
            FROM alarms.Rules r
            {AlarmRepository.RuleTagJoin}
            LEFT JOIN telemetry.Latest l ON l.TagId = t.TagId AND l.Quality <= 1
            LEFT JOIN alarms.Pending p ON p.RuleId = r.RuleId AND p.TagId = t.TagId
            WHERE r.IsActive = 1
            """, commandTimeout: 60, cancellationToken: ct))];
    }

    public async Task SetPendingAsync(Guid ruleId, Guid tagId, DateTime since, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            IF NOT EXISTS (SELECT 1 FROM alarms.Pending WHERE RuleId = @ruleId AND TagId = @tagId)
                INSERT alarms.Pending (RuleId, TagId, Since) VALUES (@ruleId, @tagId, @since)
            """, new { ruleId, tagId, since }, cancellationToken: ct));
    }

    public async Task ClearPendingAsync(Guid ruleId, Guid tagId, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("DELETE alarms.Pending WHERE RuleId = @ruleId AND TagId = @tagId", new { ruleId, tagId }, cancellationToken: ct));
    }

    public async Task<long?> RaiseAsync(Guid tenantId, string kind, Guid? ruleId, Guid assetId, Guid? tagId, string severity, string message, double? value,
        CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        try
        {
            return await con.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT alarms.Alarms (TenantId, Kind, RuleId, AssetId, TagId, Severity, Message, Value) OUTPUT inserted.AlarmId
                VALUES (@tenantId, @kind, @ruleId, @assetId, @tagId, @severity, @message, @value)
                """, new { tenantId, kind, ruleId, assetId, tagId, severity, message = message.Length > 300 ? message[..300] : message, value }, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            return null;   // already open (two passes raced): the unique index keeps one open alarm
        }
    }

    public async Task ClearAsync(long alarmId, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition(
            "UPDATE alarms.Alarms SET State = 'Cleared', ClearedAt = SYSUTCDATETIME() WHERE AlarmId = @alarmId AND ClearedAt IS NULL",
            new { alarmId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AnomalyCandidate>> AnomalyCandidatesAsync(IReadOnlyCollection<string> sensorTypes, DateTime now, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<AnomalyCandidate>(new CommandDefinition("""
            SELECT t.TenantId, a.AssetId, a.Name AS AssetName, t.TagId, t.Name AS TagName, t.SensorTypeCode, t.Unit,
                   b.M AS BaselineMean, ISNULL(b.S, 0) AS BaselineStd, b.N AS BaselineCount, rc.M AS RecentAvg, rc.N AS RecentCount,
                   (SELECT TOP 1 x.AlarmId FROM alarms.Alarms x WHERE x.TagId = t.TagId AND x.Kind = 'Anomaly' AND x.ClearedAt IS NULL) AS OpenAlarmId
            FROM plant.Tags t
            JOIN plant.Assets a ON a.AssetId = t.AssetId AND a.IsActive = 1
            CROSS APPLY (SELECT AVG(r.Value) AS M, STDEV(r.Value) AS S, COUNT(*) AS N FROM telemetry.Readings r
                         WHERE r.TagId = t.TagId AND r.Ts >= DATEADD(HOUR, -24, @now) AND r.Ts < DATEADD(MINUTE, -15, @now)
                           AND r.Quality <= 1 AND r.Value IS NOT NULL) b
            CROSS APPLY (SELECT AVG(r.Value) AS M, COUNT(*) AS N FROM telemetry.Readings r
                         WHERE r.TagId = t.TagId AND r.Ts >= DATEADD(MINUTE, -5, @now) AND r.Ts <= @now
                           AND r.Quality <= 1 AND r.Value IS NOT NULL) rc
            WHERE t.IsActive = 1 AND t.SensorTypeCode IN @sensorTypes AND b.N > 0 AND rc.N > 0
            """, new { sensorTypes, now }, commandTimeout: 120, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<Escalation>> DueEscalationsAsync(DateTime olderThan, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<Escalation>(new CommandDefinition("""
            SELECT TenantId, AlarmId, Message, RaisedAt FROM alarms.Alarms
            WHERE State = 'Active' AND Severity = 'Critical' AND EscalatedAt IS NULL AND RaisedAt < @olderThan
            """, new { olderThan }, cancellationToken: ct))];
    }

    public async Task MarkEscalatedAsync(long alarmId, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("UPDATE alarms.Alarms SET EscalatedAt = SYSUTCDATETIME() WHERE AlarmId = @alarmId",
            new { alarmId }, cancellationToken: ct));
    }
}
