using Dapper;
using Divakar.FactoryOS.Application.Users;
using Divakar.FactoryOS.Domain.Security;
using Divakar.FactoryOS.Infrastructure.Database;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class UserRepository(Db db) : IUserRepository
{
    private sealed record UserRow(Guid UserId, string Email, string DisplayName, bool IsActive, DateTime? LastLoginAt,
        DateTime CreatedAt, string? Roles);

    private sealed record RoleRow(Guid RoleId, string Code, string Name, bool IsSystem, string? Permissions, int Users);

    private const string UserSelect = """
        SELECT u.UserId, u.Email, u.DisplayName, u.IsActive, u.LastLoginAt, u.CreatedAt,
               (SELECT STRING_AGG(r.Code, ',') FROM core.UserRoles ur JOIN core.Roles r ON r.RoleId = ur.RoleId
                WHERE ur.UserId = u.UserId) AS Roles
        FROM core.Users u
        WHERE u.TenantId = @tenantId
        """;

    private static UserSummary Map(UserRow r) => new(r.UserId, r.Email, r.DisplayName, r.IsActive, r.LastLoginAt, r.CreatedAt,
        string.IsNullOrEmpty(r.Roles) ? [] : r.Roles.Split(','));

    public async Task<IReadOnlyList<UserSummary>> UsersAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var rows = await con.QueryAsync<UserRow>(new CommandDefinition(UserSelect + " ORDER BY u.DisplayName", new { tenantId }, cancellationToken: ct));
        return [.. rows.Select(Map)];
    }

    public async Task<UserSummary?> UserAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var row = await con.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            UserSelect + " AND u.UserId = @userId", new { tenantId, userId }, cancellationToken: ct));
        return row is null ? null : Map(row);
    }

    public async Task<bool> EmailExistsAsync(string email, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);   // unique across all tenants
        return await con.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM core.Users WHERE Email = @email", new { email }, cancellationToken: ct)) > 0;
    }

    public async Task<Guid> CreateAsync(Guid tenantId, string email, string displayName, string passwordHash,
        IReadOnlyList<string> roles, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);
        var userId = Guid.NewGuid();
        await con.ExecuteAsync(new CommandDefinition("""
            INSERT core.Users (UserId, TenantId, Email, DisplayName, PasswordHash) VALUES (@userId, @tenantId, @email, @displayName, @passwordHash)
            """, new { userId, tenantId, email, displayName, passwordHash }, tx, cancellationToken: ct));
        await SetRoles(con, tx, tenantId, userId, roles, ct);
        await tx.CommitAsync(ct);
        return userId;
    }

    public async Task UpdateAsync(Guid tenantId, Guid userId, string displayName, bool isActive, IReadOnlyList<string> roles,
        string? passwordHash, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            UPDATE core.Users SET DisplayName = @displayName, IsActive = @isActive,
                   PasswordHash = COALESCE(@passwordHash, PasswordHash)
            WHERE UserId = @userId AND TenantId = @tenantId;
            -- deactivating or a new password ends every open session of the user
            IF @isActive = 0 OR @passwordHash IS NOT NULL
                UPDATE core.RefreshTokens SET RevokedAt = SYSUTCDATETIME() WHERE UserId = @userId AND RevokedAt IS NULL;
            """, new { tenantId, userId, displayName, isActive, passwordHash }, tx, cancellationToken: ct));
        await SetRoles(con, tx, tenantId, userId, roles, ct);
        await tx.CommitAsync(ct);
    }

    private static async Task SetRoles(Microsoft.Data.SqlClient.SqlConnection con, System.Data.Common.DbTransaction tx,
        Guid tenantId, Guid userId, IReadOnlyList<string> roles, CancellationToken ct)
    {
        await con.ExecuteAsync(new CommandDefinition("""
            DELETE core.UserRoles WHERE UserId = @userId;
            INSERT core.UserRoles (UserId, RoleId)
            SELECT @userId, RoleId FROM core.Roles WHERE TenantId = @tenantId AND Code IN @roles;
            """, new { userId, tenantId, roles }, tx, cancellationToken: ct));
    }

    public async Task<int> ActiveAdminsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(DISTINCT u.UserId) FROM core.Users u
            JOIN core.UserRoles ur ON ur.UserId = u.UserId JOIN core.Roles r ON r.RoleId = ur.RoleId
            WHERE u.TenantId = @tenantId AND u.IsActive = 1 AND r.Code = @admin
            """, new { tenantId, admin = SystemRoles.FactoryAdmin }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<RoleSummary>> RolesAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var rows = await con.QueryAsync<RoleRow>(new CommandDefinition("""
            SELECT r.RoleId, r.Code, r.Name, r.IsSystem,
                   (SELECT STRING_AGG(rp.PermissionCode, ',') FROM core.RolePermissions rp WHERE rp.RoleId = r.RoleId) AS Permissions,
                   (SELECT COUNT(*) FROM core.UserRoles ur WHERE ur.RoleId = r.RoleId) AS Users
            FROM core.Roles r WHERE r.TenantId = @tenantId
            ORDER BY r.IsSystem DESC, r.Name
            """, new { tenantId }, cancellationToken: ct));
        return [.. rows.Select(r => new RoleSummary(r.RoleId, r.Code, r.Name, r.IsSystem,
            string.IsNullOrEmpty(r.Permissions) ? [] : r.Permissions.Split(','), r.Users))];
    }

    public async Task<Guid> SaveRoleAsync(Guid tenantId, Guid? roleId, string code, string name, IReadOnlyList<string> permissions,
        CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);
        var id = roleId ?? Guid.NewGuid();
        await con.ExecuteAsync(new CommandDefinition("""
            IF EXISTS (SELECT 1 FROM core.Roles WHERE RoleId = @id AND TenantId = @tenantId)
                UPDATE core.Roles SET Code = @code, Name = @name WHERE RoleId = @id AND TenantId = @tenantId;
            ELSE
                INSERT core.Roles (RoleId, TenantId, Code, Name, IsSystem) VALUES (@id, @tenantId, @code, @name, 0);
            DELETE core.RolePermissions WHERE RoleId = @id;
            """, new { id, tenantId, code, name }, tx, cancellationToken: ct));
        await con.ExecuteAsync(new CommandDefinition(
            "INSERT core.RolePermissions (RoleId, PermissionCode) VALUES (@id, @p)",
            permissions.Select(p => new { id, p }), tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task DeleteRoleAsync(Guid tenantId, Guid roleId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition(
            "DELETE core.Roles WHERE RoleId = @roleId AND TenantId = @tenantId AND IsSystem = 0",
            new { roleId, tenantId }, cancellationToken: ct));
    }
}
