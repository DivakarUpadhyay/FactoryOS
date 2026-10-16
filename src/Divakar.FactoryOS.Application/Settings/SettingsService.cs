using System.Text.Json;
using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Tenancy;

namespace Divakar.FactoryOS.Application.Settings;

/// <summary>A shift by local clock time; End before Start means it runs past midnight.</summary>
public sealed record Shift(string Name, string Start, string End, int BreakMinutes = 0);

public sealed record TenantSettings(
    string TimeZone, string Currency, decimal PeakRate, decimal OffPeakRate, byte PeakStartHour, byte PeakEndHour,
    decimal? ContractKw, decimal ElectricityFactor, decimal GasFactor, Guid? MainMeterAssetId, IReadOnlyList<Shift> Shifts,
    string? AlertEmails, string? AlertWebhookUrl = null, decimal? DemandChargePerKw = null)
{
    public static readonly IReadOnlyList<Shift> DefaultShifts =
        [new("Day", "08:00", "20:00", 60), new("Night", "20:00", "08:00", 60)];

    public static TenantSettings Defaults { get; } =
        new("Asia/Taipei", "TWD", 5.20m, 2.30m, 16, 22, null, 0.474m, 2.09m, null, DefaultShifts, null);

    public TimeZoneInfo Zone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>Electricity price for an hour that starts at this local time (weekends are off-peak).</summary>
    public decimal RateAt(DateTime local) =>
        local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && local.Hour >= PeakStartHour && local.Hour < PeakEndHour
            ? PeakRate : OffPeakRate;

    public bool IsPeak(DateTime local) => RateAt(local) == PeakRate && PeakRate != OffPeakRate;
}

public interface ISettingsRepository
{
    Task<TenantSettings?> GetAsync(Guid tenantId, CancellationToken ct);
    Task SaveAsync(Guid tenantId, TenantSettings settings, CancellationToken ct);
}

public sealed class SettingsService(ISettingsRepository repo, ICurrentUser me, IAuditLog audit)
{
    public async Task<TenantSettings> GetAsync(CancellationToken ct) => await repo.GetAsync(me.RequireTenant(), ct) ?? TenantSettings.Defaults;

    /// <summary>Settings for a tenant from background work (worker, reports), where nobody is signed in.</summary>
    public async Task<TenantSettings> ForTenantAsync(Guid tenantId, CancellationToken ct) => await repo.GetAsync(tenantId, ct) ?? TenantSettings.Defaults;

    public async Task SaveAsync(TenantSettings s, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(s.TimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            throw AppException.Validation($"Unknown time zone '{s.TimeZone}'. Use a name such as Asia/Taipei.");
        }
        if (s.PeakRate < 0 || s.OffPeakRate < 0 || s.ElectricityFactor < 0 || s.GasFactor < 0 || s.ContractKw < 0 || s.DemandChargePerKw < 0)
        {
            throw AppException.Validation("Rates, factors and the contract capacity cannot be negative.");
        }
        if (s.PeakStartHour >= s.PeakEndHour || s.PeakEndHour > 24)
        {
            throw AppException.Validation("The peak period must start before it ends, within 0–24 h.");
        }
        if (s.Shifts.Count == 0 || s.Shifts.Any(x => string.IsNullOrWhiteSpace(x.Name) || !TimeOnly.TryParse(x.Start, out _) || !TimeOnly.TryParse(x.End, out _)
                                                       || x.BreakMinutes is < 0 or > 240))
        {
            throw AppException.Validation("Each shift needs a name, a start and end time (HH:mm) and a break of 0–240 minutes.");
        }
        if (s.AlertEmails is { Length: > 0 } emails)
        {
            foreach (var e in emails.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                Guard.Email(e);
            }
        }
        if (!string.IsNullOrWhiteSpace(s.AlertWebhookUrl)
            && (!Uri.TryCreate(s.AlertWebhookUrl, UriKind.Absolute, out var hook) || hook.Scheme is not ("https" or "http") || s.AlertWebhookUrl.Length > 500))
        {
            throw AppException.Validation("The webhook must be a full http(s) address of at most 500 characters.");
        }
        await repo.SaveAsync(tenantId, s with { Currency = s.Currency.Trim().ToUpperInvariant(), AlertWebhookUrl = string.IsNullOrWhiteSpace(s.AlertWebhookUrl) ? null : s.AlertWebhookUrl.Trim() }, ct);
        await audit.WriteAsync("settings.update", "tenant", tenantId.ToString(), null, ct);
    }
}

/// <summary>Refuses calls to modules the tenant's plan does not include (the menu hides them too; this is the server-side check).</summary>
public sealed class ModuleGate(ITenantRepository tenants, ICurrentUser me)
{
    public async Task EnsureAsync(string module, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        var tenant = await tenants.TenantAsync(tenantId, ct) ?? throw AppException.NotFound("Tenant");
        var plan = await tenants.PlanAsync(tenant.PlanCode, ct) ?? throw AppException.NotFound("Plan");
        if (!plan.Modules.Contains(module))
        {
            throw AppException.Forbidden($"Your plan ({plan.Name}) does not include the {module} module. Upgrade to use it.");
        }
    }
}

public static class ShiftJson
{
    public static IReadOnlyList<Shift> Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? TenantSettings.DefaultShifts
            : JsonSerializer.Deserialize<List<Shift>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [.. TenantSettings.DefaultShifts];
}
