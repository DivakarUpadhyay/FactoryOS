namespace Divakar.FactoryOS.Domain.Tenancy;

public static class TenantStatuses
{
    public const string Trial = "Trial";
    public const string Active = "Active";
    public const string Suspended = "Suspended";
    public const string Cancelled = "Cancelled";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Trial, Active, Suspended, Cancelled };

    /// <summary>Users of a tenant in these states may sign in.</summary>
    public static bool CanSignIn(string status, DateTime? trialEndsAt, DateTime nowUtc) =>
        status == Active || (status == Trial && (trialEndsAt is null || trialEndsAt > nowUtc));
}

public sealed record Plan(
    string PlanCode, string Name, decimal PriceMonthly, string Currency, int MaxPlants, int MaxAssets, int MaxUsers,
    IReadOnlyList<string> Modules, bool IsActive, int SortOrder);

public sealed record Tenant(
    Guid TenantId, string Code, string Name, string PlanCode, string Status, string Country, string TimeZone,
    string Currency, DateTime? TrialEndsAt, DateTime CreatedAt);
