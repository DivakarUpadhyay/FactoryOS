namespace Divakar.FactoryOS.Application.Live;

/// <summary>A tag value that changed, as pushed to browsers.</summary>
public sealed record LiveChange(Guid TenantId, Guid AssetId, Guid TagId, DateTime Ts, double? Value, byte Quality, DateTime StoredAt);

/// <summary>An asset's connection state, as pushed to browsers when it changes.</summary>
public sealed record AssetStatusRow(Guid TenantId, Guid AssetId, string Status, DateTime? LastSeenAt, string? LastError);

/// <summary>Reads what changed across every tenant (system connection); the API pushes it to each tenant's browsers.</summary>
public interface ILiveFeed
{
    /// <summary>Values stored after the marker (server clock), with the marker for the next call.</summary>
    Task<(IReadOnlyList<LiveChange> Changes, DateTime Marker)> ChangesSinceAsync(DateTime since, CancellationToken ct);
    Task<DateTime> NowAsync(CancellationToken ct);
    Task<IReadOnlyList<AssetStatusRow>> StatusesAsync(CancellationToken ct);
    /// <summary>Alarms raised after the given id (all companies).</summary>
    Task<IReadOnlyList<NewAlarm>> AlarmsAfterAsync(long afterId, CancellationToken ct);
    Task<long> LastAlarmIdAsync(CancellationToken ct);
}

public sealed record NewAlarm(Guid TenantId, long AlarmId, Guid AssetId, string Severity, string Message);

/// <summary>
/// Reads overlap by a couple of seconds so a value committed just after a read is not missed; this drops the repeats.
/// </summary>
public sealed class ChangeDeduper
{
    private readonly Dictionary<Guid, DateTime> sent = [];

    public IReadOnlyList<LiveChange> Fresh(IEnumerable<LiveChange> changes)
    {
        var fresh = new List<LiveChange>();
        foreach (var c in changes)
        {
            if (!sent.TryGetValue(c.TagId, out var at) || c.StoredAt > at)
            {
                sent[c.TagId] = c.StoredAt;
                fresh.Add(c);
            }
        }
        return fresh;
    }
}

/// <summary>Remembers asset states between passes and returns only the ones that changed.</summary>
public sealed class StatusTracker
{
    private readonly Dictionary<Guid, (string Status, string? Error)> known = [];

    public IReadOnlyList<AssetStatusRow> Changed(IEnumerable<AssetStatusRow> rows)
    {
        var changed = new List<AssetStatusRow>();
        foreach (var r in rows)
        {
            if (!known.TryGetValue(r.AssetId, out var k) || k.Status != r.Status || k.Error != r.LastError)
            {
                changed.Add(r);
                known[r.AssetId] = (r.Status, r.LastError);
            }
        }
        return changed;
    }
}

/// <summary>One asset on the floor plan: where it sits, its area and line, state and live power.</summary>
public sealed record FloorAsset(Guid AssetId, Guid NodeId, string? AreaName, string? LineName, string Code, string Name, string AssetTypeCode,
    string Category, string Status, int? LayoutX, int? LayoutY, Guid? PowerTagId, double? PowerKw, decimal? RatedPowerKw);

public interface IFloorRepository
{
    Task<IReadOnlyList<FloorAsset>> FloorAsync(Guid tenantId, CancellationToken ct);
}

public sealed class FloorService(IFloorRepository repo, Abstractions.ICurrentUser me)
{
    public Task<IReadOnlyList<FloorAsset>> FloorAsync(CancellationToken ct) => repo.FloorAsync(me.RequireTenant(), ct);
}
