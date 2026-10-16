using System.Text.Json;
using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Alarms;
using Divakar.FactoryOS.Application.Settings;

namespace Divakar.FactoryOS.Application.Notifications;

public sealed record Notification(long NotificationId, string Channel, string Recipient, string Subject, string Status, int Attempts, string? Error,
    DateTime CreatedAt, DateTime? SentAt);

public sealed record PendingNotification(long NotificationId, Guid TenantId, string Channel, string Recipient, string Subject, string Body, int Attempts);

public interface INotificationRepository
{
    Task EnqueueAsync(Guid tenantId, string channel, string recipient, string subject, string body, CancellationToken ct);
    Task<IReadOnlyList<Notification>> RecentAsync(Guid tenantId, int take, CancellationToken ct);
    Task<IReadOnlyList<PendingNotification>> PendingAsync(int take, CancellationToken ct);
    Task MarkAsync(long id, string status, string? error, CancellationToken ct);
}

/// <summary>Delivers one message on one channel. Throws on failure; returns false when the channel is not configured.</summary>
public interface INotificationTransport
{
    string Channel { get; }
    Task<bool> SendAsync(PendingNotification n, CancellationToken ct);
}

/// <summary>Queues alarm messages to the company's alarm e-mail addresses and webhook (from Settings).</summary>
public sealed class AlarmNotifier(INotificationRepository repo, ISettingsRepository settings) : IAlarmNotifier
{
    public async Task NotifyAsync(Guid tenantId, string subject, string body, object payload, CancellationToken ct)
    {
        var s = await settings.GetAsync(tenantId, ct) ?? TenantSettings.Defaults;
        foreach (var email in (s.AlertEmails ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await repo.EnqueueAsync(tenantId, "Email", email, subject, body, ct);
        }
        if (!string.IsNullOrWhiteSpace(s.AlertWebhookUrl))
        {
            await repo.EnqueueAsync(tenantId, "Webhook", s.AlertWebhookUrl, subject,
                JsonSerializer.Serialize(new { subject, text = body, data = payload, source = "Divakar FactoryOS" }), ct);
        }
    }
}

/// <summary>Worker job: sends queued messages, retrying a failed one up to 5 times.</summary>
public sealed class NotificationSender(INotificationRepository repo, IEnumerable<INotificationTransport> transports)
{
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var sent = 0;
        foreach (var n in await repo.PendingAsync(50, ct))
        {
            var t = transports.FirstOrDefault(x => x.Channel == n.Channel);
            try
            {
                if (t is null || !await t.SendAsync(n, ct))
                {
                    await repo.MarkAsync(n.NotificationId, "Skipped", n.Channel == "Email" ? "E-mail (SMTP) is not configured on the server" : "Channel not configured", ct);
                    continue;
                }
                await repo.MarkAsync(n.NotificationId, "Sent", null, ct);
                sent++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await repo.MarkAsync(n.NotificationId, n.Attempts + 1 >= 5 ? "Failed" : "Pending", $"{ex.GetType().Name}: {ex.Message}"[..Math.Min(300, ex.Message.Length + ex.GetType().Name.Length + 2)], ct);
            }
        }
        return sent;
    }
}

public sealed class NotificationService(INotificationRepository repo, ICurrentUser me)
{
    public Task<IReadOnlyList<Notification>> RecentAsync(CancellationToken ct) => repo.RecentAsync(me.RequireTenant(), 200, ct);
}
