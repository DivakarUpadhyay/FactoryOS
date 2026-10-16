using Dapper;
using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Auth;
using Divakar.FactoryOS.Infrastructure.Database;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

/// <summary>Sign-in data. Runs before a tenant is known, so it uses the system connection.</summary>
public sealed class AuthRepository(Db db) : IAuthRepository
{
    private const string UserSelect = """
        SELECT u.UserId, u.TenantId, u.Email, u.DisplayName, u.PasswordHash, u.IsSuperAdmin, u.IsActive, u.FailedLogins,
               u.LockedUntil, t.Status AS TenantStatus, t.TrialEndsAt, t.Name AS TenantName, t.PlanCode, p.Modules
        FROM core.Users u
        LEFT JOIN core.Tenants t ON t.TenantId = u.TenantId
        LEFT JOIN core.Plans p ON p.PlanCode = t.PlanCode
        """;

    public async Task<UserAuthRow?> FindByEmailAsync(string email, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return await con.QuerySingleOrDefaultAsync<UserAuthRow>(new CommandDefinition(
            UserSelect + " WHERE u.Email = @email", new { email }, cancellationToken: ct));
    }

    public async Task<UserAuthRow?> FindByIdAsync(Guid userId, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return await con.QuerySingleOrDefaultAsync<UserAuthRow>(new CommandDefinition(
            UserSelect + " WHERE u.UserId = @userId", new { userId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<string>> PermissionsAsync(Guid userId, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<string>(new CommandDefinition("""
            SELECT DISTINCT rp.PermissionCode
            FROM core.UserRoles ur JOIN core.RolePermissions rp ON rp.RoleId = ur.RoleId
            WHERE ur.UserId = @userId
            ORDER BY rp.PermissionCode
            """, new { userId }, cancellationToken: ct))];
    }

    public async Task RecordFailedLoginAsync(Guid userId, DateTime? lockUntil, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            UPDATE core.Users
            SET FailedLogins = CASE WHEN @lockUntil IS NULL THEN FailedLogins + 1 ELSE 0 END,
                LockedUntil = COALESCE(@lockUntil, LockedUntil)
            WHERE UserId = @userId
            """, new { userId, lockUntil }, cancellationToken: ct));
    }

    public async Task RecordLoginAsync(Guid userId, DateTime at, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition(
            "UPDATE core.Users SET FailedLogins = 0, LockedUntil = NULL, LastLoginAt = @at WHERE UserId = @userId",
            new { userId, at }, cancellationToken: ct));
    }

    public async Task StoreRefreshTokenAsync(string hash, Guid userId, Guid familyId, DateTime expiresAt, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            INSERT core.RefreshTokens (TokenHash, UserId, FamilyId, ExpiresAt) VALUES (@hash, @userId, @familyId, @expiresAt)
            """, new { hash, userId, familyId, expiresAt }, cancellationToken: ct));
    }

    public async Task<RefreshTokenRow?> FindRefreshTokenAsync(string hash, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return await con.QuerySingleOrDefaultAsync<RefreshTokenRow>(new CommandDefinition(
            "SELECT TokenHash, UserId, FamilyId, ExpiresAt, RevokedAt, ReplacedByHash FROM core.RefreshTokens WHERE TokenHash = @hash",
            new { hash }, cancellationToken: ct));
    }

    public async Task RotateRefreshTokenAsync(string oldHash, string newHash, Guid userId, Guid familyId, DateTime expiresAt,
        DateTime now, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);
        // Only one of two parallel refreshes with the same token can win; the loser finds it revoked.
        var won = await con.ExecuteAsync(new CommandDefinition("""
            UPDATE core.RefreshTokens SET RevokedAt = @now, ReplacedByHash = @newHash
            WHERE TokenHash = @oldHash AND RevokedAt IS NULL
            """, new { oldHash, newHash, now }, tx, cancellationToken: ct));
        if (won == 0)
        {
            await tx.RollbackAsync(ct);
            throw Application.Common.AppException.Unauthorized("Session expired. Sign in again.");
        }
        await con.ExecuteAsync(new CommandDefinition("""
            INSERT core.RefreshTokens (TokenHash, UserId, FamilyId, ExpiresAt) VALUES (@newHash, @userId, @familyId, @expiresAt)
            """, new { newHash, userId, familyId, expiresAt }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task RevokeFamilyAsync(Guid familyId, DateTime now, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition(
            "UPDATE core.RefreshTokens SET RevokedAt = COALESCE(RevokedAt, @now) WHERE FamilyId = @familyId",
            new { familyId, now }, cancellationToken: ct));
    }

    public async Task<bool> AnySuperAdminAsync(CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return await con.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM core.Users WHERE IsSuperAdmin = 1) THEN 1 ELSE 0 END", cancellationToken: ct));
    }

    public async Task<Guid> CreateSuperAdminAsync(string email, string displayName, string passwordHash, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT core.Users (TenantId, Email, DisplayName, PasswordHash, IsSuperAdmin)
            OUTPUT inserted.UserId
            VALUES (NULL, @email, @displayName, @passwordHash, 1)
            """, new { email, displayName, passwordHash }, cancellationToken: ct));
    }
}

/// <summary>Audit entries are written with the system connection and stamped with the caller.</summary>
public sealed class AuditLogWriter(Db db, ICurrentUser me) : IAuditLog
{
    public async Task WriteForAsync(Guid? tenantId, Guid userId, string action, string entity, CancellationToken ct = default)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            INSERT core.AuditLog (TenantId, UserId, Action, Entity, EntityId, IpAddress)
            VALUES (@tenantId, @userId, @action, @entity, @entityId, @ip)
            """, new { tenantId, userId, action, entity, entityId = userId.ToString(), ip = me.IpAddress }, cancellationToken: ct));
    }

    public async Task WriteAsync(string action, string entity, string? entityId, object? detail = null, CancellationToken ct = default)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            INSERT core.AuditLog (TenantId, UserId, Action, Entity, EntityId, Detail, IpAddress)
            VALUES (@tenantId, @userId, @action, @entity, @entityId, @detail, @ip)
            """, new
        {
            tenantId = me.IsAuthenticated ? me.TenantId : null,
            userId = me.IsAuthenticated ? me.UserId : (Guid?)null,
            action, entity, entityId,
            detail = detail is null ? null : System.Text.Json.JsonSerializer.Serialize(detail, Json.Options),
            ip = me.IpAddress,
        }, cancellationToken: ct));
    }
}

public sealed class AuditReader(Db db) : Application.Common.IAuditReader
{
    public async Task<IReadOnlyList<Application.Common.AuditEntry>> ReadAsync(Guid? tenantId, int take, long? beforeId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Application.Common.AuditEntry>(new CommandDefinition("""
            SELECT TOP (@take) a.AuditId, a.TenantId, a.UserId, u.Email AS UserEmail, a.Action, a.Entity, a.EntityId,
                   a.Detail, a.IpAddress, a.At
            FROM core.AuditLog a LEFT JOIN core.Users u ON u.UserId = a.UserId
            WHERE (@tenantId IS NULL OR a.TenantId = @tenantId) AND (@beforeId IS NULL OR a.AuditId < @beforeId)
            ORDER BY a.AuditId DESC
            """, new { tenantId, take, beforeId }, cancellationToken: ct))];
    }
}

internal static class Json
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);
}
