namespace Divakar.FactoryOS.Application.Abstractions;

public interface IClock
{
    DateTime UtcNow { get; }
}

/// <summary>Who is calling. Filled from the access token by the API; empty for anonymous calls.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid UserId { get; }
    Guid? TenantId { get; }
    bool IsSuperAdmin { get; }
    IReadOnlySet<string> Permissions { get; }
    string? IpAddress { get; }

    /// <summary>The caller's tenant; fails for platform operators, who have none.</summary>
    Guid RequireTenant();
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string hash, string password);
}

public sealed record AccessToken(string Token, DateTime ExpiresAt);

public interface ITokenService
{
    AccessToken CreateAccessToken(Guid userId, Guid? tenantId, bool isSuperAdmin, string email, string displayName,
        IEnumerable<string> permissions);

    /// <summary>A new random refresh token and the hash that is stored.</summary>
    (string Token, string Hash) CreateRefreshToken();

    string HashRefreshToken(string token);
}

public interface IAuditLog
{
    Task WriteAsync(string action, string entity, string? entityId, object? detail = null, CancellationToken ct = default);

    /// <summary>For events before sign-in completes (login, failed login), where the caller is known only by the account.</summary>
    Task WriteForAsync(Guid? tenantId, Guid userId, string action, string entity, CancellationToken ct = default);
}
