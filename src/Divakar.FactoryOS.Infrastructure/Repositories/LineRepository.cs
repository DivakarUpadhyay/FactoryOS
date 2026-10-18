using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using Divakar.FactoryOS.Application.Notifications;
using Divakar.FactoryOS.Infrastructure.Database;
using Divakar.FactoryOS.Infrastructure.Security;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class LineRepository(Db db, SecretProtector protector) : ILineRepository
{
    private sealed record Row(Guid TenantId, string ChannelSecret, string AccessToken, string? Recipients, bool Enabled);

    private const string Select = "SELECT TenantId, ChannelSecret, AccessToken, Recipients, Enabled FROM core.LineChannels WHERE TenantId = @tenantId";

    private LineChannel? Map(Row? r) => r is null ? null
        : new LineChannel(r.TenantId, protector.Decrypt(r.ChannelSecret), protector.Decrypt(r.AccessToken), LineRules.Parse(r.Recipients), r.Enabled);

    public async Task<LineChannel?> GetAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return Map(await con.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(Select, new { tenantId }, cancellationToken: ct)));
    }

    public async Task<LineChannel?> GetSystemAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return Map(await con.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(Select, new { tenantId }, cancellationToken: ct)));
    }

    public async Task SaveAsync(Guid tenantId, string? channelSecret, string? accessToken, string recipients, bool enabled, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            MERGE core.LineChannels AS x USING (SELECT @tenantId AS TenantId) AS s ON x.TenantId = s.TenantId
            WHEN MATCHED THEN UPDATE SET ChannelSecret = ISNULL(@secret, ChannelSecret), AccessToken = ISNULL(@token, AccessToken),
                Recipients = @recipients, Enabled = @enabled, UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (TenantId, ChannelSecret, AccessToken, Recipients, Enabled) VALUES (@tenantId, @secret, @token, @recipients, @enabled);
            """, new
        {
            tenantId,
            secret = channelSecret is null ? null : protector.Encrypt(channelSecret),
            token = accessToken is null ? null : protector.Encrypt(accessToken),
            recipients,
            enabled,
        }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<LineContact>> ContactsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<LineContact>(new CommandDefinition(
            "SELECT TOP (50) SourceId, Kind, LastText, LastSeen FROM core.LineContacts WHERE TenantId = @tenantId ORDER BY LastSeen DESC",
            new { tenantId }, cancellationToken: ct))];
    }

    public async Task RecordContactsAsync(Guid tenantId, IReadOnlyList<(string SourceId, string Kind, string? Text)> contacts, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        foreach (var (sourceId, kind, text) in contacts)
        {
            await con.ExecuteAsync(new CommandDefinition("""
                MERGE core.LineContacts AS x USING (SELECT @tenantId AS TenantId, @sourceId AS SourceId) AS s ON x.TenantId = s.TenantId AND x.SourceId = s.SourceId
                WHEN MATCHED THEN UPDATE SET LastText = ISNULL(@text, LastText), LastSeen = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN INSERT (TenantId, SourceId, Kind, LastText) VALUES (@tenantId, @sourceId, @kind, @text);
                """, new { tenantId, sourceId, kind, text }, cancellationToken: ct));
        }
    }
}

public sealed class LineOptions
{
    /// <summary>Messaging API host; tests point it at a local stand-in.</summary>
    public string ApiBase { get; set; } = "https://api.line.me";
}

/// <summary>Pushes a text message through the tenant's own LINE Official Account (Messaging API push).</summary>
public sealed class LineTransport(HttpClient http, ILineRepository line, LineOptions options) : INotificationTransport
{
    public string Channel => "Line";

    public async Task<bool> SendAsync(PendingNotification n, CancellationToken ct)
    {
        if (await line.GetSystemAsync(n.TenantId, ct) is not { Enabled: true } c)
        {
            return false;
        }
        var text = $"{n.Subject}\n{n.Body}";
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{options.ApiBase.TrimEnd('/')}/v2/bot/message/push")
        {
            Content = JsonContent.Create(new { to = n.Recipient, messages = new[] { new { type = "text", text = text.Length > 4900 ? text[..4900] : text } } }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.AccessToken);
        req.Headers.Add("X-Line-Retry-Key", Guid.NewGuid().ToString());
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var detail = await res.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"LINE answered {(int)res.StatusCode}: {detail[..Math.Min(150, detail.Length)]}");
        }
        return true;
    }
}
