using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Settings;

namespace Divakar.FactoryOS.Application.QualityControl;

public sealed record InspectionItem(int Seq, string Text, Guid? CharId, string? CharName, string? Unit, double? Lsl, double? Usl);

public sealed record InspectionPlan(Guid PlanId, Guid ProductId, string ProductName, string Name, string Trigger, int? EveryN, bool CreateNcrOnFail,
    bool IsActive, IReadOnlyList<InspectionItem> Items);

public sealed record InspectionItemInput(string Text, Guid? CharId);

public sealed record SaveInspectionPlanRequest(Guid ProductId, string Name, string Trigger, int? EveryN, bool CreateNcrOnFail, bool IsActive,
    IReadOnlyList<InspectionItemInput> Items);

public sealed record DueInspection(Guid PlanId, string PlanName, string Trigger, Guid WorkOrderId, string WorkOrderCode, string ProductName, string? LotNo,
    string Reason);

public sealed record ResultInput(int Seq, double? Value, bool? Passed);

public sealed record PerformInspectionRequest(Guid PlanId, Guid? WorkOrderId, string? LotNo, string? Note, IReadOnlyList<ResultInput> Results);

public sealed record InspectionRecord(Guid InspectionId, Guid PlanId, string PlanName, string? WorkOrderCode, string? LotNo, string Result, Guid? NcId,
    string? InspectorName, DateTime At, string? Note);

public sealed record PerformResult(Guid InspectionId, string Result, Guid? NcId, IReadOnlyList<string> Failed);

/// <summary>A work order as the due-list sees it.</summary>
public sealed record OrderForInspection(Guid WorkOrderId, string Code, Guid ProductId, string ProductName, string Status, int Produced, string? LotNo,
    DateTime? ActualEnd);

public interface IInspectionRepository
{
    Task<IReadOnlyList<InspectionPlan>> PlansAsync(Guid tenantId, CancellationToken ct);
    Task<Guid> SavePlanAsync(Guid tenantId, Guid? id, SaveInspectionPlanRequest r, CancellationToken ct);
    Task<IReadOnlyDictionary<(Guid PlanId, Guid WorkOrderId), int>> CountsAsync(Guid tenantId, CancellationToken ct);
    Task<Guid> SaveInspectionAsync(Guid tenantId, PerformInspectionRequest r, string result, IReadOnlyList<(int Seq, double? Value, bool Passed)> rows,
        Guid userId, CancellationToken ct);
    Task LinkNcrAsync(Guid tenantId, Guid inspectionId, Guid ncId, CancellationToken ct);
    Task<IReadOnlyList<InspectionRecord>> HistoryAsync(Guid tenantId, int take, CancellationToken ct);
}

public sealed class InspectionService(IInspectionRepository repo, IQualityRepository quality, Production.IProductionRepository production, ModuleGate gate,
    ICurrentUser me, IAuditLog audit)
{
    public static readonly string[] Triggers = ["FirstPiece", "EveryN", "Final"];

    private async Task<Guid> Tenant(CancellationToken ct)
    {
        await gate.EnsureAsync("quality", ct);
        return me.RequireTenant();
    }

    public async Task<IReadOnlyList<InspectionPlan>> PlansAsync(CancellationToken ct) => await repo.PlansAsync(await Tenant(ct), ct);

    public async Task<Guid> SavePlanAsync(Guid? id, SaveInspectionPlanRequest r, CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        if (!Triggers.Contains(r.Trigger))
        {
            throw AppException.Validation("When: FirstPiece, EveryN or Final.");
        }
        if (r.Trigger == "EveryN" && r.EveryN is not > 0)
        {
            throw AppException.Validation("Give N, the number of parts between inspections.");
        }
        if (r.Items.Count is 0 or > 50 || r.Items.Any(i => string.IsNullOrWhiteSpace(i.Text) || i.Text.Length > 200))
        {
            throw AppException.Validation("A plan has 1 to 50 checks, each with a description of up to 200 characters.");
        }
        if ((await production.ProductsAsync(tenantId, ct)).All(p => p.ProductId != r.ProductId))
        {
            throw AppException.NotFound("Product");
        }
        var chars = (await quality.CharacteristicsAsync(tenantId, ct)).Select(c => c.CharId).ToHashSet();
        if (r.Items.Any(i => i.CharId is { } c && !chars.Contains(c)))
        {
            throw AppException.NotFound("Characteristic");
        }
        if (id is { } existing && (await repo.PlansAsync(tenantId, ct)).All(p => p.PlanId != existing))
        {
            throw AppException.NotFound("Inspection plan");
        }
        var saved = await repo.SavePlanAsync(tenantId, id, r with
        {
            Name = Guard.Required(r.Name, "Name", 150), EveryN = r.Trigger == "EveryN" ? r.EveryN : null,
            Items = [.. r.Items.Select(i => i with { Text = i.Text.Trim() })],
        }, ct);
        await audit.WriteAsync(id is null ? "inspplan.create" : "inspplan.update", "inspplan", saved.ToString(), null, ct);
        return saved;
    }

    /// <summary>
    /// What needs inspecting now: first piece once an order runs; every N parts as the counter passes each multiple;
    /// final once an order is completed (within 7 days).
    /// </summary>
    public static IEnumerable<DueInspection> Due(IEnumerable<InspectionPlan> plans, IEnumerable<OrderForInspection> orders,
        IReadOnlyDictionary<(Guid, Guid), int> done, DateTime now)
    {
        foreach (var o in orders)
        {
            foreach (var p in plans.Where(p => p.IsActive && p.ProductId == o.ProductId))
            {
                var count = done.GetValueOrDefault((p.PlanId, o.WorkOrderId));
                var reason = p.Trigger switch
                {
                    "FirstPiece" when o.Status == "Running" && count == 0 => "First piece of the order",
                    "EveryN" when o.Status == "Running" && p.EveryN is > 0 && o.Produced / p.EveryN.Value > count =>
                        $"{o.Produced} made — check every {p.EveryN} parts ({count} done)",
                    "Final" when o.Status == "Completed" && count == 0 && o.ActualEnd > now.AddDays(-7) => "Order completed — final inspection",
                    _ => null,
                };
                if (reason is not null)
                {
                    yield return new DueInspection(p.PlanId, p.Name, p.Trigger, o.WorkOrderId, o.Code, o.ProductName, o.LotNo, reason);
                }
            }
        }
    }

    public async Task<IReadOnlyList<DueInspection>> DueAsync(CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        var orders = (await production.WorkOrdersAsync(tenantId, null, ct))
            .Select(w => new OrderForInspection(w.WorkOrderId, w.Code, w.ProductId, w.ProductName, w.Status, w.Produced, w.LotNo, w.ActualEnd));
        return [.. Due(await repo.PlansAsync(tenantId, ct), orders, await repo.CountsAsync(tenantId, ct), DateTime.UtcNow)];
    }

    /// <summary>
    /// Records an inspection. Measured checks pass when inside the characteristic's specification limits and also feed its SPC
    /// chart (single-value characteristics); a failure opens a non-conformance when the plan says so.
    /// </summary>
    public async Task<PerformResult> PerformAsync(PerformInspectionRequest r, CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        var plan = (await repo.PlansAsync(tenantId, ct)).FirstOrDefault(p => p.PlanId == r.PlanId) ?? throw AppException.NotFound("Inspection plan");
        if (r.WorkOrderId is { } woId && await production.WorkOrderAsync(tenantId, woId, ct) is null)
        {
            throw AppException.NotFound("Work order");
        }
        var chars = (await quality.CharacteristicsAsync(tenantId, ct)).ToDictionary(c => c.CharId);
        var rows = new List<(int Seq, double? Value, bool Passed)>();
        var failed = new List<string>();
        foreach (var item in plan.Items)
        {
            var given = r.Results.FirstOrDefault(x => x.Seq == item.Seq) ?? throw AppException.Validation($"Missing result for '{item.Text}'.");
            bool passed;
            if (item.CharId is { } cid)
            {
                if (given.Value is not { } v || double.IsNaN(v) || double.IsInfinity(v))
                {
                    throw AppException.Validation($"Enter the measured value for '{item.Text}'.");
                }
                passed = v >= (item.Lsl ?? double.MinValue) && v <= (item.Usl ?? double.MaxValue);
                if (chars.TryGetValue(cid, out var ch) && ch.SubgroupSize == 1 && ch.TagId is null)
                {
                    await quality.AddMeasurementsAsync(tenantId, cid, [v], Guard.Optional(r.LotNo, "Lot", 60), r.WorkOrderId, $"Inspection: {plan.Name}", me.UserId, ct);
                }
            }
            else
            {
                passed = given.Passed ?? throw AppException.Validation($"Mark '{item.Text}' as pass or fail.");
            }
            rows.Add((item.Seq, given.Value, passed));
            if (!passed)
            {
                failed.Add(item.Text);
            }
        }
        var result = failed.Count == 0 ? "Pass" : "Fail";
        var id = await repo.SaveInspectionAsync(tenantId, r with { LotNo = Guard.Optional(r.LotNo, "Lot", 60), Note = Guard.Optional(r.Note, "Note", 300) }, result, rows,
            me.UserId, ct);
        Guid? ncId = null;
        if (result == "Fail" && plan.CreateNcrOnFail)
        {
            var wo = r.WorkOrderId is { } w ? await production.WorkOrderAsync(tenantId, w, ct) : null;
            ncId = await quality.CreateNcrAsync(tenantId, $"ncr-{DateTime.UtcNow:yyMMdd-HHmmss}-{Random.Shared.Next(100, 999)}",
                new SaveNcrRequest($"Inspection failed: {plan.Name}{(wo is null ? "" : $" — {wo.Code}")}", $"Failed checks: {string.Join("; ", failed)}", "Major",
                    plan.ProductId, wo?.AssetId, r.LotNo ?? wo?.LotNo, null, null, null, null, null, null), me.UserId, ct);
            await repo.LinkNcrAsync(tenantId, id, ncId.Value, ct);
        }
        await audit.WriteAsync("inspection.record", "inspection", id.ToString(), new { plan = plan.Name, result }, ct);
        return new PerformResult(id, result, ncId, failed);
    }

    public async Task<IReadOnlyList<InspectionRecord>> HistoryAsync(CancellationToken ct) => await repo.HistoryAsync(await Tenant(ct), 200, ct);
}
