using Dapper;
using Divakar.FactoryOS.Application.Maintenance;
using Divakar.FactoryOS.Infrastructure.Database;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class FixKnowledgeRepository(Db db) : IFixKnowledgeRepository
{
    public async Task<FixCase?> CaseForWorkOrderAsync(Guid tenantId, Guid maintWoId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.QuerySingleOrDefaultAsync<FixCase>(new CommandDefinition("""
            SELECT w.AssetId, a.AssetTypeCode, al.RuleId, t.SensorTypeCode,
                   CONCAT(w.Title, N' ', w.Description, N' ', al.Message) AS Text
            FROM maintenance.WorkOrders w
            JOIN plant.Assets a ON a.AssetId = w.AssetId
            LEFT JOIN alarms.Alarms al ON al.AlarmId = w.AlarmId
            LEFT JOIN plant.Tags t ON t.TagId = al.TagId
            WHERE w.TenantId = @tenantId AND w.MaintWoId = @maintWoId
            """, new { tenantId, maintWoId }, cancellationToken: ct));
    }

    public async Task<FixCase?> CaseForAlarmAsync(Guid tenantId, long alarmId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.QuerySingleOrDefaultAsync<FixCase>(new CommandDefinition("""
            SELECT al.AssetId, a.AssetTypeCode, al.RuleId, t.SensorTypeCode, al.Message AS Text
            FROM alarms.Alarms al
            JOIN plant.Assets a ON a.AssetId = al.AssetId
            LEFT JOIN plant.Tags t ON t.TagId = al.TagId
            WHERE al.TenantId = @tenantId AND al.AlarmId = @alarmId
            """, new { tenantId, alarmId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PastFix>> PastFixesAsync(Guid tenantId, Guid? excludeWoId, string? search, int max, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var like = search is null ? null : $"%{search.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]")}%";
        return [.. await con.QueryAsync<PastFix>(new CommandDefinition($"""
            SELECT TOP ({max}) w.MaintWoId, w.Code, w.Title, w.AssetId, a.Name AS AssetName, a.AssetTypeCode, al.RuleId, t.SensorTypeCode,
                   r.FailureCode, r.Cause, r.Fix, r.RecordedAt, u.DisplayName AS RecordedBy, r.Helpful
            FROM maintenance.Resolutions r
            JOIN maintenance.WorkOrders w ON w.MaintWoId = r.MaintWoId
            JOIN plant.Assets a ON a.AssetId = w.AssetId
            LEFT JOIN alarms.Alarms al ON al.AlarmId = w.AlarmId
            LEFT JOIN plant.Tags t ON t.TagId = al.TagId
            LEFT JOIN core.Users u ON u.UserId = r.RecordedBy
            WHERE r.TenantId = @tenantId AND w.Status = 'Done' AND (@excludeWoId IS NULL OR r.MaintWoId <> @excludeWoId)
              AND (@like IS NULL OR w.Title LIKE @like OR r.Cause LIKE @like OR r.Fix LIKE @like OR r.FailureCode LIKE @like OR a.Name LIKE @like)
            ORDER BY r.RecordedAt DESC
            """, new { tenantId, excludeWoId, like }, cancellationToken: ct))];
    }

    public async Task<Resolution?> ResolutionAsync(Guid tenantId, Guid maintWoId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.QuerySingleOrDefaultAsync<Resolution>(new CommandDefinition("""
            SELECT r.FailureCode, r.Cause, r.Fix, r.RecordedAt, u.DisplayName AS RecordedBy
            FROM maintenance.Resolutions r LEFT JOIN core.Users u ON u.UserId = r.RecordedBy
            WHERE r.TenantId = @tenantId AND r.MaintWoId = @maintWoId
            """, new { tenantId, maintWoId }, cancellationToken: ct));
    }

    public async Task SaveResolutionAsync(Guid tenantId, Guid maintWoId, string? failureCode, string cause, string fix, Guid? userId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            MERGE maintenance.Resolutions AS x USING (SELECT @maintWoId AS MaintWoId) AS s ON x.MaintWoId = s.MaintWoId
            WHEN MATCHED THEN UPDATE SET FailureCode = @failureCode, Cause = @cause, Fix = @fix, RecordedBy = @userId, RecordedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (MaintWoId, TenantId, FailureCode, Cause, Fix, RecordedBy) VALUES (@maintWoId, @tenantId, @failureCode, @cause, @fix, @userId);
            """, new { tenantId, maintWoId, failureCode, cause, fix, userId }, cancellationToken: ct));
    }

    public async Task MarkHelpfulAsync(Guid tenantId, Guid maintWoId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("UPDATE maintenance.Resolutions SET Helpful = Helpful + 1 WHERE TenantId = @tenantId AND MaintWoId = @maintWoId",
            new { tenantId, maintWoId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<string>> FailureCodesAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<string>(new CommandDefinition("""
            SELECT FailureCode FROM maintenance.Resolutions WHERE TenantId = @tenantId AND FailureCode IS NOT NULL
            GROUP BY FailureCode ORDER BY COUNT(*) DESC, FailureCode
            """, new { tenantId }, cancellationToken: ct))];
    }
}
