using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Domain.Security;
using Divakar.FactoryOS.Domain.Tenancy;

namespace Divakar.FactoryOS.Application.Auth;

public sealed record LoginRequest(string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record SetupRequest(string Email, string DisplayName, string Password);

public sealed record AuthResult(string AccessToken, DateTime AccessTokenExpiresAt, string RefreshToken, MeResponse User);

public sealed record MeResponse(
    Guid UserId, string Email, string DisplayName, bool IsSuperAdmin, Guid? TenantId, string? TenantName,
    string? PlanCode, IReadOnlyList<string> Modules, IReadOnlyList<string> Permissions);

public sealed record UserAuthRow(
    Guid UserId, Guid? TenantId, string Email, string DisplayName, string PasswordHash, bool IsSuperAdmin, bool IsActive,
    int FailedLogins, DateTime? LockedUntil, string? TenantStatus, DateTime? TrialEndsAt, string? TenantName,
    string? PlanCode, string? Modules);

public sealed record RefreshTokenRow(string TokenHash, Guid UserId, Guid FamilyId, DateTime ExpiresAt, DateTime? RevokedAt, string? ReplacedByHash);

public interface IAuthRepository
{
    Task<UserAuthRow?> FindByEmailAsync(string email, CancellationToken ct);
    Task<UserAuthRow?> FindByIdAsync(Guid userId, CancellationToken ct);
    Task<IReadOnlyList<string>> PermissionsAsync(Guid userId, CancellationToken ct);
    Task RecordFailedLoginAsync(Guid userId, DateTime? lockUntil, CancellationToken ct);
    Task RecordLoginAsync(Guid userId, DateTime at, CancellationToken ct);
    Task StoreRefreshTokenAsync(string hash, Guid userId, Guid familyId, DateTime expiresAt, CancellationToken ct);
    Task<RefreshTokenRow?> FindRefreshTokenAsync(string hash, CancellationToken ct);
    Task RotateRefreshTokenAsync(string oldHash, string newHash, Guid userId, Guid familyId, DateTime expiresAt, DateTime now, CancellationToken ct);
    Task RevokeFamilyAsync(Guid familyId, DateTime now, CancellationToken ct);
    Task<bool> AnySuperAdminAsync(CancellationToken ct);
    Task<Guid> CreateSuperAdminAsync(string email, string displayName, string passwordHash, CancellationToken ct);
}

public sealed class AuthOptions
{
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 14;

    /// <summary>
    /// A rotated token presented again within this many seconds, while its replacement is still unused, is a lost
    /// response (the tab closed or navigated mid-refresh), not theft: the session continues from the replacement.
    /// </summary>
    public int ReuseGraceSeconds { get; set; } = 60;
}

public sealed class AuthService(
    IAuthRepository repo, IPasswordHasher hasher, ITokenService tokens, IClock clock, IAuditLog audit, AuthOptions options)
{
    // Checked against a real hash when the email is unknown, so response time does not reveal which emails exist.
    private static string? dummyHash;

    public async Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var email = (request.Email ?? "").Trim().ToLowerInvariant();
        var user = await repo.FindByEmailAsync(email, ct);
        var now = clock.UtcNow;
        if (user is null)
        {
            dummyHash ??= hasher.Hash(Guid.NewGuid().ToString());
            hasher.Verify(dummyHash, request.Password ?? "");
            throw AppException.Unauthorized();
        }
        if (user.LockedUntil > now)
        {
            throw AppException.Unauthorized("Too many failed attempts. Try again later.");
        }
        if (!hasher.Verify(user.PasswordHash, request.Password ?? ""))
        {
            var failed = user.FailedLogins + 1;
            DateTime? lockUntil = failed >= options.MaxFailedLogins ? now.AddMinutes(options.LockoutMinutes) : null;
            await repo.RecordFailedLoginAsync(user.UserId, lockUntil, ct);
            await audit.WriteForAsync(user.TenantId, user.UserId, "login.failed", "user", ct);
            throw AppException.Unauthorized();
        }
        EnsureMaySignIn(user, now);
        await repo.RecordLoginAsync(user.UserId, now, ct);
        await audit.WriteForAsync(user.TenantId, user.UserId, "login", "user", ct);
        return await IssueAsync(user, Guid.NewGuid(), null, ct);
    }

    public async Task<AuthResult> RefreshAsync(RefreshRequest request, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var row = await repo.FindRefreshTokenAsync(tokens.HashRefreshToken(request.RefreshToken ?? ""), ct)
                  ?? throw AppException.Unauthorized("Session expired. Sign in again.");
        if (row.RevokedAt is not null)
        {
            if (row.ReplacedByHash is not null && (now - row.RevokedAt.Value).TotalSeconds <= options.ReuseGraceSeconds
                && await repo.FindRefreshTokenAsync(row.ReplacedByHash, ct) is { RevokedAt: null } unused)
            {
                var again = await repo.FindByIdAsync(unused.UserId, ct) ?? throw AppException.Unauthorized();
                EnsureMaySignIn(again, now);
                return await IssueAsync(again, unused.FamilyId, unused.TokenHash, ct);
            }
            // An already-used token came back after its replacement was used: someone may have copied it.
            // End the whole session family.
            await repo.RevokeFamilyAsync(row.FamilyId, now, ct);
            throw AppException.Unauthorized("Session expired. Sign in again.");
        }
        if (row.ExpiresAt <= now)
        {
            throw AppException.Unauthorized("Session expired. Sign in again.");
        }
        var user = await repo.FindByIdAsync(row.UserId, ct) ?? throw AppException.Unauthorized();
        EnsureMaySignIn(user, now);
        return await IssueAsync(user, row.FamilyId, row.TokenHash, ct);
    }

    public async Task LogoutAsync(RefreshRequest request, CancellationToken ct)
    {
        var row = await repo.FindRefreshTokenAsync(tokens.HashRefreshToken(request.RefreshToken ?? ""), ct);
        if (row is not null)
        {
            await repo.RevokeFamilyAsync(row.FamilyId, clock.UtcNow, ct);
        }
    }

    public async Task<MeResponse> MeAsync(Guid userId, CancellationToken ct)
    {
        var user = await repo.FindByIdAsync(userId, ct) ?? throw AppException.Unauthorized();
        return ToMe(user, await PermissionsFor(user, ct));
    }

    public async Task<bool> NeedsSetupAsync(CancellationToken ct) => !await repo.AnySuperAdminAsync(ct);

    /// <summary>Creates the first platform operator. Works only while none exists.</summary>
    public async Task<Guid> SetupAsync(SetupRequest request, CancellationToken ct)
    {
        if (await repo.AnySuperAdminAsync(ct))
        {
            throw AppException.Conflict("The platform is already set up.");
        }
        var id = await repo.CreateSuperAdminAsync(Guard.Email(request.Email), Guard.Required(request.DisplayName, "Name", 150),
            hasher.Hash(Guard.Password(request.Password)), ct);
        await audit.WriteAsync("setup", "user", id.ToString(), null, ct);
        return id;
    }

    private void EnsureMaySignIn(UserAuthRow user, DateTime now)
    {
        if (!user.IsActive)
        {
            throw AppException.Unauthorized("This account is deactivated.");
        }
        if (!user.IsSuperAdmin && !TenantStatuses.CanSignIn(user.TenantStatus ?? "", user.TrialEndsAt, now))
        {
            throw AppException.Unauthorized("Your company's subscription is not active. Contact your administrator.");
        }
    }

    private async Task<IReadOnlyList<string>> PermissionsFor(UserAuthRow user, CancellationToken ct) =>
        user.IsSuperAdmin ? [.. Permissions.PlatformOnly, .. Permissions.TenantAll] : await repo.PermissionsAsync(user.UserId, ct);

    private async Task<AuthResult> IssueAsync(UserAuthRow user, Guid familyId, string? replacing, CancellationToken ct)
    {
        var perms = await PermissionsFor(user, ct);
        var access = tokens.CreateAccessToken(user.UserId, user.TenantId, user.IsSuperAdmin, user.Email, user.DisplayName, perms);
        var (refresh, hash) = tokens.CreateRefreshToken();
        var expires = clock.UtcNow.AddDays(options.RefreshTokenDays);
        if (replacing is null)
        {
            await repo.StoreRefreshTokenAsync(hash, user.UserId, familyId, expires, ct);
        }
        else
        {
            await repo.RotateRefreshTokenAsync(replacing, hash, user.UserId, familyId, expires, clock.UtcNow, ct);
        }
        return new AuthResult(access.Token, access.ExpiresAt, refresh, ToMe(user, perms));
    }

    private static MeResponse ToMe(UserAuthRow u, IReadOnlyList<string> perms) =>
        new(u.UserId, u.Email, u.DisplayName, u.IsSuperAdmin, u.TenantId, u.TenantName, u.PlanCode,
            string.IsNullOrEmpty(u.Modules) ? [] : System.Text.Json.JsonSerializer.Deserialize<List<string>>(u.Modules) ?? [],
            perms);
}
