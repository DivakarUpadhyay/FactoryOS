using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Alarms;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Production;
using Divakar.FactoryOS.Application.Settings;
using Divakar.FactoryOS.Domain.Plant;

namespace Divakar.FactoryOS.Application.Maintenance;

public sealed record ChecklistItem(string Text, bool Done = false);

public sealed record MaintPlan(Guid PlanId, Guid AssetId, string AssetName, string Name, string TriggerType, int Interval,
    IReadOnlyList<string> Checklist, int? EstimatedMinutes, DateTime LastDoneAt, double? LastDoneCount, bool IsActive,
    double Used, double UsedPct, bool HasOpenOrder);

public sealed record SavePlanRequest(Guid AssetId, string Name, string TriggerType, int Interval, IReadOnlyList<string>? Checklist,
    int? EstimatedMinutes, bool IsActive = true);

public sealed record MaintWorkOrder(Guid MaintWoId, string Code, Guid AssetId, string AssetName, Guid? PlanId, long? AlarmId, string Type,
    byte Priority, string Title, string? Description, string Status, Guid? AssignedTo, string? AssignedName, IReadOnlyList<ChecklistItem> Checklist,
    DateTime? DueAt, DateTime? StartedAt, DateTime? CompletedAt, string? Notes, DateTime CreatedAt, bool Overdue);

public sealed record CreateMaintWoRequest(Guid AssetId, string Type, byte Priority, string Title, string? Description, DateTime? DueAt,
    Guid? AssignedTo, IReadOnlyList<string>? Checklist);

public sealed record ChecklistRequest(IReadOnlyList<ChecklistItem> Items, string? Notes);

public sealed record AssignRequest(Guid? UserId);

public sealed record AssetReliability(Guid AssetId, string AssetName, int Failures, double DowntimeMinutes, double? MttrMinutes, double? MtbfHours,
    double RunHours, int OpenOrders);

public sealed record MaintenanceKpis(int Open, int InProgress, int Overdue, int DoneLast30, double? PmCompliancePct, double? MttrMinutes,
    double? MtbfHours, IReadOnlyList<AssetReliability> Assets);

/// <summary>A plan as the scheduler sees it, with what has been used since the last service.</summary>
public sealed record PlanUsage(Guid TenantId, Guid PlanId, Guid AssetId, string AssetName, string Name, string TriggerType, int Interval,
    string? Checklist, DateTime LastDoneAt, double? LastDoneCount, double? CurrentCount, double RunSeconds, bool HasOpenOrder);

public sealed record FailureStats(Guid AssetId, int Failures, double DowntimeSeconds);

public interface IMaintenanceRepository
{
    Task<IReadOnlyList<PlanUsage>> PlanUsageAsync(Guid? tenantId, CancellationToken ct);
    Task<IReadOnlyList<(Guid PlanId, bool IsActive, int? EstimatedMinutes, string AssetName)>> PlanInfoAsync(Guid tenantId, CancellationToken ct);
    Task<Guid> SavePlanAsync(Guid tenantId, Guid? id, SavePlanRequest r, string? checklistJson, CancellationToken ct);
    Task<IReadOnlyList<MaintWorkOrder>> WorkOrdersAsync(Guid tenantId, string? status, Guid? assetId, CancellationToken ct);
    Task<MaintWorkOrder?> WorkOrderAsync(Guid tenantId, Guid id, CancellationToken ct);
    Task<Guid> CreateWorkOrderAsync(Guid tenantId, string code, Guid assetId, Guid? planId, long? alarmId, string type, int priority, string title,
        string? description, DateTime? dueAt, Guid? assignedTo, string? checklistJson, Guid? createdBy, CancellationToken ct);
    Task SetStatusAsync(Guid tenantId, Guid id, string status, Guid? assignee, CancellationToken ct);
    Task SaveChecklistAsync(Guid tenantId, Guid id, string checklistJson, string? notes, CancellationToken ct);
    Task AssignAsync(Guid tenantId, Guid id, Guid? userId, CancellationToken ct);
    Task MarkPlanDoneAsync(Guid tenantId, Guid planId, CancellationToken ct);
    Task<bool> UserInTenantAsync(Guid tenantId, Guid userId, CancellationToken ct);
    Task<bool> AssetExistsAsync(Guid tenantId, Guid assetId, CancellationToken ct);
    Task<bool> HasOpenOrderAsync(Guid tenantId, Guid assetId, string type, CancellationToken ct);
    Task<IReadOnlyList<FailureStats>> FailuresAsync(Guid tenantId, DateTime from, DateTime to, CancellationToken ct);
    Task<(int Due, int OnTime)> PmComplianceAsync(Guid tenantId, DateTime from, CancellationToken ct);
}

public static class MaintenanceRules
{
    /// <summary>How much of the interval has been used: days, running hours or cycles since the last service.</summary>
    public static double Used(PlanUsage p, DateTime now) => p.TriggerType switch
    {
        "Calendar" => (now - p.LastDoneAt).TotalDays,
        "RunHours" => p.RunSeconds / 3600,
        "Cycles" => p.CurrentCount is { } c && p.LastDoneCount is { } l ? Math.Max(0, c - l) : 0,
        _ => 0,
    };

    public static bool IsDue(PlanUsage p, DateTime now) => !p.HasOpenOrder && Used(p, now) >= p.Interval;

    public static string Unit(string trigger) => trigger switch { "Calendar" => "days", "RunHours" => "running hours", _ => "cycles" };
}

public sealed class MaintenanceService(IMaintenanceRepository repo, IProductionRepository production, IFixKnowledgeRepository knowledge, ModuleGate gate,
    ICurrentUser me, IAuditLog audit)
{
    private static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static string NewCode(string prefix) => $"{prefix}-{DateTime.UtcNow:yyMMdd-HHmmss}-{Random.Shared.Next(100, 999)}";

    public async Task<IReadOnlyList<MaintPlan>> PlansAsync(CancellationToken ct)
    {
        await gate.EnsureAsync("maintenance", ct);
        var tenantId = me.RequireTenant();
        var info = (await repo.PlanInfoAsync(tenantId, ct)).ToDictionary(x => x.PlanId);
        var now = DateTime.UtcNow;
        return [.. (await repo.PlanUsageAsync(tenantId, ct)).Select(p =>
        {
            var used = MaintenanceRules.Used(p, now);
            var i = info.GetValueOrDefault(p.PlanId);
            return new MaintPlan(p.PlanId, p.AssetId, p.AssetName, p.Name, p.TriggerType, p.Interval, ParseList(p.Checklist), i.EstimatedMinutes,
                p.LastDoneAt, p.LastDoneCount, i.IsActive, Math.Round(used, 1), Math.Round(used / p.Interval * 100, 1), p.HasOpenOrder);
        }).OrderByDescending(p => p.UsedPct)];
    }

    public static IReadOnlyList<string> ParseList(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : System.Text.Json.JsonSerializer.Deserialize<List<string>>(json, Web) ?? [];

    public async Task<Guid> SavePlanAsync(Guid? id, SavePlanRequest r, CancellationToken ct)
    {
        await gate.EnsureAsync("maintenance", ct);
        var tenantId = me.RequireTenant();
        if (r.TriggerType is not ("Calendar" or "RunHours" or "Cycles"))
        {
            throw AppException.Validation("The trigger is Calendar (days), RunHours or Cycles.");
        }
        if (r.Interval is <= 0 or > 10_000_000)
        {
            throw AppException.Validation("The interval must be a positive number.");
        }
        if (!await repo.AssetExistsAsync(tenantId, r.AssetId, ct))
        {
            throw AppException.NotFound("Asset");
        }
        if (id is { } existing && (await repo.PlanInfoAsync(tenantId, ct)).All(p => p.PlanId != existing))
        {
            throw AppException.NotFound("Plan");
        }
        var items = (r.Checklist ?? []).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (items.Any(x => x.Length > 200) || items.Count > 50)
        {
            throw AppException.Validation("A checklist has at most 50 items of 200 characters.");
        }
        var saved = await repo.SavePlanAsync(tenantId, id, r with { Name = Guard.Required(r.Name, "Name", 150) },
            System.Text.Json.JsonSerializer.Serialize(items, Web), ct);
        await audit.WriteAsync(id is null ? "pmplan.create" : "pmplan.update", "pmplan", saved.ToString(), null, ct);
        return saved;
    }

    public async Task<IReadOnlyList<MaintWorkOrder>> WorkOrdersAsync(string? status, Guid? assetId, CancellationToken ct)
    {
        await gate.EnsureAsync("maintenance", ct);
        return await repo.WorkOrdersAsync(me.RequireTenant(), status, assetId, ct);
    }

    public async Task<Guid> CreateWorkOrderAsync(CreateMaintWoRequest r, CancellationToken ct)
    {
        await gate.EnsureAsync("maintenance", ct);
        var tenantId = me.RequireTenant();
        if (r.Type is not ("Preventive" or "Corrective" or "Predictive"))
        {
            throw AppException.Validation("Type is Preventive, Corrective or Predictive.");
        }
        if (r.Priority is < 1 or > 3)
        {
            throw AppException.Validation("Priority is 1 (urgent), 2 (normal) or 3 (low).");
        }
        if (!await repo.AssetExistsAsync(tenantId, r.AssetId, ct))
        {
            throw AppException.NotFound("Asset");
        }
        if (r.AssignedTo is { } u && !await repo.UserInTenantAsync(tenantId, u, ct))
        {
            throw AppException.NotFound("User");
        }
        var checklist = (r.Checklist ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => new ChecklistItem(x.Trim())).ToList();
        var id = await repo.CreateWorkOrderAsync(tenantId, NewCode("mwo"), r.AssetId, null, null, r.Type, r.Priority, Guard.Required(r.Title, "Title", 200),
            Guard.Optional(r.Description, "Description", 1000), r.DueAt?.ToUniversalTime(), r.AssignedTo,
            System.Text.Json.JsonSerializer.Serialize(checklist, Web), me.UserId, ct);
        await audit.WriteAsync("maintwo.create", "maintwo", id.ToString(), null, ct);
        return id;
    }

    /// <summary>start: Open → InProgress (assigns the caller if nobody is assigned); complete: → Done (resets the plan); cancel.</summary>
    public async Task ChangeAsync(Guid id, string action, CancellationToken ct)
    {
        await gate.EnsureAsync("maintenance", ct);
        var tenantId = me.RequireTenant();
        var wo = await repo.WorkOrderAsync(tenantId, id, ct) ?? throw AppException.NotFound("Work order");
        switch (action)
        {
            case "start" when wo.Status == "Open":
                await repo.SetStatusAsync(tenantId, id, "InProgress", wo.AssignedTo ?? me.UserId, ct);
                break;
            case "complete" when wo.Status is "Open" or "InProgress":
                if (wo.Checklist.Any(c => !c.Done))
                {
                    throw AppException.Validation($"Tick every checklist item first ({wo.Checklist.Count(c => !c.Done)} open).");
                }
                if (FixKnowledgeService.NeedsResolution(wo.Type) && await knowledge.ResolutionAsync(tenantId, id, ct) is null)
                {
                    throw AppException.Validation("Record the cause and the fix first, so the next technician can find them.");
                }
                await repo.SetStatusAsync(tenantId, id, "Done", wo.AssignedTo ?? me.UserId, ct);
                if (wo.PlanId is { } planId)
                {
                    await repo.MarkPlanDoneAsync(tenantId, planId, ct);
                }
                break;
            case "cancel" when wo.Status is "Open" or "InProgress":
                await repo.SetStatusAsync(tenantId, id, "Cancelled", wo.AssignedTo, ct);
                break;
            default:
                throw AppException.Validation($"A work order that is {wo.Status} cannot be {action}ed.");
        }
        await audit.WriteAsync($"maintwo.{action}", "maintwo", id.ToString(), null, ct);
    }

    public async Task SaveChecklistAsync(Guid id, ChecklistRequest r, CancellationToken ct)
    {
        await gate.EnsureAsync("maintenance", ct);
        var tenantId = me.RequireTenant();
        var wo = await repo.WorkOrderAsync(tenantId, id, ct) ?? throw AppException.NotFound("Work order");
        if (wo.Status is "Done" or "Cancelled")
        {
            throw AppException.Validation("A finished work order cannot be changed.");
        }
        if (r.Items.Count > 50 || r.Items.Any(i => string.IsNullOrWhiteSpace(i.Text) || i.Text.Length > 200))
        {
            throw AppException.Validation("A checklist has at most 50 items of 1–200 characters.");
        }
        await repo.SaveChecklistAsync(tenantId, id, System.Text.Json.JsonSerializer.Serialize(r.Items, Web), Guard.Optional(r.Notes, "Notes", 1000), ct);
    }

    public async Task AssignAsync(Guid id, AssignRequest r, CancellationToken ct)
    {
        await gate.EnsureAsync("maintenance", ct);
        var tenantId = me.RequireTenant();
        _ = await repo.WorkOrderAsync(tenantId, id, ct) ?? throw AppException.NotFound("Work order");
        if (r.UserId is { } u && !await repo.UserInTenantAsync(tenantId, u, ct))
        {
            throw AppException.NotFound("User");
        }
        await repo.AssignAsync(tenantId, id, r.UserId, ct);
    }

    /// <summary>Reliability over 30 days: failures are downtime in the Fault state; MTTR = mean fault duration, MTBF = running hours ÷ failures.</summary>
    public async Task<MaintenanceKpis> KpisAsync(CancellationToken ct)
    {
        await gate.EnsureAsync("maintenance", ct);
        var tenantId = me.RequireTenant();
        var now = DateTime.UtcNow;
        var from = now.AddDays(-30);
        var failures = (await repo.FailuresAsync(tenantId, from, now, ct)).ToDictionary(f => f.AssetId);
        var run = (await production.StateSecondsAsync(tenantId, from, now, ct)).Where(s => s.State == MachineStates.Running)
            .GroupBy(s => s.AssetId).ToDictionary(g => g.Key, g => g.Sum(x => x.Seconds));
        var machines = await production.MachinesAsync(tenantId, ct);
        var orders = await repo.WorkOrdersAsync(tenantId, null, null, ct);
        var assets = machines.Where(m => run.ContainsKey(m.AssetId) || failures.ContainsKey(m.AssetId)).Select(m =>
        {
            var f = failures.GetValueOrDefault(m.AssetId);
            var hours = run.GetValueOrDefault(m.AssetId) / 3600;
            var n = f?.Failures ?? 0;
            return new AssetReliability(m.AssetId, m.Name, n, Math.Round((f?.DowntimeSeconds ?? 0) / 60, 1),
                n > 0 ? Math.Round(f!.DowntimeSeconds / 60 / n, 1) : null, n > 0 ? Math.Round(hours / n, 1) : null, Math.Round(hours, 1),
                orders.Count(o => o.AssetId == m.AssetId && o.Status is "Open" or "InProgress"));
        }).OrderByDescending(a => a.Failures).ThenBy(a => a.MtbfHours ?? double.MaxValue).ToList();

        var totalFailures = assets.Sum(a => a.Failures);
        var (due, onTime) = await repo.PmComplianceAsync(tenantId, from, ct);
        return new MaintenanceKpis(orders.Count(o => o.Status == "Open"), orders.Count(o => o.Status == "InProgress"), orders.Count(o => o.Overdue),
            orders.Count(o => o.Status == "Done" && o.CompletedAt > from), due > 0 ? Math.Round(onTime * 100.0 / due, 1) : null,
            totalFailures > 0 ? Math.Round(assets.Sum(a => a.DowntimeMinutes) / totalFailures, 1) : null,
            totalFailures > 0 ? Math.Round(assets.Sum(a => a.RunHours) / totalFailures, 1) : null, assets);
    }
}

/// <summary>Worker job: opens a preventive work order when a plan's interval is used up (one open order per plan).</summary>
public sealed class PmScheduler(IMaintenanceRepository repo) : IAlarmWorkOrders
{
    private static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);

    public async Task<int> RunOnceAsync(DateTime now, CancellationToken ct)
    {
        var created = 0;
        foreach (var p in (await repo.PlanUsageAsync(null, ct)).Where(p => MaintenanceRules.IsDue(p, now)))
        {
            var items = MaintenanceService.ParseList(p.Checklist).Select(x => new ChecklistItem(x)).ToList();
            var used = MaintenanceRules.Used(p, now);
            await repo.CreateWorkOrderAsync(p.TenantId, MaintenanceService.NewCode("pm"), p.AssetId, p.PlanId, null, "Preventive", 2, $"PM: {p.Name}",
                $"Due after {p.Interval} {MaintenanceRules.Unit(p.TriggerType)}; {used:0.#} used since the last service.", now, null,
                System.Text.Json.JsonSerializer.Serialize(items, Web), null, ct);
            created++;
        }
        return created;
    }

    /// <summary>Alarms and anomalies open a work order unless one of the same type is already open for the asset.</summary>
    public async Task CreateForAlarmAsync(Guid tenantId, Guid assetId, long alarmId, string type, string title, int priority, CancellationToken ct)
    {
        if (await repo.HasOpenOrderAsync(tenantId, assetId, type, ct))
        {
            return;
        }
        await repo.CreateWorkOrderAsync(tenantId, MaintenanceService.NewCode(type == "Predictive" ? "pdm" : "cm"), assetId, null, alarmId, type, priority,
            title, $"Opened automatically from alarm #{alarmId}.", DateTime.UtcNow.AddHours(priority == 1 ? 4 : 24), null, "[]", null, ct);
    }
}
