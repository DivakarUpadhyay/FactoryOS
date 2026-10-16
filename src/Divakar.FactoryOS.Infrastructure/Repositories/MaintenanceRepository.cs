using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Dapper;
using Divakar.FactoryOS.Application.Maintenance;
using Divakar.FactoryOS.Application.Notifications;
using Divakar.FactoryOS.Infrastructure.Database;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class MaintenanceRepository(Db db) : IMaintenanceRepository
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const string CurrentCount = """
        (SELECT TOP 1 l.Value FROM plant.Tags g JOIN telemetry.Latest l ON l.TagId = g.TagId
         WHERE g.AssetId = {0} AND g.SensorTypeCode = 'part_count' ORDER BY g.Code)
        """;

    public async Task<IReadOnlyList<PlanUsage>> PlanUsageAsync(Guid? tenantId, CancellationToken ct)
    {
        await using var con = tenantId is null ? await db.OpenSystemAsync(ct) : await db.OpenAsync(ct);
        return [.. await con.QueryAsync<PlanUsage>(new CommandDefinition($"""
            SELECT p.TenantId, p.PlanId, p.AssetId, a.Name AS AssetName, p.Name, p.TriggerType, p.Interval, p.Checklist, p.LastDoneAt, p.LastDoneCount,
                   {string.Format(CurrentCount, "p.AssetId")} AS CurrentCount,
                   CAST(ISNULL(rs.Sec, 0) AS float) AS RunSeconds,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM maintenance.WorkOrders w WHERE w.PlanId = p.PlanId AND w.Status IN ('Open', 'InProgress'))
                             THEN 1 ELSE 0 END AS bit) AS HasOpenOrder
            FROM maintenance.Plans p
            JOIN plant.Assets a ON a.AssetId = p.AssetId
            OUTER APPLY (
                SELECT SUM(CASE WHEN x.V = 2 THEN DATEDIFF_BIG(MILLISECOND, x.Ts,
                           CASE WHEN x.NextTs IS NOT NULL AND DATEDIFF(SECOND, x.Ts, x.NextTs) <= 180 THEN x.NextTs
                                WHEN x.NextTs IS NULL AND DATEDIFF(SECOND, x.Ts, SYSUTCDATETIME()) <= 180 THEN SYSUTCDATETIME()
                                ELSE DATEADD(SECOND, 60, x.Ts) END) ELSE 0 END) / 1000.0 AS Sec
                FROM (SELECT r.Ts, r.Value AS V, LEAD(r.Ts) OVER (ORDER BY r.Ts) AS NextTs
                      FROM plant.Tags g JOIN telemetry.Readings r ON r.TagId = g.TagId
                      WHERE p.TriggerType = 'RunHours' AND g.AssetId = p.AssetId AND g.SensorTypeCode = 'machine_state'
                        AND r.Ts >= p.LastDoneAt AND r.Quality <= 1) x
            ) rs
            WHERE (@tenantId IS NULL AND p.IsActive = 1) OR p.TenantId = @tenantId
            """, new { tenantId }, commandTimeout: 120, cancellationToken: ct))];
    }

    private sealed record PlanInfoRow(Guid PlanId, bool IsActive, int? EstimatedMinutes, string AssetName);

    public async Task<IReadOnlyList<(Guid PlanId, bool IsActive, int? EstimatedMinutes, string AssetName)>> PlanInfoAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var rows = await con.QueryAsync<PlanInfoRow>(new CommandDefinition("""
            SELECT p.PlanId, p.IsActive, p.EstimatedMinutes, a.Name AS AssetName
            FROM maintenance.Plans p JOIN plant.Assets a ON a.AssetId = p.AssetId WHERE p.TenantId = @tenantId
            """, new { tenantId }, cancellationToken: ct));
        return [.. rows.Select(r => (r.PlanId, r.IsActive, r.EstimatedMinutes, r.AssetName))];
    }

    public async Task<Guid> SavePlanAsync(Guid tenantId, Guid? id, SavePlanRequest r, string? checklistJson, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var args = new { tenantId, id, r.AssetId, r.Name, r.TriggerType, r.Interval, checklistJson, r.EstimatedMinutes, r.IsActive };
        if (id is not null)
        {
            await con.ExecuteAsync(new CommandDefinition("""
                UPDATE maintenance.Plans SET AssetId = @AssetId, Name = @Name, TriggerType = @TriggerType, Interval = @Interval, Checklist = @checklistJson,
                       EstimatedMinutes = @EstimatedMinutes, IsActive = @IsActive
                WHERE TenantId = @tenantId AND PlanId = @id
                """, args, cancellationToken: ct));
            return id.Value;
        }
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition($"""
            INSERT maintenance.Plans (TenantId, AssetId, Name, TriggerType, Interval, Checklist, EstimatedMinutes, IsActive, LastDoneCount)
            OUTPUT inserted.PlanId
            VALUES (@tenantId, @AssetId, @Name, @TriggerType, @Interval, @checklistJson, @EstimatedMinutes, @IsActive, {string.Format(CurrentCount, "@AssetId")})
            """, args, cancellationToken: ct));
    }

    private sealed record WoRow(Guid MaintWoId, string Code, Guid AssetId, string AssetName, Guid? PlanId, long? AlarmId, string Type, byte Priority,
        string Title, string? Description, string Status, Guid? AssignedTo, string? AssignedName, string? Checklist, DateTime? DueAt, DateTime? StartedAt,
        DateTime? CompletedAt, string? Notes, DateTime CreatedAt, bool Overdue);

    private const string WoSelect = """
        SELECT w.MaintWoId, w.Code, w.AssetId, a.Name AS AssetName, w.PlanId, w.AlarmId, w.Type, w.Priority, w.Title, w.Description, w.Status,
               w.AssignedTo, u.DisplayName AS AssignedName, w.Checklist, w.DueAt, w.StartedAt, w.CompletedAt, w.Notes, w.CreatedAt,
               CAST(CASE WHEN w.Status IN ('Open', 'InProgress') AND w.DueAt < SYSUTCDATETIME() THEN 1 ELSE 0 END AS bit) AS Overdue
        FROM maintenance.WorkOrders w
        JOIN plant.Assets a ON a.AssetId = w.AssetId
        LEFT JOIN core.Users u ON u.UserId = w.AssignedTo
        """;

    private static MaintWorkOrder Map(WoRow r) => new(r.MaintWoId, r.Code, r.AssetId, r.AssetName, r.PlanId, r.AlarmId, r.Type, r.Priority, r.Title,
        r.Description, r.Status, r.AssignedTo, r.AssignedName,
        string.IsNullOrWhiteSpace(r.Checklist) ? [] : JsonSerializer.Deserialize<List<ChecklistItem>>(r.Checklist, Web) ?? [],
        r.DueAt, r.StartedAt, r.CompletedAt, r.Notes, r.CreatedAt, r.Overdue);

    public async Task<IReadOnlyList<MaintWorkOrder>> WorkOrdersAsync(Guid tenantId, string? status, Guid? assetId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var rows = await con.QueryAsync<WoRow>(new CommandDefinition($"""
            {WoSelect}
            WHERE w.TenantId = @tenantId AND (@status IS NULL OR w.Status = @status OR (@status = 'open' AND w.Status IN ('Open', 'InProgress')))
              AND (@assetId IS NULL OR w.AssetId = @assetId)
              AND (w.Status IN ('Open', 'InProgress') OR w.CreatedAt > DATEADD(DAY, -90, SYSUTCDATETIME()))
            ORDER BY CASE w.Status WHEN 'InProgress' THEN 0 WHEN 'Open' THEN 1 ELSE 2 END, w.Priority, w.DueAt, w.CreatedAt DESC
            """, new { tenantId, status, assetId }, cancellationToken: ct));
        return [.. rows.Select(Map)];
    }

    public async Task<MaintWorkOrder?> WorkOrderAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var row = await con.QuerySingleOrDefaultAsync<WoRow>(new CommandDefinition($"{WoSelect} WHERE w.TenantId = @tenantId AND w.MaintWoId = @id",
            new { tenantId, id }, cancellationToken: ct));
        return row is null ? null : Map(row);
    }

    public async Task<Guid> CreateWorkOrderAsync(Guid tenantId, string code, Guid assetId, Guid? planId, long? alarmId, string type, int priority, string title,
        string? description, DateTime? dueAt, Guid? assignedTo, string? checklistJson, Guid? createdBy, CancellationToken ct)
    {
        // the system connection: the worker creates orders for every company; callers have already checked the tenant
        await using var con = await db.OpenSystemAsync(ct);
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT maintenance.WorkOrders (TenantId, Code, AssetId, PlanId, AlarmId, Type, Priority, Title, Description, DueAt, AssignedTo, Checklist, CreatedBy)
            OUTPUT inserted.MaintWoId
            VALUES (@tenantId, @code, @assetId, @planId, @alarmId, @type, @priority, @title, @description, @dueAt, @assignedTo, @checklistJson, @createdBy)
            """, new { tenantId, code, assetId, planId, alarmId, type, priority, title, description, dueAt, assignedTo, checklistJson, createdBy },
            cancellationToken: ct));
    }

    public async Task SetStatusAsync(Guid tenantId, Guid id, string status, Guid? assignee, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            UPDATE maintenance.WorkOrders
            SET Status = @status, AssignedTo = @assignee,
                StartedAt = CASE WHEN @status = 'InProgress' THEN SYSUTCDATETIME() ELSE StartedAt END,
                CompletedAt = CASE WHEN @status IN ('Done', 'Cancelled') THEN SYSUTCDATETIME() ELSE CompletedAt END
            WHERE TenantId = @tenantId AND MaintWoId = @id
            """, new { tenantId, id, status, assignee }, cancellationToken: ct));
    }

    public async Task SaveChecklistAsync(Guid tenantId, Guid id, string checklistJson, string? notes, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition(
            "UPDATE maintenance.WorkOrders SET Checklist = @checklistJson, Notes = @notes WHERE TenantId = @tenantId AND MaintWoId = @id",
            new { tenantId, id, checklistJson, notes }, cancellationToken: ct));
    }

    public async Task AssignAsync(Guid tenantId, Guid id, Guid? userId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("UPDATE maintenance.WorkOrders SET AssignedTo = @userId WHERE TenantId = @tenantId AND MaintWoId = @id",
            new { tenantId, id, userId }, cancellationToken: ct));
    }

    public async Task MarkPlanDoneAsync(Guid tenantId, Guid planId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition($"""
            UPDATE p SET LastDoneAt = SYSUTCDATETIME(), LastDoneCount = {string.Format(CurrentCount, "p.AssetId")}
            FROM maintenance.Plans p WHERE p.TenantId = @tenantId AND p.PlanId = @planId
            """, new { tenantId, planId }, cancellationToken: ct));
    }

    public async Task<bool> UserInTenantAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM core.Users WHERE TenantId = @tenantId AND UserId = @userId AND IsActive = 1", new { tenantId, userId }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> AssetExistsAsync(Guid tenantId, Guid assetId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM plant.Assets WHERE TenantId = @tenantId AND AssetId = @assetId",
            new { tenantId, assetId }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> HasOpenOrderAsync(Guid tenantId, Guid assetId, string type, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return await con.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM maintenance.WorkOrders
            WHERE TenantId = @tenantId AND AssetId = @assetId AND Type = @type AND Status IN ('Open', 'InProgress')
            """, new { tenantId, assetId, type }, cancellationToken: ct)) > 0;
    }

    public async Task<IReadOnlyList<FailureStats>> FailuresAsync(Guid tenantId, DateTime from, DateTime to, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<FailureStats>(new CommandDefinition("""
            SELECT AssetId, COUNT(*) AS Failures,
                   CAST(SUM(DATEDIFF_BIG(MILLISECOND, IIF(StartTs > @from, StartTs, @from),
                        IIF(ISNULL(EndTs, SYSUTCDATETIME()) < @to, ISNULL(EndTs, SYSUTCDATETIME()), @to))) / 1000.0 AS float) AS DowntimeSeconds
            FROM production.Downtimes
            WHERE TenantId = @tenantId AND State = 'Fault' AND StartTs < @to AND ISNULL(EndTs, SYSUTCDATETIME()) > @from
            GROUP BY AssetId
            """, new { tenantId, from, to }, cancellationToken: ct))];
    }

    public async Task<(int Due, int OnTime)> PmComplianceAsync(Guid tenantId, DateTime from, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var r = await con.QuerySingleAsync<(int, int)>(new CommandDefinition("""
            SELECT COUNT(*), ISNULL(SUM(CASE WHEN Status = 'Done' AND CompletedAt <= DATEADD(DAY, 1, DueAt) THEN 1 ELSE 0 END), 0)
            FROM maintenance.WorkOrders
            WHERE TenantId = @tenantId AND Type = 'Preventive' AND DueAt >= @from AND DueAt < SYSUTCDATETIME() AND Status <> 'Cancelled'
            """, new { tenantId, from }, cancellationToken: ct));
        return r;
    }
}

public sealed class NotificationRepository(Db db) : INotificationRepository
{
    public async Task EnqueueAsync(Guid tenantId, string channel, string recipient, string subject, string body, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            INSERT core.Notifications (TenantId, Channel, Recipient, Subject, Body) VALUES (@tenantId, @channel, @recipient, @subject, @body)
            """, new { tenantId, channel, recipient, subject = subject.Length > 200 ? subject[..200] : subject, body = body.Length > 2000 ? body[..2000] : body },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Notification>> RecentAsync(Guid tenantId, int take, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Notification>(new CommandDefinition($"""
            SELECT TOP ({take}) NotificationId, Channel, Recipient, Subject, Status, Attempts, Error, CreatedAt, SentAt
            FROM core.Notifications WHERE TenantId = @tenantId ORDER BY NotificationId DESC
            """, new { tenantId }, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<PendingNotification>> PendingAsync(int take, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<PendingNotification>(new CommandDefinition($"""
            SELECT TOP ({take}) NotificationId, TenantId, Channel, Recipient, Subject, Body, Attempts
            FROM core.Notifications WHERE Status = 'Pending' ORDER BY NotificationId
            """, cancellationToken: ct))];
    }

    public async Task MarkAsync(long id, string status, string? error, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            UPDATE core.Notifications SET Status = @status, Error = @error, Attempts = Attempts + 1,
                   SentAt = CASE WHEN @status = 'Sent' THEN SYSUTCDATETIME() ELSE SentAt END
            WHERE NotificationId = @id
            """, new { id, status, error }, cancellationToken: ct));
    }
}

public sealed class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "alerts@divakar-factoryos.local";
}

/// <summary>E-mail through the SMTP server in configuration (Smtp:Host and friends); without a host, e-mails are skipped, not faked.</summary>
public sealed class SmtpTransport(SmtpOptions options) : INotificationTransport
{
    public string Channel => "Email";

    public async Task<bool> SendAsync(PendingNotification n, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            return false;
        }
        using var client = new SmtpClient(options.Host, options.Port) { EnableSsl = options.EnableSsl, Timeout = 15_000 };
        if (!string.IsNullOrEmpty(options.User))
        {
            client.Credentials = new System.Net.NetworkCredential(options.User, options.Password);
        }
        using var msg = new MailMessage(options.From, n.Recipient, n.Subject, n.Body + "\n\n— Divakar FactoryOS");
        await client.SendMailAsync(msg, ct);
        return true;
    }
}

/// <summary>Posts the alarm JSON to the company's webhook; any 2xx answer counts as delivered.</summary>
public sealed class WebhookTransport(HttpClient http) : INotificationTransport
{
    public string Channel => "Webhook";

    public async Task<bool> SendAsync(PendingNotification n, CancellationToken ct)
    {
        using var content = new StringContent(n.Body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var res = await http.PostAsync(n.Recipient, content, ct);
        res.EnsureSuccessStatusCode();
        return true;
    }
}
