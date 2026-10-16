using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Domain.Security;
using Divakar.FactoryOS.Domain.Tenancy;

namespace Divakar.FactoryOS.Application.Tenancy;

public sealed record CreateTenantRequest(
    string Code, string Name, string PlanCode, string AdminEmail, string AdminName, string AdminPassword,
    string? Country, string? TimeZone, string? Currency);

public sealed record UpdateTenantRequest(string? Name, string? PlanCode, string? Status, DateTime? TrialEndsAt);

public sealed record UpdatePlanRequest(string Name, decimal PriceMonthly, int MaxPlants, int MaxAssets, int MaxUsers,
    IReadOnlyList<string> Modules, bool IsActive);

public sealed record TenantSummary(
    Guid TenantId, string Code, string Name, string PlanCode, string Status, string Country, DateTime? TrialEndsAt,
    DateTime CreatedAt, int Users, int Plants, int Assets);

public sealed record NewTenant(Guid TenantId, string Code, string Name, string PlanCode, string Status, string Country,
    string TimeZone, string Currency, DateTime? TrialEndsAt, string AdminEmail, string AdminName, string AdminPasswordHash,
    IReadOnlyList<RoleTemplate> Roles);

public interface ITenantRepository
{
    Task<IReadOnlyList<Plan>> PlansAsync(CancellationToken ct);
    Task<Plan?> PlanAsync(string planCode, CancellationToken ct);
    Task UpdatePlanAsync(string planCode, UpdatePlanRequest request, CancellationToken ct);
    Task<IReadOnlyList<TenantSummary>> TenantsAsync(CancellationToken ct);
    Task<Tenant?> TenantAsync(Guid tenantId, CancellationToken ct);
    Task<bool> CodeExistsAsync(string code, CancellationToken ct);
    Task<bool> EmailExistsAsync(string email, CancellationToken ct);
    Task CreateAsync(NewTenant tenant, CancellationToken ct);
    Task UpdateAsync(Guid tenantId, string name, string planCode, string status, DateTime? trialEndsAt, CancellationToken ct);

    /// <summary>Current usage of one tenant: users, plants and assets.</summary>
    Task<(int Users, int Plants, int Assets)> UsageAsync(Guid tenantId, CancellationToken ct);
}

public static class Modules
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        "plant", "energy", "power", "carbon", "production", "maintenance", "alarms", "robotics", "quality",
        "inventory", "ehs", "ai", "api",
    };
}

public sealed class TenantService(ITenantRepository repo, IPasswordHasher hasher, IClock clock, IAuditLog audit)
{
    public Task<IReadOnlyList<Plan>> PlansAsync(CancellationToken ct) => repo.PlansAsync(ct);

    public async Task UpdatePlanAsync(string planCode, UpdatePlanRequest request, CancellationToken ct)
    {
        _ = await repo.PlanAsync(planCode, ct) ?? throw AppException.NotFound("Plan");
        Guard.Required(request.Name, "Name", 100);
        if (request.PriceMonthly < 0 || request.MaxPlants < 1 || request.MaxAssets < 1 || request.MaxUsers < 1)
        {
            throw AppException.Validation("Price cannot be negative and every limit must be at least 1.");
        }
        var unknown = request.Modules.Where(m => !Modules.All.Contains(m)).ToList();
        if (unknown.Count > 0)
        {
            throw AppException.Validation($"Unknown modules: {string.Join(", ", unknown)}.");
        }
        await repo.UpdatePlanAsync(planCode, request with { Name = request.Name.Trim() }, ct);
        await audit.WriteAsync("plan.update", "plan", planCode, request, ct);
    }

    public Task<IReadOnlyList<TenantSummary>> TenantsAsync(CancellationToken ct) => repo.TenantsAsync(ct);

    public async Task<Guid> CreateAsync(CreateTenantRequest request, CancellationToken ct)
    {
        var code = Guard.Code(request.Code, "Code");
        var name = Guard.Required(request.Name, "Name", 200);
        var email = Guard.Email(request.AdminEmail);
        var plan = await repo.PlanAsync(request.PlanCode ?? "", ct) ?? throw AppException.Validation("Unknown plan.");
        if (!plan.IsActive)
        {
            throw AppException.Validation("This plan is not on sale.");
        }
        if (await repo.CodeExistsAsync(code, ct))
        {
            throw AppException.Conflict($"A tenant with code '{code}' already exists.");
        }
        if (await repo.EmailExistsAsync(email, ct))
        {
            throw AppException.Conflict("This email is already used by another account.");
        }
        var trial = plan.PlanCode == "trial";
        var tenant = new NewTenant(
            Guid.NewGuid(), code, name, plan.PlanCode, trial ? TenantStatuses.Trial : TenantStatuses.Active,
            (Guard.Optional(request.Country, "Country", 2) ?? "TW").ToUpperInvariant(),
            Guard.Optional(request.TimeZone, "Time zone", 64) ?? "Asia/Taipei",
            (Guard.Optional(request.Currency, "Currency", 3) ?? "TWD").ToUpperInvariant(),
            trial ? clock.UtcNow.AddDays(30) : null,
            email, Guard.Required(request.AdminName, "Admin name", 150), hasher.Hash(Guard.Password(request.AdminPassword)),
            SystemRoles.Templates);
        await repo.CreateAsync(tenant, ct);
        await audit.WriteAsync("tenant.create", "tenant", tenant.TenantId.ToString(), new { code, name, plan = plan.PlanCode }, ct);
        return tenant.TenantId;
    }

    public async Task UpdateAsync(Guid tenantId, UpdateTenantRequest request, CancellationToken ct)
    {
        var t = await repo.TenantAsync(tenantId, ct) ?? throw AppException.NotFound("Tenant");
        var status = request.Status ?? t.Status;
        if (!TenantStatuses.All.Contains(status))
        {
            throw AppException.Validation("Unknown status.");
        }
        var planCode = request.PlanCode ?? t.PlanCode;
        if (planCode != t.PlanCode)
        {
            var plan = await repo.PlanAsync(planCode, ct) ?? throw AppException.Validation("Unknown plan.");
            var (users, plants, assets) = await repo.UsageAsync(tenantId, ct);
            if (users > plan.MaxUsers || plants > plan.MaxPlants || assets > plan.MaxAssets)
            {
                throw AppException.Limit($"The tenant uses more than plan '{plan.Name}' allows " +
                                         $"({users} users, {plants} plants, {assets} assets).");
            }
        }
        var name = request.Name is null ? t.Name : Guard.Required(request.Name, "Name", 200);
        await repo.UpdateAsync(tenantId, name, planCode, status, request.TrialEndsAt ?? t.TrialEndsAt, ct);
        await audit.WriteAsync("tenant.update", "tenant", tenantId.ToString(), request, ct);
    }
}
