using System.Text.Json;
using Dapper;
using Divakar.FactoryOS.Application.Tenancy;
using Divakar.FactoryOS.Domain.Security;
using Divakar.FactoryOS.Domain.Tenancy;
using Divakar.FactoryOS.Infrastructure.Database;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class TenantRepository(Db db) : ITenantRepository
{
    private sealed record PlanRow(string PlanCode, string Name, decimal PriceMonthly, string Currency, int MaxPlants,
        int MaxAssets, int MaxUsers, string Modules, bool IsActive, int SortOrder)
    {
        public Plan ToPlan() => new(PlanCode, Name, PriceMonthly, Currency, MaxPlants, MaxAssets, MaxUsers,
            JsonSerializer.Deserialize<List<string>>(Modules) ?? [], IsActive, SortOrder);
    }

    private const string PlanSelect =
        "SELECT PlanCode, Name, PriceMonthly, Currency, MaxPlants, MaxAssets, MaxUsers, Modules, IsActive, SortOrder FROM core.Plans";

    public async Task<IReadOnlyList<Plan>> PlansAsync(CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var rows = await con.QueryAsync<PlanRow>(new CommandDefinition(PlanSelect + " ORDER BY SortOrder", cancellationToken: ct));
        return [.. rows.Select(r => r.ToPlan())];
    }

    public async Task<Plan?> PlanAsync(string planCode, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var row = await con.QuerySingleOrDefaultAsync<PlanRow>(new CommandDefinition(
            PlanSelect + " WHERE PlanCode = @planCode", new { planCode }, cancellationToken: ct));
        return row?.ToPlan();
    }

    public async Task UpdatePlanAsync(string planCode, UpdatePlanRequest r, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            UPDATE core.Plans SET Name = @Name, PriceMonthly = @PriceMonthly, MaxPlants = @MaxPlants, MaxAssets = @MaxAssets,
                   MaxUsers = @MaxUsers, Modules = @modules, IsActive = @IsActive
            WHERE PlanCode = @planCode
            """, new { r.Name, r.PriceMonthly, r.MaxPlants, r.MaxAssets, r.MaxUsers, modules = JsonSerializer.Serialize(r.Modules),
            r.IsActive, planCode }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<TenantSummary>> TenantsAsync(CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<TenantSummary>(new CommandDefinition("""
            SELECT t.TenantId, t.Code, t.Name, t.PlanCode, t.Status, t.Country, t.TrialEndsAt, t.CreatedAt,
                   (SELECT COUNT(*) FROM core.Users u WHERE u.TenantId = t.TenantId) AS Users,
                   (SELECT COUNT(*) FROM plant.Nodes n WHERE n.TenantId = t.TenantId AND n.Level = 'Plant') AS Plants,
                   (SELECT COUNT(*) FROM plant.Assets a WHERE a.TenantId = t.TenantId) AS Assets
            FROM core.Tenants t
            ORDER BY t.CreatedAt DESC
            """, cancellationToken: ct))];
    }

    public async Task<Tenant?> TenantAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.QuerySingleOrDefaultAsync<Tenant>(new CommandDefinition("""
            SELECT TenantId, Code, Name, PlanCode, Status, Country, TimeZone, Currency, TrialEndsAt, CreatedAt
            FROM core.Tenants WHERE TenantId = @tenantId
            """, new { tenantId }, cancellationToken: ct));
    }

    public async Task<bool> CodeExistsAsync(string code, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM core.Tenants WHERE Code = @code", new { code }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> EmailExistsAsync(string email, CancellationToken ct)
    {
        // Emails are unique across every tenant, so this check must see all users.
        await using var con = await db.OpenSystemAsync(ct);
        return await con.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM core.Users WHERE Email = @email", new { email }, cancellationToken: ct)) > 0;
    }

    public async Task CreateAsync(NewTenant t, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            INSERT core.Tenants (TenantId, Code, Name, PlanCode, Status, Country, TimeZone, Currency, TrialEndsAt)
            VALUES (@TenantId, @Code, @Name, @PlanCode, @Status, @Country, @TimeZone, @Currency, @TrialEndsAt)
            """, t, tx, cancellationToken: ct));
        Guid? adminRole = null;
        foreach (var role in t.Roles)
        {
            var roleId = Guid.NewGuid();
            await con.ExecuteAsync(new CommandDefinition("""
                INSERT core.Roles (RoleId, TenantId, Code, Name, IsSystem) VALUES (@roleId, @TenantId, @Code, @Name, 1)
                """, new { roleId, t.TenantId, role.Code, role.Name }, tx, cancellationToken: ct));
            await con.ExecuteAsync(new CommandDefinition(
                "INSERT core.RolePermissions (RoleId, PermissionCode) VALUES (@roleId, @p)",
                role.Permissions.Select(p => new { roleId, p }), tx, cancellationToken: ct));
            if (role.Code == SystemRoles.FactoryAdmin)
            {
                adminRole = roleId;
            }
        }
        var userId = Guid.NewGuid();
        await con.ExecuteAsync(new CommandDefinition("""
            INSERT core.Users (UserId, TenantId, Email, DisplayName, PasswordHash) VALUES (@userId, @TenantId, @AdminEmail, @AdminName, @AdminPasswordHash);
            INSERT core.UserRoles (UserId, RoleId) VALUES (@userId, @adminRole);
            """, new { userId, t.TenantId, t.AdminEmail, t.AdminName, t.AdminPasswordHash, adminRole }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task UpdateAsync(Guid tenantId, string name, string planCode, string status, DateTime? trialEndsAt, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            UPDATE core.Tenants SET Name = @name, PlanCode = @planCode, Status = @status, TrialEndsAt = @trialEndsAt
            WHERE TenantId = @tenantId
            """, new { tenantId, name, planCode, status, trialEndsAt }, cancellationToken: ct));
    }

    public async Task<(int Users, int Plants, int Assets)> UsageAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.QuerySingleAsync<(int, int, int)>(new CommandDefinition("""
            SELECT (SELECT COUNT(*) FROM core.Users WHERE TenantId = @tenantId AND IsActive = 1),
                   (SELECT COUNT(*) FROM plant.Nodes WHERE TenantId = @tenantId AND Level = 'Plant'),
                   (SELECT COUNT(*) FROM plant.Assets WHERE TenantId = @tenantId)
            """, new { tenantId }, cancellationToken: ct));
    }
}
