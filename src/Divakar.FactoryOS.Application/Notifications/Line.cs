using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;

namespace Divakar.FactoryOS.Application.Notifications;

/// <summary>A tenant's LINE Messaging API channel, decrypted. Never sent to the browser.</summary>
public sealed record LineChannel(Guid TenantId, string ChannelSecret, string AccessToken, IReadOnlyList<string> Recipients, bool Enabled);

public sealed record LineContact(string SourceId, string Kind, string? LastText, DateTime LastSeen);

public sealed record LineSettingsView(bool Configured, bool Enabled, IReadOnlyList<string> Recipients, string WebhookUrl, IReadOnlyList<LineContact> Contacts);

/// <summary>Secret and token are optional on update: empty keeps the stored value.</summary>
public sealed record SaveLineRequest(string? ChannelSecret, string? AccessToken, IReadOnlyList<string>? Recipients, bool Enabled);

public interface ILineRepository
{
    Task<LineChannel?> GetAsync(Guid tenantId, CancellationToken ct);
    /// <summary>Reads without a signed-in tenant (webhook and worker).</summary>
    Task<LineChannel?> GetSystemAsync(Guid tenantId, CancellationToken ct);
    Task SaveAsync(Guid tenantId, string? channelSecret, string? accessToken, string recipients, bool enabled, CancellationToken ct);
    Task<IReadOnlyList<LineContact>> ContactsAsync(Guid tenantId, CancellationToken ct);
    Task RecordContactsAsync(Guid tenantId, IReadOnlyList<(string SourceId, string Kind, string? Text)> contacts, CancellationToken ct);
}

public static partial class LineRules
{
    [GeneratedRegex("^[UCR][0-9a-f]{32}$")]
    public static partial Regex RecipientId();

    public static IReadOnlyList<string> Parse(string? csv) =>
        [.. (csv ?? "").Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Distinct()];

    /// <summary>LINE signs each webhook body with HMAC-SHA256 of the channel secret, Base64 in X-Line-Signature.</summary>
    public static bool SignatureValid(string channelSecret, byte[] body, string? signature)
    {
        if (string.IsNullOrEmpty(signature))
        {
            return false;
        }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(channelSecret), body);
        byte[] given;
        try
        {
            given = Convert.FromBase64String(signature);
        }
        catch (FormatException)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    /// <summary>The user / group / room ids (and the first words they wrote) in a webhook body.</summary>
    public static IReadOnlyList<(string SourceId, string Kind, string? Text)> Sources(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var list = new List<(string, string, string?)>();
        if (!doc.RootElement.TryGetProperty("events", out var events))
        {
            return list;
        }
        foreach (var e in events.EnumerateArray())
        {
            if (!e.TryGetProperty("source", out var src) || !src.TryGetProperty("type", out var type))
            {
                continue;
            }
            var kind = type.GetString();
            var id = kind switch
            {
                "group" => src.TryGetProperty("groupId", out var g) ? g.GetString() : null,
                "room" => src.TryGetProperty("roomId", out var r) ? r.GetString() : null,
                "user" => src.TryGetProperty("userId", out var u) ? u.GetString() : null,
                _ => null,
            };
            if (id is null || kind is null || !RecipientId().IsMatch(id))
            {
                continue;
            }
            string? text = e.TryGetProperty("message", out var m) && m.TryGetProperty("text", out var t) ? t.GetString() : null;
            list.Add((id, kind, text is null ? null : text[..Math.Min(100, text.Length)]));
        }
        return list;
    }
}

public sealed class LineService(ILineRepository repo, INotificationRepository notifications, ICurrentUser me, IAuditLog audit)
{
    public async Task<LineSettingsView> GetAsync(string apiBase, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        var c = await repo.GetAsync(tenantId, ct);
        return new LineSettingsView(c is not null, c?.Enabled ?? false, c?.Recipients ?? [], $"{apiBase.TrimEnd('/')}/api/v1/integrations/line/webhook/{tenantId}",
            await repo.ContactsAsync(tenantId, ct));
    }

    public async Task SaveAsync(SaveLineRequest r, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        var existing = await repo.GetAsync(tenantId, ct);
        var secret = string.IsNullOrWhiteSpace(r.ChannelSecret) ? null : r.ChannelSecret.Trim();
        var token = string.IsNullOrWhiteSpace(r.AccessToken) ? null : r.AccessToken.Trim();
        if (existing is null && (secret is null || token is null))
        {
            throw AppException.Validation("Paste the channel secret and the channel access token from the LINE Developers console.");
        }
        if (secret is { Length: > 100 } || token is { Length: > 600 })
        {
            throw AppException.Validation("That does not look like a LINE channel secret / access token.");
        }
        var recipients = (r.Recipients ?? []).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
        if (recipients.FirstOrDefault(x => !LineRules.RecipientId().IsMatch(x)) is { } bad)
        {
            throw AppException.Validation($"'{bad}' is not a LINE user, group or room id (U… / C… / R… followed by 32 characters).");
        }
        if (recipients.Count > 20)
        {
            throw AppException.Validation("At most 20 LINE recipients.");
        }
        await repo.SaveAsync(tenantId, secret, token, string.Join(",", recipients), r.Enabled, ct);
        await audit.WriteAsync("line.save", "tenant", tenantId.ToString(), new { recipients = recipients.Count, r.Enabled, secretChanged = secret is not null, tokenChanged = token is not null }, ct);
    }

    public async Task<int> TestAsync(CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        var c = await repo.GetAsync(tenantId, ct) ?? throw AppException.Validation("Set up LINE first.");
        if (c.Recipients.Count == 0)
        {
            throw AppException.Validation("Add at least one recipient.");
        }
        foreach (var to in c.Recipients)
        {
            await notifications.EnqueueAsync(tenantId, "Line", to, "Test from Divakar FactoryOS", "This is a test message. Alarms will arrive here like this.", ct);
        }
        return c.Recipients.Count;
    }

    /// <summary>LINE's webhook: verifies the signature and remembers who wrote to the bot. Returns false when the signature is wrong.</summary>
    public async Task<bool> WebhookAsync(Guid tenantId, byte[] body, string? signature, CancellationToken ct)
    {
        var c = await repo.GetSystemAsync(tenantId, ct);
        if (c is null || !LineRules.SignatureValid(c.ChannelSecret, body, signature))
        {
            return false;
        }
        var sources = LineRules.Sources(body);
        if (sources.Count > 0)
        {
            await repo.RecordContactsAsync(tenantId, sources, ct);
        }
        return true;
    }
}
