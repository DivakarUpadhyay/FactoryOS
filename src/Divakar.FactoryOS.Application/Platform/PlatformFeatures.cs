using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Tenancy;

namespace Divakar.FactoryOS.Application.Platform;

// ---------- Dashboards ----------

public sealed record Dashboard(Guid DashboardId, string Name, bool IsShared, Guid OwnerId, string OwnerName, bool Mine, JsonElement Widgets, DateTime UpdatedAt);

public sealed record SaveDashboardRequest(string Name, bool IsShared, JsonElement Widgets);

public interface IDashboardRepository
{
    Task<IReadOnlyList<Dashboard>> ListAsync(Guid tenantId, Guid userId, CancellationToken ct);
    Task<Guid> SaveAsync(Guid tenantId, Guid? id, Guid ownerId, string name, bool shared, string widgetsJson, CancellationToken ct);
    Task DeleteAsync(Guid tenantId, Guid id, CancellationToken ct);
}

public sealed class DashboardService(IDashboardRepository repo, ICurrentUser me)
{
    /// <summary>Widget types the web app knows how to draw.</summary>
    public static readonly string[] WidgetTypes = ["stat", "oee", "energy-hourly", "alarms", "machines", "tag", "output", "fleet", "stock-low", "text"];

    public Task<IReadOnlyList<Dashboard>> ListAsync(CancellationToken ct) => repo.ListAsync(me.RequireTenant(), me.UserId, ct);

    public async Task<Guid> SaveAsync(Guid? id, SaveDashboardRequest r, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        var name = Guard.Required(r.Name, "Name", 100);
        if (r.Widgets.ValueKind != JsonValueKind.Array || r.Widgets.GetArrayLength() > 30)
        {
            throw AppException.Validation("A dashboard holds a list of at most 30 widgets.");
        }
        foreach (var w in r.Widgets.EnumerateArray())
        {
            if (w.ValueKind != JsonValueKind.Object || !w.TryGetProperty("type", out var type) || !WidgetTypes.Contains(type.GetString()))
            {
                throw AppException.Validation($"Each widget needs a type: {string.Join(", ", WidgetTypes)}.");
            }
        }
        var json = r.Widgets.GetRawText();
        if (json.Length > 50_000)
        {
            throw AppException.Validation("The dashboard is too large.");
        }
        if (id is { } existing)
        {
            var d = (await repo.ListAsync(tenantId, me.UserId, ct)).FirstOrDefault(x => x.DashboardId == existing) ?? throw AppException.NotFound("Dashboard");
            if (!d.Mine)
            {
                throw AppException.Forbidden("Only the owner can change a dashboard. Save a copy instead.");
            }
        }
        return await repo.SaveAsync(tenantId, id, me.UserId, name, r.IsShared, json, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        var d = (await repo.ListAsync(tenantId, me.UserId, ct)).FirstOrDefault(x => x.DashboardId == id) ?? throw AppException.NotFound("Dashboard");
        if (!d.Mine)
        {
            throw AppException.Forbidden("Only the owner can delete a dashboard.");
        }
        await repo.DeleteAsync(tenantId, id, ct);
    }
}

// ---------- Billing ----------

public sealed record InvoiceLine(string Description, decimal Quantity, decimal UnitPrice, decimal Amount);

public sealed record Invoice(Guid InvoiceId, Guid TenantId, string TenantName, string Number, DateOnly PeriodStart, DateOnly PeriodEnd, string Currency,
    IReadOnlyList<InvoiceLine> Lines, decimal Subtotal, decimal TaxRate, decimal Tax, decimal Total, string Status, DateTime IssuedAt, DateOnly DueOn,
    DateTime? PaidAt, string? PaymentRef);

public sealed record BillingOverview(decimal Mrr, string Currency, int PayingTenants, int TrialTenants, decimal Outstanding, decimal PaidThisMonth,
    IReadOnlyList<Invoice> Invoices);

public sealed record Usage(string PlanCode, string PlanName, decimal PriceMonthly, string Currency, int Users, int MaxUsers, int Plants, int MaxPlants,
    int Assets, int MaxAssets, IReadOnlyList<string> Modules, string Status, DateTime? TrialEndsAt);

public sealed record PayRequest(string? PaymentRef);

/// <summary>A tenant and its plan as the invoice run sees them.</summary>
public sealed record BillableTenant(Guid TenantId, string Name, string Status, string PlanCode, string PlanName, decimal PriceMonthly, string Currency);

public interface IBillingRepository
{
    Task<IReadOnlyList<BillableTenant>> BillableAsync(CancellationToken ct);
    Task<IReadOnlyList<Invoice>> InvoicesAsync(Guid? tenantId, CancellationToken ct);
    Task<bool> ExistsAsync(Guid tenantId, DateOnly periodStart, CancellationToken ct);
    Task<int> CountForMonthAsync(DateOnly periodStart, CancellationToken ct);
    Task InsertAsync(Guid tenantId, string number, DateOnly start, DateOnly end, string currency, string linesJson, decimal subtotal, decimal taxRate,
        decimal tax, decimal total, DateOnly dueOn, CancellationToken ct);
    Task SetStatusAsync(Guid invoiceId, string status, string? paymentRef, CancellationToken ct);
}

public sealed class BillingOptions
{
    /// <summary>Taiwan business tax (VAT) 5 %.</summary>
    public decimal TaxRate { get; set; } = 0.05m;
    public int PaymentDays { get; set; } = 30;
}

public sealed class BillingService(IBillingRepository repo, ITenantRepository tenants, BillingOptions options, ICurrentUser me, IAuditLog audit)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>One invoice per active, paying tenant for the month (trial, suspended and price-on-request plans are skipped). Safe to run twice.</summary>
    public async Task<int> GenerateAsync(DateOnly month, CancellationToken ct)
    {
        var start = new DateOnly(month.Year, month.Month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        var created = 0;
        foreach (var t in (await repo.BillableAsync(ct)).Where(t => t.Status == "Active" && t.PriceMonthly > 0))
        {
            if (await repo.ExistsAsync(t.TenantId, start, ct))
            {
                continue;
            }
            var lines = new List<InvoiceLine> { new($"{t.PlanName} subscription, {start:yyyy-MM}", 1, t.PriceMonthly, t.PriceMonthly) };
            var subtotal = lines.Sum(l => l.Amount);
            var tax = Math.Round(subtotal * options.TaxRate, 0, MidpointRounding.AwayFromZero);
            var number = $"FOS-{start:yyyyMM}-{await repo.CountForMonthAsync(start, ct) + 1:0000}";
            await repo.InsertAsync(t.TenantId, number, start, end, t.Currency, JsonSerializer.Serialize(lines, Web), subtotal, options.TaxRate, tax, subtotal + tax,
                DateOnly.FromDateTime(DateTime.UtcNow).AddDays(options.PaymentDays), ct);
            created++;
        }
        return created;
    }

    public async Task<int> GenerateNowAsync(DateOnly? month, CancellationToken ct)
    {
        var m = month ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var n = await GenerateAsync(m, ct);
        await audit.WriteAsync("billing.generate", "invoice", $"{m:yyyy-MM}", new { created = n }, ct);
        return n;
    }

    public async Task<BillingOverview> OverviewAsync(CancellationToken ct)
    {
        var billable = await repo.BillableAsync(ct);
        var invoices = await repo.InvoicesAsync(null, ct);
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return new BillingOverview(billable.Where(t => t.Status == "Active").Sum(t => t.PriceMonthly), billable.FirstOrDefault()?.Currency ?? "TWD",
            billable.Count(t => t.Status == "Active" && t.PriceMonthly > 0), billable.Count(t => t.Status == "Trial"),
            invoices.Where(i => i.Status == "Issued").Sum(i => i.Total), invoices.Where(i => i.Status == "Paid" && i.PaidAt >= monthStart).Sum(i => i.Total), invoices);
    }

    public Task<IReadOnlyList<Invoice>> MyInvoicesAsync(CancellationToken ct) => repo.InvoicesAsync(me.RequireTenant(), ct);

    public async Task SetStatusAsync(Guid id, string status, PayRequest r, CancellationToken ct)
    {
        var inv = (await repo.InvoicesAsync(null, ct)).FirstOrDefault(i => i.InvoiceId == id) ?? throw AppException.NotFound("Invoice");
        if (inv.Status != "Issued")
        {
            throw AppException.Validation($"The invoice is already {inv.Status.ToLowerInvariant()}.");
        }
        await repo.SetStatusAsync(id, status, Guard.Optional(r.PaymentRef, "Payment reference", 100), ct);
        await audit.WriteAsync($"invoice.{status.ToLowerInvariant()}", "invoice", id.ToString(), new { inv.Number }, ct);
    }

    public async Task<Usage> UsageAsync(CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        var t = await tenants.TenantAsync(tenantId, ct) ?? throw AppException.NotFound("Tenant");
        var p = await tenants.PlanAsync(t.PlanCode, ct) ?? throw AppException.NotFound("Plan");
        var (users, plants, assets) = await tenants.UsageAsync(tenantId, ct);
        return new Usage(p.PlanCode, p.Name, p.PriceMonthly, p.Currency, users, p.MaxUsers, plants, p.MaxPlants, assets, p.MaxAssets, p.Modules, t.Status, t.TrialEndsAt);
    }
}

// ---------- Integration API keys ----------

public sealed record ApiKeyInfo(string Prefix, string Name, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? RevokedAt);

public sealed record NewApiKey(string Key, string Prefix, string Name);

public sealed record CreateApiKeyRequest(string Name);

public sealed record ApiKeyOwner(Guid TenantId, string TenantStatus);

public interface IApiKeyRepository
{
    Task InsertAsync(Guid tenantId, string hash, string prefix, string name, Guid userId, CancellationToken ct);
    Task<IReadOnlyList<ApiKeyInfo>> ListAsync(Guid tenantId, CancellationToken ct);
    Task<int> RevokeAsync(Guid tenantId, string prefix, CancellationToken ct);
    /// <summary>The company that owns an unrevoked key (system connection); also records the use.</summary>
    Task<ApiKeyOwner?> ResolveAsync(string hash, CancellationToken ct);
}

public sealed class ApiKeyService(IApiKeyRepository repo, ICurrentUser me, IAuditLog audit)
{
    /// <summary>What an integration key may do: read every module, change nothing.</summary>
    public static readonly string[] Permissions =
        ["plant.read", "energy.read", "production.read", "maintenance.read", "quality.read", "robotics.read", "alarms.read", "reports.read"];

    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public async Task<NewApiKey> CreateAsync(CreateApiKeyRequest r, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        var name = Guard.Required(r.Name, "Name", 100);
        if ((await repo.ListAsync(tenantId, ct)).Count(k => k.RevokedAt is null) >= 20)
        {
            throw AppException.Limit("A company can have at most 20 active API keys.");
        }
        var key = "fos_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(30)).Replace('+', 'x').Replace('/', 'y').TrimEnd('=');
        var prefix = key[..12];
        await repo.InsertAsync(tenantId, Hash(key), prefix, name, me.UserId, ct);
        await audit.WriteAsync("apikey.create", "apikey", prefix, new { name }, ct);
        return new NewApiKey(key, prefix, name);
    }

    public Task<IReadOnlyList<ApiKeyInfo>> ListAsync(CancellationToken ct) => repo.ListAsync(me.RequireTenant(), ct);

    public async Task RevokeAsync(string prefix, CancellationToken ct)
    {
        if (await repo.RevokeAsync(me.RequireTenant(), prefix, ct) == 0)
        {
            throw AppException.NotFound("API key");
        }
        await audit.WriteAsync("apikey.revoke", "apikey", prefix, null, ct);
    }
}
