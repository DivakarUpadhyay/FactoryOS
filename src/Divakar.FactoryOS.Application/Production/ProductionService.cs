using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Energy;
using Divakar.FactoryOS.Application.Settings;
using Divakar.FactoryOS.Domain.Plant;

namespace Divakar.FactoryOS.Application.Production;

public sealed record Machine(Guid AssetId, string Code, string Name, string AssetTypeCode, string Category, string? AreaName, string? LineName,
    string Status, decimal? IdealCycleSec);

public sealed record StateSeconds(Guid AssetId, int State, double Seconds);

public sealed record PlannedDown(Guid AssetId, double Seconds);

public sealed record Reason(string ReasonCode, string Name, string Category, int SortOrder);

public sealed record Downtime(long DowntimeId, Guid AssetId, string AssetName, DateTime StartTs, DateTime? EndTs, double Seconds, string State,
    string? ReasonCode, string? ReasonName, string? ReasonCategory, string? Comment);

public sealed record ParetoRow(string? ReasonCode, string Reason, string Category, int Count, double Seconds);

public sealed record Product(Guid ProductId, string Code, string Name, decimal? IdealCycleSec, string Unit, bool IsActive);

public sealed record WorkOrder(Guid WorkOrderId, string Code, Guid ProductId, string ProductCode, string ProductName, Guid AssetId, string AssetName,
    int Quantity, string Status, DateOnly? DueDate, string? LotNo, int Produced, int Scrap, DateTime? ActualStart, DateTime? ActualEnd, string? Notes,
    DateTime CreatedAt);

public sealed record SaveProductRequest(string Code, string Name, decimal? IdealCycleSec, string? Unit, bool IsActive = true);

public sealed record SaveWorkOrderRequest(string Code, Guid ProductId, Guid AssetId, int Quantity, DateOnly? DueDate, string? LotNo, string? Notes);

public sealed record ReasonRequest(string ReasonCode, string? Comment);

public interface IProductionRepository
{
    Task<IReadOnlyList<Machine>> MachinesAsync(Guid tenantId, CancellationToken ct);
    Task<IReadOnlyList<StateSeconds>> StateSecondsAsync(Guid tenantId, DateTime from, DateTime to, CancellationToken ct);
    Task<IReadOnlyList<PlannedDown>> PlannedDownAsync(Guid tenantId, DateTime from, DateTime to, CancellationToken ct);
    Task<IReadOnlyList<Reason>> ReasonsAsync(CancellationToken ct);
    Task<IReadOnlyList<Downtime>> DowntimesAsync(Guid tenantId, DateTime from, DateTime to, Guid? assetId, bool onlyOpenOrUnexplained, CancellationToken ct);
    Task<Downtime?> DowntimeAsync(Guid tenantId, long id, CancellationToken ct);
    Task SetReasonAsync(Guid tenantId, long id, string reasonCode, string? comment, Guid userId, CancellationToken ct);
    Task<IReadOnlyList<ParetoRow>> ParetoAsync(Guid tenantId, DateTime from, DateTime to, CancellationToken ct);

    Task<IReadOnlyList<Product>> ProductsAsync(Guid tenantId, CancellationToken ct);
    Task<Guid> SaveProductAsync(Guid tenantId, Guid? id, SaveProductRequest r, CancellationToken ct);
    Task<bool> ProductCodeExistsAsync(Guid tenantId, string code, Guid? exceptId, CancellationToken ct);

    Task<IReadOnlyList<WorkOrder>> WorkOrdersAsync(Guid tenantId, string? status, CancellationToken ct);
    Task<WorkOrder?> WorkOrderAsync(Guid tenantId, Guid id, CancellationToken ct);
    Task<Guid> CreateWorkOrderAsync(Guid tenantId, SaveWorkOrderRequest r, CancellationToken ct);
    Task<bool> WorkOrderCodeExistsAsync(Guid tenantId, string code, CancellationToken ct);
    Task<bool> AssetHasRunningOrderAsync(Guid tenantId, Guid assetId, CancellationToken ct);
    Task StartWorkOrderAsync(Guid tenantId, Guid id, CancellationToken ct);
    Task FinishWorkOrderAsync(Guid tenantId, Guid id, string status, CancellationToken ct);
}

/// <summary>Runs in the worker for every company: opens and closes downtime records from machine states.</summary>
public interface IProductionJobsStore
{
    Task<int> DetectDowntimeAsync(CancellationToken ct);
}

public sealed record Oee(double? Availability, double? Performance, double? Quality, double? Value);

public sealed record MachineOee(Guid AssetId, string Code, string Name, string? AreaName, string? LineName, string Status, string PackMlState,
    double RunSec, double DownSec, double PlannedSec, double Good, double Scrap, decimal? IdealCycleSec, double PartsPerHour, Oee Oee);

public sealed record OeeReport(DateTime From, DateTime To, Oee Plant, double Good, double Scrap, double RunSec, double PlannedSec,
    IReadOnlyList<MachineOee> Machines, IReadOnlyList<HourOutput> HourlyOutput);

public sealed record HourOutput(DateTime Hour, double Parts);

public sealed record ShiftReport(string Shift, DateTime From, DateTime To, Oee Oee, double Good, double Scrap, double DowntimeMinutes,
    IReadOnlyList<ParetoRow> TopReasons);

public sealed class ProductionService(IProductionRepository repo, IEnergyRepository counters, SettingsService settings, ModuleGate gate,
    ICurrentUser me, IAuditLog audit, IPlanningRepository planning)
{
    /// <summary>PackML name for each machine state.</summary>
    public static string PackMl(string status) => status switch
    {
        "Running" => "Execute",
        "Idle" => "Idle",
        "Stopped" => "Stopped",
        "Fault" => "Aborted",
        "Maintenance" => "Maintenance",
        _ => "Unknown",
    };

    /// <summary>
    /// OEE = availability × performance × quality. Planned production time is the observed time minus planned stops
    /// (maintenance state, planned downtime reasons and breaks); offline time is not counted because nothing is known about it.
    /// </summary>
    public static (Oee Oee, double Planned) Calculate(double run, double idle, double stopped, double fault, double plannedDown,
        double good, double scrap, decimal? idealCycleSec)
    {
        var planned = Math.Max(run, run + idle + stopped + fault - plannedDown);
        double? a = planned > 0 ? run / planned : null;
        var total = good + scrap;
        double? p = run > 0 && idealCycleSec is > 0 && total > 0 ? Math.Min(1, (double)idealCycleSec.Value * total / run) : null;
        double? q = total > 0 ? good / total : null;
        double? oee = a is { } x && p is { } y && q is { } z ? x * y * z : null;
        return (new Oee(R(a), R(p), R(q), R(oee)), planned);
    }

    private static double? R(double? v) => v is { } x ? Math.Round(x * 100, 1) : null;

    private static (DateTime From, DateTime To) Window(DateTime? from, DateTime? to, TimeSpan fallback)
    {
        var t = (to ?? DateTime.UtcNow).ToUniversalTime();
        var f = (from ?? t - fallback).ToUniversalTime();
        if (f >= t || t - f > TimeSpan.FromDays(100))
        {
            throw AppException.Validation("Choose a period of up to 100 days with 'from' before 'to'.");
        }
        return (f, t);
    }

    public async Task<OeeReport> OeeAsync(DateTime? from, DateTime? to, CancellationToken ct, double extraPlannedSec = 0)
    {
        await gate.EnsureAsync("production", ct);
        var tenantId = me.RequireTenant();
        var (f, t) = Window(from, to, TimeSpan.FromHours(24));
        var end = t > DateTime.UtcNow ? DateTime.UtcNow : t;
        var machines = (await repo.MachinesAsync(tenantId, ct)).Where(m => m.Category is "Production" or "Robotics" && m.AssetTypeCode != "agv").ToList();
        var states = (await repo.StateSecondsAsync(tenantId, f, end > f ? end : t, ct)).ToLookup(s => s.AssetId);
        var planned = (await repo.PlannedDownAsync(tenantId, f, t, ct)).ToDictionary(p => p.AssetId, p => p.Seconds);
        var deltas = await counters.CounterDeltasAsync(tenantId, ["part_count", "scrap_count"], f, t, ct);
        var good = deltas.Where(d => d.SensorTypeCode == "part_count").GroupBy(d => d.AssetId).ToDictionary(g => g.Key, g => g.Sum(x => x.Delta));
        var scrap = deltas.Where(d => d.SensorTypeCode == "scrap_count").GroupBy(d => d.AssetId).ToDictionary(g => g.Key, g => g.Sum(x => x.Delta));

        var rows = new List<MachineOee>();
        foreach (var m in machines)
        {
            double Sec(int state) => states[m.AssetId].Where(s => s.State == state).Sum(s => s.Seconds);
            var run = Sec(MachineStates.Running);
            var idle = Sec(MachineStates.Idle);
            var stopped = Sec(MachineStates.Stopped);
            var fault = Sec(MachineStates.Fault);
            var g = good.GetValueOrDefault(m.AssetId);
            var s = scrap.GetValueOrDefault(m.AssetId);
            var (oee, plannedSec) = Calculate(run, idle, stopped, fault, planned.GetValueOrDefault(m.AssetId) + extraPlannedSec, g, s, m.IdealCycleSec);
            rows.Add(new MachineOee(m.AssetId, m.Code, m.Name, m.AreaName, m.LineName, m.Status, PackMl(m.Status), Math.Round(run), Math.Round(idle + stopped + fault),
                Math.Round(plannedSec), g, s, m.IdealCycleSec, run > 0 ? Math.Round(g / (run / 3600), 1) : 0, oee));
        }

        // plant figures weight each machine by its time and output
        var totRun = rows.Sum(r => r.RunSec);
        var totPlanned = rows.Sum(r => r.PlannedSec);
        var withIdeal = rows.Where(r => r.IdealCycleSec is > 0 && r.RunSec > 0).ToList();
        var idealWork = withIdeal.Sum(r => (double)r.IdealCycleSec!.Value * (r.Good + r.Scrap));
        double? pa = totPlanned > 0 ? totRun / totPlanned : null;
        double? pp = withIdeal.Sum(r => r.RunSec) > 0 ? Math.Min(1, idealWork / withIdeal.Sum(r => r.RunSec)) : null;
        var totGood = rows.Sum(r => r.Good);
        var totScrap = rows.Sum(r => r.Scrap);
        double? pq = totGood + totScrap > 0 ? totGood / (totGood + totScrap) : null;
        var plant = new Oee(R(pa), R(pp), R(pq), pa is { } a && pp is { } p && pq is { } q ? R(a * p * q) : null);

        var s0 = await settings.GetAsync(ct);
        var offset = (int)s0.Zone().GetUtcOffset(t).TotalMinutes;
        var partTags = deltas.Where(d => d.SensorTypeCode == "part_count" && machines.Any(m => m.AssetId == d.AssetId)).Select(d => d.TagId).ToList();
        var hourly = EnergyService.Consumption(await counters.CounterBucketsAsync(tenantId, partTags, f, t, 60, offset, ct));

        return new OeeReport(f, t, plant, totGood, totScrap, totRun, totPlanned, [.. rows.OrderBy(r => r.Oee.Value ?? 999)],
            [.. hourly.Select(h => new HourOutput(h.Bucket, Math.Round(h.Value)))]);
    }

    public async Task<IReadOnlyList<ShiftReport>> ShiftsAsync(DateOnly? date, CancellationToken ct)
    {
        await gate.EnsureAsync("production", ct);
        var s = await settings.GetAsync(ct);
        var zone = s.Zone();
        var day = date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
        var reports = new List<ShiftReport>();
        foreach (var shift in s.Shifts)
        {
            var start = day.ToDateTime(TimeOnly.Parse(shift.Start));
            var endLocal = day.ToDateTime(TimeOnly.Parse(shift.End));
            if (endLocal <= start)
            {
                endLocal = endLocal.AddDays(1);
            }
            var f = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(start, DateTimeKind.Unspecified), zone);
            var t = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(endLocal, DateTimeKind.Unspecified), zone);
            if (f >= DateTime.UtcNow)
            {
                reports.Add(new ShiftReport(shift.Name, start, endLocal, new Oee(null, null, null, null), 0, 0, 0, []));
                continue;
            }
            var oee = await OeeAsync(f, t, ct, shift.BreakMinutes * 60);
            var pareto = await repo.ParetoAsync(me.RequireTenant(), f, t, ct);
            reports.Add(new ShiftReport(shift.Name, start, endLocal, oee.Plant, oee.Good, oee.Scrap, Math.Round(pareto.Sum(x => x.Seconds) / 60, 1),
                [.. pareto.Take(3)]));
        }
        return reports;
    }

    public Task<IReadOnlyList<Reason>> ReasonsAsync(CancellationToken ct) => repo.ReasonsAsync(ct);

    public async Task<IReadOnlyList<Downtime>> DowntimesAsync(DateTime? from, DateTime? to, Guid? assetId, bool pending, CancellationToken ct)
    {
        await gate.EnsureAsync("production", ct);
        var (f, t) = Window(from, to, TimeSpan.FromHours(24));
        return await repo.DowntimesAsync(me.RequireTenant(), f, t, assetId, pending, ct);
    }

    public async Task SetReasonAsync(long id, ReasonRequest r, CancellationToken ct)
    {
        await gate.EnsureAsync("production", ct);
        var tenantId = me.RequireTenant();
        _ = await repo.DowntimeAsync(tenantId, id, ct) ?? throw AppException.NotFound("Downtime");
        if ((await repo.ReasonsAsync(ct)).All(x => x.ReasonCode != r.ReasonCode))
        {
            throw AppException.Validation("Unknown downtime reason.");
        }
        if (r.ReasonCode == "other" && string.IsNullOrWhiteSpace(r.Comment))
        {
            throw AppException.Validation("Add a comment when the reason is 'Other'.");
        }
        await repo.SetReasonAsync(tenantId, id, r.ReasonCode, Guard.Optional(r.Comment, "Comment", 300), me.UserId, ct);
        await audit.WriteAsync("downtime.reason", "downtime", id.ToString(), new { r.ReasonCode }, ct);
    }

    public async Task<IReadOnlyList<ParetoRow>> ParetoAsync(DateTime? from, DateTime? to, CancellationToken ct)
    {
        await gate.EnsureAsync("production", ct);
        var (f, t) = Window(from, to, TimeSpan.FromDays(7));
        return await repo.ParetoAsync(me.RequireTenant(), f, t, ct);
    }

    public async Task<IReadOnlyList<Product>> ProductsAsync(CancellationToken ct)
    {
        await gate.EnsureAsync("production", ct);
        return await repo.ProductsAsync(me.RequireTenant(), ct);
    }

    public async Task<Guid> SaveProductAsync(Guid? id, SaveProductRequest r, CancellationToken ct)
    {
        await gate.EnsureAsync("production", ct);
        var tenantId = me.RequireTenant();
        var clean = r with
        {
            Code = Guard.Code(r.Code, "Code"),
            Name = Guard.Required(r.Name, "Name", 150),
            Unit = Guard.Optional(r.Unit, "Unit", 20) ?? "pcs",
        };
        if (r.IdealCycleSec is <= 0)
        {
            throw AppException.Validation("The ideal cycle time must be more than zero.");
        }
        if (await repo.ProductCodeExistsAsync(tenantId, clean.Code, id, ct))
        {
            throw AppException.Conflict($"A product with code '{clean.Code}' already exists.");
        }
        if (id is { } existing && (await repo.ProductsAsync(tenantId, ct)).All(p => p.ProductId != existing))
        {
            throw AppException.NotFound("Product");
        }
        var saved = await repo.SaveProductAsync(tenantId, id, clean, ct);
        await audit.WriteAsync(id is null ? "product.create" : "product.update", "product", saved.ToString(), null, ct);
        return saved;
    }

    public async Task<IReadOnlyList<WorkOrder>> WorkOrdersAsync(string? status, CancellationToken ct)
    {
        await gate.EnsureAsync("production", ct);
        return await repo.WorkOrdersAsync(me.RequireTenant(), status, ct);
    }

    public async Task<Guid> CreateWorkOrderAsync(SaveWorkOrderRequest r, CancellationToken ct)
    {
        await gate.EnsureAsync("production", ct);
        var tenantId = me.RequireTenant();
        var code = Guard.Code(r.Code, "Code");
        if (r.Quantity <= 0)
        {
            throw AppException.Validation("The quantity must be more than zero.");
        }
        if ((await repo.ProductsAsync(tenantId, ct)).All(p => p.ProductId != r.ProductId))
        {
            throw AppException.NotFound("Product");
        }
        if ((await repo.MachinesAsync(tenantId, ct)).All(m => m.AssetId != r.AssetId))
        {
            throw AppException.NotFound("Machine");
        }
        if (await repo.WorkOrderCodeExistsAsync(tenantId, code, ct))
        {
            throw AppException.Conflict($"Work order '{code}' already exists.");
        }
        var id = await repo.CreateWorkOrderAsync(tenantId, r with { Code = code, LotNo = Guard.Optional(r.LotNo, "Lot", 60), Notes = Guard.Optional(r.Notes, "Notes", 500) }, ct);
        await planning.CopyRoutingAsync(tenantId, id, r.ProductId, ct);   // the order keeps its own copy of the operations
        await audit.WriteAsync("workorder.create", "workorder", id.ToString(), new { code }, ct);
        return id;
    }

    /// <summary>start: Planned → Running (one per machine); complete or cancel: Running/Planned → done, freezing the counts.</summary>
    public async Task ChangeWorkOrderAsync(Guid id, string action, CancellationToken ct)
    {
        await gate.EnsureAsync("production", ct);
        var tenantId = me.RequireTenant();
        var wo = await repo.WorkOrderAsync(tenantId, id, ct) ?? throw AppException.NotFound("Work order");
        switch (action)
        {
            case "start" when wo.Status == "Planned":
                if (await repo.AssetHasRunningOrderAsync(tenantId, wo.AssetId, ct))
                {
                    throw AppException.Conflict($"{wo.AssetName} is already running another work order. Complete it first.");
                }
                await repo.StartWorkOrderAsync(tenantId, id, ct);
                break;
            case "complete" when wo.Status == "Running":
                await repo.FinishWorkOrderAsync(tenantId, id, "Completed", ct);
                break;
            case "cancel" when wo.Status is "Planned" or "Running":
                await repo.FinishWorkOrderAsync(tenantId, id, "Cancelled", ct);
                break;
            default:
                throw AppException.Validation($"A {wo.Status.ToLowerInvariant()} work order cannot be {action}ed.");
        }
        await audit.WriteAsync($"workorder.{action}", "workorder", id.ToString(), null, ct);
    }
}
