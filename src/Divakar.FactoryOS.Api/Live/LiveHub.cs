using Divakar.FactoryOS.Application.Live;
using Divakar.FactoryOS.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Divakar.FactoryOS.Api.Live;

/// <summary>
/// Browsers connect here (with their access token) and receive "values" and "status" messages for their own
/// company only: each connection joins its tenant's group, and the broadcaster sends per group.
/// </summary>
[Authorize]
public sealed class LiveHub : Hub
{
    public static string Group(Guid tenantId) => $"t:{tenantId:N}";

    public override async Task OnConnectedAsync()
    {
        if (Guid.TryParse(Context.User?.FindFirst(ClaimNames.Tenant)?.Value, out var tenantId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, Group(tenantId));
        }
        await base.OnConnectedAsync();
    }
}

/// <summary>Pushes changed tag values every second and changed asset states every two seconds.</summary>
public sealed class LiveBroadcaster(IServiceScopeFactory scopes, IHubContext<LiveHub> hub, ILogger<LiveBroadcaster> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTime? since = null;
        long? lastAlarm = null;
        var tracker = new StatusTracker();
        var dedupe = new ChangeDeduper();
        var tick = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var feed = scope.ServiceProvider.GetRequiredService<ILiveFeed>();
                since ??= await feed.NowAsync(stoppingToken);
                var (all, marker) = await feed.ChangesSinceAsync(since.Value.AddSeconds(-2), stoppingToken);
                if (marker > since)
                {
                    since = marker;
                }
                var changes = dedupe.Fresh(all);
                if (changes.Count > 0)
                {
                    foreach (var g in changes.GroupBy(c => c.TenantId))
                    {
                        await hub.Clients.Group(LiveHub.Group(g.Key)).SendAsync("values",
                            g.Select(c => new { c.AssetId, c.TagId, c.Ts, c.Value, c.Quality }), stoppingToken);
                    }
                }
                lastAlarm ??= await feed.LastAlarmIdAsync(stoppingToken);
                var raised = await feed.AlarmsAfterAsync(lastAlarm.Value, stoppingToken);
                if (raised.Count > 0)
                {
                    lastAlarm = raised.Max(a => a.AlarmId);
                    foreach (var g in raised.GroupBy(a => a.TenantId))
                    {
                        await hub.Clients.Group(LiveHub.Group(g.Key)).SendAsync("alarms",
                            g.Select(a => new { a.AlarmId, a.AssetId, a.Severity, a.Message }), stoppingToken);
                    }
                }
                if (tick++ % 2 == 0)
                {
                    foreach (var g in tracker.Changed(await feed.StatusesAsync(stoppingToken)).GroupBy(s => s.TenantId))
                    {
                        await hub.Clients.Group(LiveHub.Group(g.Key)).SendAsync("status",
                            g.Select(s => new { s.AssetId, s.Status, s.LastSeenAt, s.LastError }), stoppingToken);
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogWarning(ex, "Live broadcast pass failed; retrying");
            }
            await Task.Delay(1000, stoppingToken);
        }
    }
}
