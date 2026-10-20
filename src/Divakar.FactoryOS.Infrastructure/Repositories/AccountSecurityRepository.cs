using System.Net.Mail;
using System.Text.Json;
using Dapper;
using Divakar.FactoryOS.Application.Auth;
using Divakar.FactoryOS.Infrastructure.Database;
using Divakar.FactoryOS.Infrastructure.Security;
using Microsoft.Extensions.Logging;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

/// <summary>Password and two-step sign-in data. Used before a tenant is known (sign-in, reset links), so it uses the system connection.</summary>
public sealed class AccountSecurityRepository(Db db, SecretProtector protector) : IAccountSecurityRepository
{
    private sealed record TotpRow(string? TotpSecret, bool TotpEnabled, long? TotpLastStep, string? RecoveryCodes);

    public async Task<TotpState?> TotpAsync(Guid userId, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        var r = await con.QuerySingleOrDefaultAsync<TotpRow>(new CommandDefinition(
            "SELECT TotpSecret, TotpEnabled, TotpLastStep, RecoveryCodes FROM core.Users WHERE UserId = @userId", new { userId }, cancellationToken: ct));
        return r is null ? null : new TotpState(r.TotpSecret is null ? null : Convert.FromBase64String(protector.Decrypt(r.TotpSecret)), r.TotpEnabled, r.TotpLastStep,
            string.IsNullOrEmpty(r.RecoveryCodes) ? [] : JsonSerializer.Deserialize<List<string>>(r.RecoveryCodes) ?? []);
    }

    private async Task ExecAsync(string sql, object args, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition(sql, args, cancellationToken: ct));
    }

    public Task SetPendingSecretAsync(Guid userId, byte[] secret, CancellationToken ct) =>
        ExecAsync("UPDATE core.Users SET TotpSecret = @secret, TotpEnabled = 0, TotpLastStep = NULL, RecoveryCodes = NULL WHERE UserId = @userId",
            new { userId, secret = protector.Encrypt(Convert.ToBase64String(secret)) }, ct);

    public Task EnableTotpAsync(Guid userId, long step, IReadOnlyList<string> recoveryHashes, CancellationToken ct) =>
        ExecAsync("UPDATE core.Users SET TotpEnabled = 1, TotpLastStep = @step, RecoveryCodes = @codes WHERE UserId = @userId",
            new { userId, step, codes = JsonSerializer.Serialize(recoveryHashes) }, ct);

    public Task DisableTotpAsync(Guid userId, CancellationToken ct) =>
        ExecAsync("UPDATE core.Users SET TotpSecret = NULL, TotpEnabled = 0, TotpLastStep = NULL, RecoveryCodes = NULL WHERE UserId = @userId", new { userId }, ct);

    public Task SetLastStepAsync(Guid userId, long step, CancellationToken ct) =>
        ExecAsync("UPDATE core.Users SET TotpLastStep = @step WHERE UserId = @userId AND (TotpLastStep IS NULL OR TotpLastStep < @step)", new { userId, step }, ct);

    public Task SetRecoveryHashesAsync(Guid userId, IReadOnlyList<string> hashes, CancellationToken ct) =>
        ExecAsync("UPDATE core.Users SET RecoveryCodes = @codes WHERE UserId = @userId", new { userId, codes = JsonSerializer.Serialize(hashes) }, ct);

    public Task SetPasswordAsync(Guid userId, string passwordHash, CancellationToken ct) =>
        ExecAsync("""
            UPDATE core.Users SET PasswordHash = @passwordHash, PasswordChangedAt = SYSUTCDATETIME(), FailedLogins = 0, LockedUntil = NULL WHERE UserId = @userId;
            UPDATE core.AuthTokens SET UsedAt = SYSUTCDATETIME() WHERE UserId = @userId AND Purpose = 'reset' AND UsedAt IS NULL;
            """, new { userId, passwordHash }, ct);

    public Task RevokeSessionsAsync(Guid userId, DateTime now, CancellationToken ct) =>
        ExecAsync("UPDATE core.RefreshTokens SET RevokedAt = COALESCE(RevokedAt, @now) WHERE UserId = @userId", new { userId, now }, ct);

    public Task CreateTokenAsync(string hash, Guid userId, string purpose, DateTime expiresAt, CancellationToken ct) =>
        ExecAsync("""
            DELETE core.AuthTokens WHERE ExpiresAt < DATEADD(DAY, -1, SYSUTCDATETIME());
            INSERT core.AuthTokens (TokenHash, UserId, Purpose, ExpiresAt) VALUES (@hash, @userId, @purpose, @expiresAt);
            """, new { hash, userId, purpose, expiresAt }, ct);

    public async Task<AuthTokenRow?> TokenAsync(string hash, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return await con.QuerySingleOrDefaultAsync<AuthTokenRow>(new CommandDefinition(
            "SELECT UserId, Purpose, ExpiresAt, UsedAt, Attempts FROM core.AuthTokens WHERE TokenHash = @hash", new { hash }, cancellationToken: ct));
    }

    public Task UseTokenAsync(string hash, DateTime now, CancellationToken ct) =>
        ExecAsync("UPDATE core.AuthTokens SET UsedAt = @now WHERE TokenHash = @hash", new { hash, now }, ct);

    public Task CountAttemptAsync(string hash, CancellationToken ct) =>
        ExecAsync("UPDATE core.AuthTokens SET Attempts = Attempts + 1 WHERE TokenHash = @hash", new { hash }, ct);

    private sealed record UserRow(Guid? TenantId, string Email, string DisplayName);

    public async Task<(Guid? TenantId, string Email, string DisplayName)?> UserAsync(Guid userId, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        var r = await con.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            "SELECT TenantId, Email, DisplayName FROM core.Users WHERE UserId = @userId", new { userId }, cancellationToken: ct));
        return r is null ? null : (r.TenantId, r.Email, r.DisplayName);
    }
}

/// <summary>Reset e-mails through the SMTP server in configuration. Without one nothing is sent (an admin can create a link instead).</summary>
public sealed class SmtpResetMailer(SmtpOptions options, ILogger<SmtpResetMailer> log) : IResetMailer
{
    public async Task<bool> SendAsync(string email, string displayName, string link, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            log.LogWarning("Password reset requested but no SMTP server is configured (Smtp:Host); no e-mail was sent. An administrator can create a reset link on the Users page.");
            return false;
        }
        using var client = new SmtpClient(options.Host, options.Port) { EnableSsl = options.EnableSsl, Timeout = 15_000 };
        if (!string.IsNullOrEmpty(options.User))
        {
            client.Credentials = new System.Net.NetworkCredential(options.User, options.Password);
        }
        using var msg = new MailMessage(options.From, email, "Reset your Divakar FactoryOS password",
            $"Hello {displayName},\n\nOpen this link to choose a new password (valid for 30 minutes, once):\n{link}\n\n" +
            "If you did not ask for this, ignore this e-mail; your password stays the same.\n\n— Divakar FactoryOS");
        await client.SendMailAsync(msg, ct);
        return true;
    }
}
