using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Application.Settings;
using Divakar.FactoryOS.Domain.Plant;

namespace Divakar.FactoryOS.Application.Production;

public sealed record OperatorStats(Guid AssetId, bool CountsParts, int PendingDowntimes, Guid? WorkOrderId, string? WorkOrderCode, string? ProductName);

public sealed record OperatorMachine(Guid AssetId, string Name, string? LineName, string Status, bool CountsParts, double GoodToday, double ScrapToday,
    int PendingDowntimes, Guid? WorkOrderId, string? WorkOrderCode, string? ProductName);

public sealed record OperatorHome(DateTime Since, IReadOnlyList<OperatorMachine> Machines, IReadOnlyList<string> ScrapReasons);

public sealed record CountRequest(Guid AssetId, int Good, int Scrap, string? ScrapReason, string? Note);

public sealed record OperatorEntry(long EntryId, Guid AssetId, string AssetName, int Good, int Scrap, string? ScrapReason, string? Note, string? EnteredBy,
    DateTime EnteredAt);

public interface IOperatorRepository
{
    Task<IReadOnlyList<OperatorStats>> StatsAsync(Guid tenantId, DateTime since, CancellationToken ct);
    /// <summary>Creates the asset's virtual manual-count tags the first time.</summary>
    Task<(Guid GoodTag, Guid ScrapTag)> CounterTagsAsync(Guid tenantId, Guid assetId, CancellationToken ct);
    /// <summary>Saves the entry and returns the asset's running totals including it (entries for one asset are serialised).</summary>
    Task<(long EntryId, int TotalGood, int TotalScrap)> AddEntryAsync(Guid tenantId, CountRequest r, Guid? userId, CancellationToken ct);
    Task<IReadOnlyList<OperatorEntry>> EntriesAsync(Guid tenantId, Guid? assetId, DateTime since, CancellationToken ct);
    Task<IReadOnlyList<string>> RecentScrapReasonsAsync(Guid tenantId, CancellationToken ct);
}

public static class ManualCountTags
{
    public const string Good = "virtual:manual-good";
    public const string Scrap = "virtual:manual-scrap";
}

/// <summary>
/// The shop-floor app: an operator's machines, good / scrap counts typed in by hand, and (through the existing endpoints)
/// downtime reasons and due inspections. Hand counts move virtual counter tags, so OEE, shift reports and energy per part include them.
/// </summary>
public sealed class OperatorService(IOperatorRepository repo, IProductionRepository production, Energy.IEnergyRepository counters, IIngestionStore store,
    SettingsService settings,
    ModuleGate gate, ICurrentUser me, IAuditLog audit)
{
    public static readonly string[] DefaultScrapReasons = ["Dimension out of tolerance", "Surface defect", "Burr", "Wrong material", "Setup piece", "Damaged in handling"];

    private async Task<DateTime> StartOfTodayUtcAsync(CancellationToken ct)
    {
        var zone = (await settings.GetAsync(ct)).Zone();
        var localMidnight = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone).Date;
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localMidnight, DateTimeKind.Unspecified), zone);
    }

    public async Task<OperatorHome> HomeAsync(CancellationToken ct)
    {
        await gate.EnsureAsync("production", ct);
        var tenantId = me.RequireTenant();
        var since = await StartOfTodayUtcAsync(ct);
        var stats = (await repo.StatsAsync(tenantId, since, ct)).ToDictionary(s => s.AssetId);
        var deltas = await counters.CounterDeltasAsync(tenantId, ["part_count", "scrap_count"], since, DateTime.UtcNow.AddMinutes(1), ct);
        double Today(Guid asset, string sensor) => deltas.Where(d => d.AssetId == asset && d.SensorTypeCode == sensor).Sum(d => d.Delta);
        var machines = (await production.MachinesAsync(tenantId, ct)).Where(m => m.Category is "Production" or "Robotics" && m.AssetTypeCode != "agv")
            .Select(m =>
            {
                var s = stats.GetValueOrDefault(m.AssetId);
                return new OperatorMachine(m.AssetId, m.Name, m.LineName, m.Status, s?.CountsParts ?? false, Today(m.AssetId, "part_count"), Today(m.AssetId, "scrap_count"),
                    s?.PendingDowntimes ?? 0, s?.WorkOrderId, s?.WorkOrderCode, s?.ProductName);
            })
            .OrderByDescending(m => m.WorkOrderId is not null).ThenBy(m => m.LineName).ThenBy(m => m.Name).ToList();
        var reasons = (await repo.RecentScrapReasonsAsync(tenantId, ct)).Concat(DefaultScrapReasons).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        return new OperatorHome(since, machines, reasons);
    }

    public async Task<long> CountAsync(CountRequest r, CancellationToken ct)
    {
        await gate.EnsureAsync("production", ct);
        var tenantId = me.RequireTenant();
        if (r.Good < 0 || r.Scrap < 0 || r.Good + r.Scrap == 0 || r.Good > 100_000 || r.Scrap > 100_000)
        {
            throw AppException.Validation("Enter how many good and / or scrap parts (up to 100,000 at a time).");
        }
        if ((await production.MachinesAsync(tenantId, ct)).All(m => m.AssetId != r.AssetId))
        {
            throw AppException.NotFound("Machine");
        }
        var reason = Guard.Optional(r.ScrapReason, "Scrap reason", 80);
        if (r.Scrap > 0 && reason is null)
        {
            throw AppException.Validation("Choose why the parts were scrapped.");
        }
        var clean = r with { ScrapReason = r.Scrap > 0 ? reason : null, Note = Guard.Optional(r.Note, "Note", 300) };
        var (goodTag, scrapTag) = await repo.CounterTagsAsync(tenantId, r.AssetId, ct);
        var (id, totalGood, totalScrap) = await repo.AddEntryAsync(tenantId, clean, me.UserId, ct);
        var now = DateTime.UtcNow;
        var rows = new List<ReadingRow>();
        // counters are read as max − min inside a window, so the old value goes in just before the new one
        void Step(Guid tag, double before, int add)
        {
            rows.Add(new ReadingRow(tenantId, tag, now.AddMilliseconds(-5), before, Quality.Good, true));
            rows.Add(new ReadingRow(tenantId, tag, now, before + add, Quality.Good, true));
        }
        if (r.Good > 0)
        {
            Step(goodTag, totalGood - r.Good, r.Good);
        }
        if (r.Scrap > 0)
        {
            Step(scrapTag, totalScrap - r.Scrap, r.Scrap);
        }
        await store.WriteAsync(rows, ct);
        await audit.WriteAsync("operator.count", "asset", r.AssetId.ToString(), $"good {r.Good}, scrap {r.Scrap}", ct);
        return id;
    }

    public async Task<IReadOnlyList<OperatorEntry>> EntriesAsync(Guid? assetId, CancellationToken ct)
    {
        await gate.EnsureAsync("production", ct);
        return await repo.EntriesAsync(me.RequireTenant(), assetId, await StartOfTodayUtcAsync(ct), ct);
    }
}
