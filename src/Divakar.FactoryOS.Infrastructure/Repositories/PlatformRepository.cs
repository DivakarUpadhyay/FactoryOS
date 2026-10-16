using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Divakar.FactoryOS.Application.Assistant;
using Divakar.FactoryOS.Application.Platform;
using Divakar.FactoryOS.Application.Reports;
using Divakar.FactoryOS.Infrastructure.Database;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class DashboardRepository(Db db) : IDashboardRepository
{
    private sealed record Row(Guid DashboardId, string Name, bool IsShared, Guid OwnerId, string OwnerName, string Widgets, DateTime UpdatedAt);

    public async Task<IReadOnlyList<Dashboard>> ListAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var rows = await con.QueryAsync<Row>(new CommandDefinition("""
            SELECT d.DashboardId, d.Name, d.IsShared, d.OwnerId, u.DisplayName AS OwnerName, d.Widgets, d.UpdatedAt
            FROM core.Dashboards d JOIN core.Users u ON u.UserId = d.OwnerId
            WHERE d.TenantId = @tenantId AND (d.OwnerId = @userId OR d.IsShared = 1)
            ORDER BY CASE WHEN d.OwnerId = @userId THEN 0 ELSE 1 END, d.Name
            """, new { tenantId, userId }, cancellationToken: ct));
        return [.. rows.Select(r => new Dashboard(r.DashboardId, r.Name, r.IsShared, r.OwnerId, r.OwnerName, r.OwnerId == userId,
            JsonDocument.Parse(r.Widgets).RootElement.Clone(), r.UpdatedAt))];
    }

    public async Task<Guid> SaveAsync(Guid tenantId, Guid? id, Guid ownerId, string name, bool shared, string widgetsJson, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        if (id is not null)
        {
            await con.ExecuteAsync(new CommandDefinition("""
                UPDATE core.Dashboards SET Name = @name, IsShared = @shared, Widgets = @widgetsJson, UpdatedAt = SYSUTCDATETIME()
                WHERE TenantId = @tenantId AND DashboardId = @id AND OwnerId = @ownerId
                """, new { tenantId, id, ownerId, name, shared, widgetsJson }, cancellationToken: ct));
            return id.Value;
        }
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT core.Dashboards (TenantId, OwnerId, Name, IsShared, Widgets) OUTPUT inserted.DashboardId VALUES (@tenantId, @ownerId, @name, @shared, @widgetsJson)
            """, new { tenantId, ownerId, name, shared, widgetsJson }, cancellationToken: ct));
    }

    public async Task DeleteAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("DELETE core.Dashboards WHERE TenantId = @tenantId AND DashboardId = @id", new { tenantId, id }, cancellationToken: ct));
    }
}

public sealed class ReportScheduleRepository(Db db) : IReportScheduleRepository
{
    public async Task<IReadOnlyList<ReportSchedule>> ListAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<ReportSchedule>(new CommandDefinition(
            "SELECT ScheduleId, ReportType, Recipients, LocalHour, LastSentOn, IsActive FROM core.ReportSchedules WHERE TenantId = @tenantId ORDER BY ReportType",
            new { tenantId }, cancellationToken: ct))];
    }

    public async Task<Guid> SaveAsync(Guid tenantId, Guid? id, SaveScheduleRequest r, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        if (id is not null)
        {
            await con.ExecuteAsync(new CommandDefinition("""
                UPDATE core.ReportSchedules SET ReportType = @ReportType, Recipients = @Recipients, LocalHour = @LocalHour, IsActive = @IsActive
                WHERE TenantId = @tenantId AND ScheduleId = @id
                """, new { tenantId, id, r.ReportType, r.Recipients, r.LocalHour, r.IsActive }, cancellationToken: ct));
            return id.Value;
        }
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT core.ReportSchedules (TenantId, ReportType, Recipients, LocalHour, IsActive) OUTPUT inserted.ScheduleId
            VALUES (@tenantId, @ReportType, @Recipients, @LocalHour, @IsActive)
            """, new { tenantId, r.ReportType, r.Recipients, r.LocalHour, r.IsActive }, cancellationToken: ct));
    }

    public async Task DeleteAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("DELETE core.ReportSchedules WHERE TenantId = @tenantId AND ScheduleId = @id", new { tenantId, id }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<DueSchedule>> ActiveAsync(CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<DueSchedule>(new CommandDefinition("""
            SELECT r.TenantId, r.ScheduleId, r.ReportType, r.Recipients, r.LocalHour, r.LastSentOn, ISNULL(s.TimeZone, 'Asia/Taipei') AS TimeZone
            FROM core.ReportSchedules r
            JOIN core.Tenants t ON t.TenantId = r.TenantId AND t.Status IN ('Trial', 'Active')
            LEFT JOIN core.TenantSettings s ON s.TenantId = r.TenantId
            WHERE r.IsActive = 1
            """, cancellationToken: ct))];
    }

    public async Task MarkSentAsync(Guid scheduleId, DateOnly day, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("UPDATE core.ReportSchedules SET LastSentOn = @day WHERE ScheduleId = @scheduleId",
            new { scheduleId, day = day.ToDateTime(TimeOnly.MinValue) }, cancellationToken: ct));
    }
}

public sealed class BillingRepository(Db db) : IBillingRepository
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record Row(Guid InvoiceId, Guid TenantId, string TenantName, string Number, DateTime PeriodStart, DateTime PeriodEnd, string Currency, string Lines,
        decimal Subtotal, decimal TaxRate, decimal Tax, decimal Total, string Status, DateTime IssuedAt, DateTime DueOn, DateTime? PaidAt, string? PaymentRef);

    public async Task<IReadOnlyList<BillableTenant>> BillableAsync(CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<BillableTenant>(new CommandDefinition("""
            SELECT t.TenantId, t.Name, t.Status, t.PlanCode, p.Name AS PlanName, p.PriceMonthly, p.Currency
            FROM core.Tenants t JOIN core.Plans p ON p.PlanCode = t.PlanCode
            """, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<Invoice>> InvoicesAsync(Guid? tenantId, CancellationToken ct)
    {
        // operators read every company's invoices; a company reads only its own (the RLS policy also enforces this)
        await using var con = tenantId is null ? await db.OpenSystemAsync(ct) : await db.OpenAsync(ct);
        var rows = await con.QueryAsync<Row>(new CommandDefinition("""
            SELECT i.InvoiceId, i.TenantId, t.Name AS TenantName, i.Number, i.PeriodStart, i.PeriodEnd, i.Currency, i.Lines, i.Subtotal, i.TaxRate, i.Tax, i.Total,
                   i.Status, i.IssuedAt, i.DueOn, i.PaidAt, i.PaymentRef
            FROM core.Invoices i JOIN core.Tenants t ON t.TenantId = i.TenantId
            WHERE @tenantId IS NULL OR i.TenantId = @tenantId
            ORDER BY i.PeriodStart DESC, i.Number DESC
            """, new { tenantId }, cancellationToken: ct));
        return [.. rows.Select(r => new Invoice(r.InvoiceId, r.TenantId, r.TenantName, r.Number, DateOnly.FromDateTime(r.PeriodStart), DateOnly.FromDateTime(r.PeriodEnd),
            r.Currency, JsonSerializer.Deserialize<List<InvoiceLine>>(r.Lines, Web) ?? [], r.Subtotal, r.TaxRate, r.Tax, r.Total, r.Status, r.IssuedAt,
            DateOnly.FromDateTime(r.DueOn), r.PaidAt, r.PaymentRef))];
    }

    public async Task<bool> ExistsAsync(Guid tenantId, DateOnly periodStart, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return await con.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM core.Invoices WHERE TenantId = @tenantId AND PeriodStart = @start",
            new { tenantId, start = periodStart.ToDateTime(TimeOnly.MinValue) }, cancellationToken: ct)) > 0;
    }

    public async Task<int> CountForMonthAsync(DateOnly periodStart, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return await con.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM core.Invoices WHERE PeriodStart = @start",
            new { start = periodStart.ToDateTime(TimeOnly.MinValue) }, cancellationToken: ct));
    }

    public async Task InsertAsync(Guid tenantId, string number, DateOnly start, DateOnly end, string currency, string linesJson, decimal subtotal, decimal taxRate,
        decimal tax, decimal total, DateOnly dueOn, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            INSERT core.Invoices (TenantId, Number, PeriodStart, PeriodEnd, Currency, Lines, Subtotal, TaxRate, Tax, Total, DueOn)
            VALUES (@tenantId, @number, @start, @end, @currency, @linesJson, @subtotal, @taxRate, @tax, @total, @due)
            """, new
        {
            tenantId, number, start = start.ToDateTime(TimeOnly.MinValue), end = end.ToDateTime(TimeOnly.MinValue), currency, linesJson, subtotal, taxRate, tax, total,
            due = dueOn.ToDateTime(TimeOnly.MinValue),
        }, cancellationToken: ct));
    }

    public async Task SetStatusAsync(Guid invoiceId, string status, string? paymentRef, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            UPDATE core.Invoices SET Status = @status, PaymentRef = @paymentRef, PaidAt = CASE WHEN @status = 'Paid' THEN SYSUTCDATETIME() ELSE PaidAt END
            WHERE InvoiceId = @invoiceId AND Status = 'Issued'
            """, new { invoiceId, status, paymentRef }, cancellationToken: ct));
    }
}

public sealed class ApiKeyRepository(Db db) : IApiKeyRepository
{
    public async Task InsertAsync(Guid tenantId, string hash, string prefix, string name, Guid userId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition(
            "INSERT core.ApiKeys (KeyHash, TenantId, Name, Prefix, CreatedBy) VALUES (@hash, @tenantId, @name, @prefix, @userId)",
            new { hash, tenantId, name, prefix, userId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ApiKeyInfo>> ListAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<ApiKeyInfo>(new CommandDefinition(
            "SELECT Prefix, Name, CreatedAt, LastUsedAt, RevokedAt FROM core.ApiKeys WHERE TenantId = @tenantId ORDER BY CreatedAt DESC",
            new { tenantId }, cancellationToken: ct))];
    }

    public async Task<int> RevokeAsync(Guid tenantId, string prefix, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteAsync(new CommandDefinition(
            "UPDATE core.ApiKeys SET RevokedAt = SYSUTCDATETIME() WHERE TenantId = @tenantId AND Prefix = @prefix AND RevokedAt IS NULL",
            new { tenantId, prefix }, cancellationToken: ct));
    }

    public async Task<ApiKeyOwner?> ResolveAsync(string hash, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return await con.QuerySingleOrDefaultAsync<ApiKeyOwner>(new CommandDefinition("""
            UPDATE core.ApiKeys SET LastUsedAt = SYSUTCDATETIME()
            WHERE KeyHash = @hash AND RevokedAt IS NULL AND (LastUsedAt IS NULL OR LastUsedAt < DATEADD(MINUTE, -1, SYSUTCDATETIME()));
            SELECT k.TenantId, t.Status AS TenantStatus FROM core.ApiKeys k JOIN core.Tenants t ON t.TenantId = k.TenantId
            WHERE k.KeyHash = @hash AND k.RevokedAt IS NULL;
            """, new { hash }, cancellationToken: ct));
    }
}

public sealed class AiOptions
{
    /// <summary>Anthropic API key, from user secrets or the FACTORYOS_Ai__AnthropicApiKey environment variable — never from the database.</summary>
    public string? AnthropicApiKey { get; set; }
    public string Model { get; set; } = "claude-sonnet-5-5";
    public string Endpoint { get; set; } = "https://api.anthropic.com/v1/messages";
    public int MaxTokens { get; set; } = 1024;
}

/// <summary>Claude through the Messages API. Disabled (and never called) when no key is configured.</summary>
public sealed class ClaudeClient(HttpClient http, AiOptions options) : IAiClient
{
    public bool Enabled => !string.IsNullOrWhiteSpace(options.AnthropicApiKey);

    public string? Model => Enabled ? options.Model : null;

    public async Task<string> AskAsync(string system, string question, IReadOnlyList<ChatTurn> history, CancellationToken ct)
    {
        var messages = history.Where(h => h.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(h.Text))
            .Select(h => new { role = h.Role, content = h.Text }).Append(new { role = "user", content = question }).ToList();
        using var req = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
        {
            Content = JsonContent.Create(new { model = options.Model, max_tokens = options.MaxTokens, system, messages }),
        };
        req.Headers.Add("x-api-key", options.AnthropicApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            var detail = TryError(body);
            throw new HttpRequestException($"AI service answered {(int)res.StatusCode}{(detail is null ? "" : $": {detail}")}");
        }
        using var doc = JsonDocument.Parse(body);
        return string.Concat(doc.RootElement.GetProperty("content").EnumerateArray()
            .Where(c => c.GetProperty("type").GetString() == "text").Select(c => c.GetProperty("text").GetString())).Trim();
    }

    private static string? TryError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
