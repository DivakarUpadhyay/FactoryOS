using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Tenancy;
using Divakar.FactoryOS.Domain.Security;

namespace Divakar.FactoryOS.Application.Users;

public sealed record UserSummary(Guid UserId, string Email, string DisplayName, bool IsActive, DateTime? LastLoginAt,
    DateTime CreatedAt, IReadOnlyList<string> Roles);

public sealed record CreateUserRequest(string Email, string DisplayName, string Password, IReadOnlyList<string> Roles);
public sealed record UpdateUserRequest(string? DisplayName, bool? IsActive, IReadOnlyList<string>? Roles, string? NewPassword);

public sealed record RoleSummary(Guid RoleId, string Code, string Name, bool IsSystem, IReadOnlyList<string> Permissions, int Users);
public sealed record SaveRoleRequest(string Code, string Name, IReadOnlyList<string> Permissions);

public interface IUserRepository
{
    Task<IReadOnlyList<UserSummary>> UsersAsync(Guid tenantId, CancellationToken ct);
    Task<UserSummary?> UserAsync(Guid tenantId, Guid userId, CancellationToken ct);
    Task<bool> EmailExistsAsync(string email, CancellationToken ct);
    Task<Guid> CreateAsync(Guid tenantId, string email, string displayName, string passwordHash, IReadOnlyList<string> roles, CancellationToken ct);
    Task UpdateAsync(Guid tenantId, Guid userId, string displayName, bool isActive, IReadOnlyList<string> roles, string? passwordHash, CancellationToken ct);
    Task<int> ActiveAdminsAsync(Guid tenantId, CancellationToken ct);

    Task<IReadOnlyList<RoleSummary>> RolesAsync(Guid tenantId, CancellationToken ct);
    Task<Guid> SaveRoleAsync(Guid tenantId, Guid? roleId, string code, string name, IReadOnlyList<string> permissions, CancellationToken ct);
    Task DeleteRoleAsync(Guid tenantId, Guid roleId, CancellationToken ct);
}

public sealed class UserService(
    IUserRepository repo, ITenantRepository tenants, ICurrentUser me, IPasswordHasher hasher, IAuditLog audit)
{
    public Task<IReadOnlyList<UserSummary>> UsersAsync(CancellationToken ct) => repo.UsersAsync(me.RequireTenant(), ct);

    public async Task<Guid> CreateAsync(CreateUserRequest request, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        var email = Guard.Email(request.Email);
        var roles = await ValidRoles(tenantId, request.Roles, ct);
        var tenant = await tenants.TenantAsync(tenantId, ct) ?? throw AppException.NotFound("Tenant");
        var plan = await tenants.PlanAsync(tenant.PlanCode, ct) ?? throw AppException.NotFound("Plan");
        var (users, _, _) = await tenants.UsageAsync(tenantId, ct);
        if (users >= plan.MaxUsers)
        {
            throw AppException.Limit($"Your plan allows {plan.MaxUsers} users. Upgrade to add more.");
        }
        if (await repo.EmailExistsAsync(email, ct))
        {
            throw AppException.Conflict("This email is already used by another account.");
        }
        var id = await repo.CreateAsync(tenantId, email, Guard.Required(request.DisplayName, "Name", 150),
            hasher.Hash(Guard.Password(request.Password)), roles, ct);
        await audit.WriteAsync("user.create", "user", id.ToString(), new { email, roles }, ct);
        return id;
    }

    public async Task UpdateAsync(Guid userId, UpdateUserRequest request, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        var user = await repo.UserAsync(tenantId, userId, ct) ?? throw AppException.NotFound("User");
        var roles = request.Roles is null ? user.Roles : await ValidRoles(tenantId, request.Roles, ct);
        var active = request.IsActive ?? user.IsActive;
        var wasAdmin = user.IsActive && user.Roles.Contains(SystemRoles.FactoryAdmin);
        var staysAdmin = active && roles.Contains(SystemRoles.FactoryAdmin);
        if (wasAdmin && !staysAdmin && await repo.ActiveAdminsAsync(tenantId, ct) <= 1)
        {
            throw AppException.Conflict("The company must keep at least one active factory administrator.");
        }
        var name = request.DisplayName is null ? user.DisplayName : Guard.Required(request.DisplayName, "Name", 150);
        var hash = request.NewPassword is null ? null : hasher.Hash(Guard.Password(request.NewPassword));
        await repo.UpdateAsync(tenantId, userId, name, active, roles, hash, ct);
        await audit.WriteAsync("user.update", "user", userId.ToString(),
            new { request.DisplayName, request.IsActive, request.Roles, passwordChanged = hash is not null }, ct);
    }

    public Task<IReadOnlyList<RoleSummary>> RolesAsync(CancellationToken ct) => repo.RolesAsync(me.RequireTenant(), ct);

    public async Task<Guid> SaveRoleAsync(Guid? roleId, SaveRoleRequest request, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        var code = Guard.Code(request.Code, "Code");
        var perms = request.Permissions.Distinct().ToList();
        var bad = perms.Where(p => !Permissions.TenantAll.Contains(p)).ToList();
        if (bad.Count > 0)
        {
            throw AppException.Validation($"Unknown or platform-only permissions: {string.Join(", ", bad)}.");
        }
        var existing = await repo.RolesAsync(tenantId, ct);
        if (existing.Any(r => r.Code == code && r.RoleId != roleId))
        {
            throw AppException.Conflict($"A role with code '{code}' already exists.");
        }
        if (roleId is not null)
        {
            var role = existing.FirstOrDefault(r => r.RoleId == roleId) ?? throw AppException.NotFound("Role");
            if (role.Code == SystemRoles.FactoryAdmin && (code != role.Code || perms.Count != Permissions.TenantAll.Count))
            {
                throw AppException.Conflict("The factory administrator role always keeps every permission.");
            }
        }
        var id = await repo.SaveRoleAsync(tenantId, roleId, code, Guard.Required(request.Name, "Name", 100), perms, ct);
        await audit.WriteAsync(roleId is null ? "role.create" : "role.update", "role", id.ToString(), new { code, perms }, ct);
        return id;
    }

    public async Task DeleteRoleAsync(Guid roleId, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        var role = (await repo.RolesAsync(tenantId, ct)).FirstOrDefault(r => r.RoleId == roleId) ?? throw AppException.NotFound("Role");
        if (role.IsSystem)
        {
            throw AppException.Conflict("Built-in roles cannot be deleted; change their permissions instead.");
        }
        if (role.Users > 0)
        {
            throw AppException.Conflict($"{role.Users} users still have this role.");
        }
        await repo.DeleteRoleAsync(tenantId, roleId, ct);
        await audit.WriteAsync("role.delete", "role", roleId.ToString(), null, ct);
    }

    private async Task<IReadOnlyList<string>> ValidRoles(Guid tenantId, IReadOnlyList<string>? codes, CancellationToken ct)
    {
        var wanted = (codes ?? []).Distinct().ToList();
        if (wanted.Count == 0)
        {
            throw AppException.Validation("Give the user at least one role.");
        }
        var known = (await repo.RolesAsync(tenantId, ct)).Select(r => r.Code).ToHashSet();
        var unknown = wanted.Where(c => !known.Contains(c)).ToList();
        return unknown.Count > 0 ? throw AppException.Validation($"Unknown roles: {string.Join(", ", unknown)}.") : wanted;
    }
}
