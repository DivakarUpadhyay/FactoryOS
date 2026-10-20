using System.Security.Cryptography;
using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;

namespace Divakar.FactoryOS.Application.Auth;

public sealed record TotpState(byte[]? Secret, bool Enabled, long? LastStep, IReadOnlyList<string> RecoveryHashes);

public sealed record AuthTokenRow(Guid UserId, string Purpose, DateTime ExpiresAt, DateTime? UsedAt, int Attempts);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record ForgotPasswordRequest(string Email);
public sealed record ResetPasswordRequest(string Token, string NewPassword);
public sealed record MfaRequest(string MfaToken, string Code);
public sealed record CodeRequest(string Code);
public sealed record DisableTwoFactorRequest(string Password, string Code);

public sealed record TwoFactorStatus(bool Enabled, int RecoveryCodesLeft);
public sealed record TwoFactorSetup(string Secret, string OtpAuthUri);
public sealed record RecoveryCodes(IReadOnlyList<string> Codes);
public sealed record ResetLink(string Url, DateTime ExpiresAt);

/// <summary>Either a session, or the second step: the user must now enter an authenticator code.</summary>
public sealed record LoginOutcome(AuthResult? Session, string? MfaToken);

public interface IAccountSecurityRepository
{
    Task<TotpState?> TotpAsync(Guid userId, CancellationToken ct);
    Task SetPendingSecretAsync(Guid userId, byte[] secret, CancellationToken ct);
    Task EnableTotpAsync(Guid userId, long step, IReadOnlyList<string> recoveryHashes, CancellationToken ct);
    Task DisableTotpAsync(Guid userId, CancellationToken ct);
    Task SetLastStepAsync(Guid userId, long step, CancellationToken ct);
    Task SetRecoveryHashesAsync(Guid userId, IReadOnlyList<string> hashes, CancellationToken ct);
    Task SetPasswordAsync(Guid userId, string passwordHash, CancellationToken ct);
    Task RevokeSessionsAsync(Guid userId, DateTime now, CancellationToken ct);
    Task CreateTokenAsync(string hash, Guid userId, string purpose, DateTime expiresAt, CancellationToken ct);
    Task<AuthTokenRow?> TokenAsync(string hash, CancellationToken ct);
    Task UseTokenAsync(string hash, DateTime now, CancellationToken ct);
    Task CountAttemptAsync(string hash, CancellationToken ct);
    Task<(Guid? TenantId, string Email, string DisplayName)?> UserAsync(Guid userId, CancellationToken ct);
}

/// <summary>Sends the password-reset e-mail. Returns false when no mail server is configured.</summary>
public interface IResetMailer
{
    Task<bool> SendAsync(string email, string displayName, string link, CancellationToken ct);
}

public sealed class SecurityOptions
{
    /// <summary>Where the web app runs, for links in e-mails.</summary>
    public string WebBaseUrl { get; set; } = "http://localhost:3000";
    public int ResetLinkMinutes { get; set; } = 30;
    public int MfaStepMinutes { get; set; } = 5;
    public int MaxCodeAttempts { get; set; } = 5;
}

public static class OneTimeToken
{
    public static (string Token, string Hash) New()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return (token, Totp.Hash(token));
    }
}

/// <summary>Password changes and resets, and two-step sign-in for the signed-in user (and admins acting for their users).</summary>
public sealed class AccountSecurityService(IAccountSecurityRepository repo, IAuthRepository auth, IPasswordHasher hasher, IResetMailer mailer,
    ICurrentUser me, IClock clock, IAuditLog audit, SecurityOptions options)
{
    public async Task ChangePasswordAsync(ChangePasswordRequest r, CancellationToken ct)
    {
        var user = await auth.FindByIdAsync(me.UserId, ct) ?? throw AppException.Unauthorized();
        if (!hasher.Verify(user.PasswordHash, r.CurrentPassword ?? ""))
        {
            throw AppException.Validation("The current password is wrong.");
        }
        if (r.NewPassword == r.CurrentPassword)
        {
            throw AppException.Validation("Choose a password different from the current one.");
        }
        await repo.SetPasswordAsync(user.UserId, hasher.Hash(Guard.Password(r.NewPassword)), ct);
        await repo.RevokeSessionsAsync(user.UserId, clock.UtcNow, ct);   // other devices must sign in again
        await audit.WriteAsync("password.change", "user", user.UserId.ToString(), null, ct);
    }

    /// <summary>Always answers the same way, so it cannot be used to find out which e-mail addresses have accounts.</summary>
    public async Task ForgotAsync(ForgotPasswordRequest r, CancellationToken ct)
    {
        var user = await auth.FindByEmailAsync((r.Email ?? "").Trim().ToLowerInvariant(), ct);
        if (user is null || !user.IsActive)
        {
            return;
        }
        var link = await NewResetLinkAsync(user.UserId, ct);
        var sent = await mailer.SendAsync(user.Email, user.DisplayName, link.Url, ct);
        await audit.WriteForAsync(user.TenantId, user.UserId, sent ? "password.reset-mailed" : "password.reset-not-mailed", "user", ct);
    }

    /// <summary>An admin creates a reset link for one of their users (for plants without e-mail); shown once.</summary>
    public async Task<ResetLink> AdminResetLinkAsync(Guid userId, CancellationToken ct)
    {
        await EnsureMayManageAsync(userId, ct);
        var link = await NewResetLinkAsync(userId, ct);
        await audit.WriteAsync("password.reset-link", "user", userId.ToString(), null, ct);
        return link;
    }

    private async Task<ResetLink> NewResetLinkAsync(Guid userId, CancellationToken ct)
    {
        var (token, hash) = OneTimeToken.New();
        var expires = clock.UtcNow.AddMinutes(options.ResetLinkMinutes);
        await repo.CreateTokenAsync(hash, userId, "reset", expires, ct);
        return new ResetLink($"{options.WebBaseUrl.TrimEnd('/')}/reset?token={token}", expires);
    }

    public async Task ResetAsync(ResetPasswordRequest r, CancellationToken ct)
    {
        var hash = Totp.Hash(r.Token ?? "");
        var row = await repo.TokenAsync(hash, ct);
        var now = clock.UtcNow;
        if (row is null || row.Purpose != "reset" || row.UsedAt is not null || row.ExpiresAt <= now)
        {
            throw AppException.Validation("This reset link is used or expired. Ask for a new one.");
        }
        var password = Guard.Password(r.NewPassword);
        await repo.UseTokenAsync(hash, now, ct);
        await repo.SetPasswordAsync(row.UserId, hasher.Hash(password), ct);
        await repo.RevokeSessionsAsync(row.UserId, now, ct);
        var user = await repo.UserAsync(row.UserId, ct);
        await audit.WriteForAsync(user?.TenantId, row.UserId, "password.reset", "user", ct);
    }

    public async Task<TwoFactorStatus> StatusAsync(CancellationToken ct)
    {
        var t = await repo.TotpAsync(me.UserId, ct);
        return new TwoFactorStatus(t?.Enabled ?? false, t?.Enabled == true ? t.RecoveryHashes.Count : 0);
    }

    /// <summary>Step 1: a new secret for the authenticator app. Two-step sign-in is not on until a code is confirmed.</summary>
    public async Task<TwoFactorSetup> BeginSetupAsync(CancellationToken ct)
    {
        if ((await repo.TotpAsync(me.UserId, ct))?.Enabled == true)
        {
            throw AppException.Conflict("Two-step sign-in is already on. Turn it off first to move to a new phone.");
        }
        var user = await repo.UserAsync(me.UserId, ct) ?? throw AppException.Unauthorized();
        var secret = Totp.NewSecret();
        await repo.SetPendingSecretAsync(me.UserId, secret, ct);
        return new TwoFactorSetup(Totp.Base32(secret), Totp.Uri("Divakar FactoryOS", user.Email, secret));
    }

    /// <summary>Step 2: the first code from the app proves it is set up; returns the recovery codes (shown once).</summary>
    public async Task<RecoveryCodes> EnableAsync(CodeRequest r, CancellationToken ct)
    {
        var t = await repo.TotpAsync(me.UserId, ct);
        if (t?.Secret is null || t.Enabled)
        {
            throw AppException.Validation("Start the set-up first.");
        }
        var step = Totp.Verify(t.Secret, r.Code, clock.UtcNow, null) ?? throw AppException.Validation("That code is not right. Check the phone's time and try the next code.");
        var codes = Totp.NewRecoveryCodes();
        await repo.EnableTotpAsync(me.UserId, step, [.. codes.Select(Totp.Hash)], ct);
        await audit.WriteAsync("2fa.enable", "user", me.UserId.ToString(), null, ct);
        return new RecoveryCodes(codes);
    }

    public async Task DisableAsync(DisableTwoFactorRequest r, CancellationToken ct)
    {
        var user = await auth.FindByIdAsync(me.UserId, ct) ?? throw AppException.Unauthorized();
        var t = await repo.TotpAsync(me.UserId, ct);
        if (t is not { Enabled: true, Secret: not null })
        {
            return;
        }
        if (!hasher.Verify(user.PasswordHash, r.Password ?? "") || (Totp.Verify(t.Secret, r.Code, clock.UtcNow, t.LastStep) is null && !t.RecoveryHashes.Contains(Totp.Hash(r.Code ?? ""))))
        {
            throw AppException.Validation("The password or the code is wrong.");
        }
        await repo.DisableTotpAsync(me.UserId, ct);
        await audit.WriteAsync("2fa.disable", "user", me.UserId.ToString(), null, ct);
    }

    public async Task<RecoveryCodes> NewRecoveryCodesAsync(CodeRequest r, CancellationToken ct)
    {
        var t = await repo.TotpAsync(me.UserId, ct);
        if (t is not { Enabled: true, Secret: not null } || Totp.Verify(t.Secret, r.Code, clock.UtcNow, t.LastStep) is not { } step)
        {
            throw AppException.Validation("Enter a current code from the authenticator app.");
        }
        var codes = Totp.NewRecoveryCodes();
        await repo.SetLastStepAsync(me.UserId, step, ct);
        await repo.SetRecoveryHashesAsync(me.UserId, [.. codes.Select(Totp.Hash)], ct);
        await audit.WriteAsync("2fa.recovery-codes", "user", me.UserId.ToString(), null, ct);
        return new RecoveryCodes(codes);
    }

    /// <summary>An admin turns off two-step sign-in for a user who lost their phone and recovery codes.</summary>
    public async Task AdminResetTwoFactorAsync(Guid userId, CancellationToken ct)
    {
        await EnsureMayManageAsync(userId, ct);
        await repo.DisableTotpAsync(userId, ct);
        await repo.RevokeSessionsAsync(userId, clock.UtcNow, ct);
        await audit.WriteAsync("2fa.admin-reset", "user", userId.ToString(), null, ct);
    }

    private async Task EnsureMayManageAsync(Guid userId, CancellationToken ct)
    {
        var target = await repo.UserAsync(userId, ct) ?? throw AppException.NotFound("User");
        if (!me.IsSuperAdmin && target.TenantId != me.RequireTenant())
        {
            throw AppException.NotFound("User");
        }
    }
}
