using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Settings;

namespace Divakar.FactoryOS.Application.Production;

public sealed record BomLine(Guid ItemId, string ItemCode, string ItemName, string Unit, decimal QtyPer, decimal ScrapPct, decimal OnHand);

public sealed record BomLineInput(Guid ItemId, decimal QtyPer, decimal ScrapPct = 0);

public sealed record RoutingStep(int Seq, string Name, Guid? AssetId, string? AssetName, decimal? StdCycleSec, int? SetupMinutes);

public sealed record RoutingStepInput(string Name, Guid? AssetId, decimal? StdCycleSec, int? SetupMinutes);

public sealed record WorkOrderStep(int Seq, string Name, Guid? AssetId, string? AssetName, decimal? StdCycleSec, string Status, int? GoodQty, int? ScrapQty,
    DateTime? StartedAt, DateTime? DoneAt);

public sealed record StepDoneRequest(int GoodQty, int ScrapQty = 0);

/// <summary>Material for one work order: what the BOM needs for the ordered quantity, what was issued, and what is short.</summary>
public sealed record MaterialNeed(Guid ItemId, string ItemCode, string ItemName, string Unit, decimal Required, decimal Issued, decimal Remaining,
    decimal OnHand, decimal Short);

public sealed record MaterialsReport(Guid WorkOrderId, int Quantity, bool Ready, IReadOnlyList<MaterialNeed> Lines);

public sealed record IssuedLot(string ItemName, string? LotNo, decimal Quantity);

public sealed record IssueMaterialsResult(IReadOnlyList<IssuedLot> Issued, IReadOnlyList<MaterialNeed> StillShort);

/// <summary>An available lot of an item, oldest first (first in, first out).</summary>
public sealed record OpenLot(Guid LotId, string LotNo, decimal OnHand);

public interface IPlanningRepository
{
    Task<IReadOnlyList<BomLine>> BomAsync(Guid tenantId, Guid productId, CancellationToken ct);
    Task SaveBomAsync(Guid tenantId, Guid productId, IReadOnlyList<BomLineInput> lines, CancellationToken ct);
    Task<IReadOnlyList<RoutingStep>> RoutingAsync(Guid tenantId, Guid productId, CancellationToken ct);
    Task SaveRoutingAsync(Guid tenantId, Guid productId, IReadOnlyList<RoutingStepInput> steps, CancellationToken ct);
    Task CopyRoutingAsync(Guid tenantId, Guid workOrderId, Guid productId, CancellationToken ct);
    Task<IReadOnlyList<WorkOrderStep>> StepsAsync(Guid tenantId, Guid workOrderId, CancellationToken ct);
    Task SetStepAsync(Guid tenantId, Guid workOrderId, int seq, string status, int? good, int? scrap, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, decimal>> IssuedAsync(Guid tenantId, Guid workOrderId, CancellationToken ct);
    Task<IReadOnlyList<OpenLot>> OpenLotsAsync(Guid tenantId, Guid itemId, CancellationToken ct);
    Task<IReadOnlyList<Guid>> ItemIdsAsync(Guid tenantId, CancellationToken ct);
    Task<IReadOnlyList<Guid>> AssetIdsAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>Bills of materials, routings, work-order operations and material issue (first in, first out by lot).</summary>
public sealed class PlanningService(IPlanningRepository repo, IProductionRepository production, Inventory.IInventoryRepository inventory, ModuleGate gate,
    ICurrentUser me, IAuditLog audit)
{
    private async Task<Guid> Tenant(CancellationToken ct)
    {
        await gate.EnsureAsync("production", ct);
        return me.RequireTenant();
    }

    private async Task<Product> ProductOf(Guid tenantId, Guid productId, CancellationToken ct) =>
        (await production.ProductsAsync(tenantId, ct)).FirstOrDefault(p => p.ProductId == productId) ?? throw AppException.NotFound("Product");

    private async Task<WorkOrder> OrderOf(Guid tenantId, Guid workOrderId, CancellationToken ct) =>
        await production.WorkOrderAsync(tenantId, workOrderId, ct) ?? throw AppException.NotFound("Work order");

    public async Task<IReadOnlyList<BomLine>> BomAsync(Guid productId, CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        await ProductOf(tenantId, productId, ct);
        return await repo.BomAsync(tenantId, productId, ct);
    }

    public async Task SaveBomAsync(Guid productId, IReadOnlyList<BomLineInput> lines, CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        await ProductOf(tenantId, productId, ct);
        if (lines.Count > 100 || lines.Select(l => l.ItemId).Distinct().Count() != lines.Count)
        {
            throw AppException.Validation("A bill of materials lists each item once, at most 100 items.");
        }
        if (lines.Any(l => l.QtyPer <= 0 || l.ScrapPct is < 0 or > 100))
        {
            throw AppException.Validation("Quantities must be more than zero and scrap 0–100 %.");
        }
        var items = (await repo.ItemIdsAsync(tenantId, ct)).ToHashSet();
        if (lines.Any(l => !items.Contains(l.ItemId)))
        {
            throw AppException.NotFound("Item");
        }
        await repo.SaveBomAsync(tenantId, productId, lines, ct);
        await audit.WriteAsync("bom.save", "product", productId.ToString(), new { lines = lines.Count }, ct);
    }

    public async Task<IReadOnlyList<RoutingStep>> RoutingAsync(Guid productId, CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        await ProductOf(tenantId, productId, ct);
        return await repo.RoutingAsync(tenantId, productId, ct);
    }

    public async Task SaveRoutingAsync(Guid productId, IReadOnlyList<RoutingStepInput> steps, CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        await ProductOf(tenantId, productId, ct);
        if (steps.Count > 50 || steps.Any(s => string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 100 || s.StdCycleSec <= 0 || s.SetupMinutes < 0))
        {
            throw AppException.Validation("Each operation needs a name (≤100 characters); times must be positive; at most 50 operations.");
        }
        var assets = (await repo.AssetIdsAsync(tenantId, ct)).ToHashSet();
        if (steps.Any(s => s.AssetId is { } a && !assets.Contains(a)))
        {
            throw AppException.NotFound("Machine");
        }
        await repo.SaveRoutingAsync(tenantId, productId, [.. steps.Select(s => s with { Name = s.Name.Trim() })], ct);
        await audit.WriteAsync("routing.save", "product", productId.ToString(), new { steps = steps.Count }, ct);
    }

    /// <summary>Called when a work order is created: it gets its own copy of the product's routing.</summary>
    public Task CopyRoutingAsync(Guid tenantId, Guid workOrderId, Guid productId, CancellationToken ct) => repo.CopyRoutingAsync(tenantId, workOrderId, productId, ct);

    public static decimal Required(int quantity, decimal qtyPer, decimal scrapPct) => Math.Round(quantity * qtyPer * (1 + scrapPct / 100m), 3);

    public async Task<MaterialsReport> MaterialsAsync(Guid workOrderId, CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        var wo = await OrderOf(tenantId, workOrderId, ct);
        var bom = await repo.BomAsync(tenantId, wo.ProductId, ct);
        var issued = await repo.IssuedAsync(tenantId, workOrderId, ct);
        var lines = bom.Select(b =>
        {
            var required = Required(wo.Quantity, b.QtyPer, b.ScrapPct);
            var done = issued.GetValueOrDefault(b.ItemId);
            var remaining = Math.Max(0, required - done);
            return new MaterialNeed(b.ItemId, b.ItemCode, b.ItemName, b.Unit, required, done, remaining, b.OnHand, Math.Max(0, remaining - b.OnHand));
        }).ToList();
        return new MaterialsReport(workOrderId, wo.Quantity, lines.All(l => l.Short == 0), lines);
    }

    /// <summary>Issues what the order still needs from the oldest available lots; what cannot be covered is reported as short.</summary>
    public async Task<IssueMaterialsResult> IssueMaterialsAsync(Guid workOrderId, CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        var wo = await OrderOf(tenantId, workOrderId, ct);
        if (wo.Status is "Completed" or "Cancelled")
        {
            throw AppException.Validation("The work order is finished.");
        }
        var report = await MaterialsAsync(workOrderId, ct);
        var issued = new List<IssuedLot>();
        foreach (var need in report.Lines.Where(l => l.Remaining > 0))
        {
            var left = need.Remaining;
            foreach (var lot in await repo.OpenLotsAsync(tenantId, need.ItemId, ct))
            {
                if (left <= 0)
                {
                    break;
                }
                var take = Math.Min(left, lot.OnHand);
                await inventory.AddMovementAsync(tenantId, need.ItemId, lot.LotId, "Issue", -take, workOrderId, null, $"Material for {wo.Code}", me.UserId, ct);
                await inventory.SetLotStatusFromStockAsync(tenantId, lot.LotId, ct);
                issued.Add(new IssuedLot(need.ItemName, lot.LotNo, take));
                left -= take;
            }
        }
        await audit.WriteAsync("workorder.issue-materials", "workorder", workOrderId.ToString(), new { lots = issued.Count }, ct);
        var after = await MaterialsAsync(workOrderId, ct);
        return new IssueMaterialsResult(issued, [.. after.Lines.Where(l => l.Remaining > 0)]);
    }

    public async Task<IReadOnlyList<WorkOrderStep>> StepsAsync(Guid workOrderId, CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        await OrderOf(tenantId, workOrderId, ct);
        return await repo.StepsAsync(tenantId, workOrderId, ct);
    }

    /// <summary>Operations run in order on a running work order: an operation starts when the one before it is done.</summary>
    public async Task ChangeStepAsync(Guid workOrderId, int seq, string action, StepDoneRequest? done, CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        var wo = await OrderOf(tenantId, workOrderId, ct);
        if (wo.Status != "Running")
        {
            throw AppException.Validation("Start the work order before its operations.");
        }
        var steps = await repo.StepsAsync(tenantId, workOrderId, ct);
        var step = steps.FirstOrDefault(s => s.Seq == seq) ?? throw AppException.NotFound("Operation");
        switch (action)
        {
            case "start" when step.Status == "Pending":
                if (steps.Any(s => s.Seq < seq && s.Status != "Done"))
                {
                    throw AppException.Validation("Finish the previous operation first.");
                }
                await repo.SetStepAsync(tenantId, workOrderId, seq, "Running", null, null, ct);
                break;
            case "complete" when step.Status == "Running":
                if (done is null || done.GoodQty < 0 || done.ScrapQty < 0)
                {
                    throw AppException.Validation("Enter the good and scrap quantities.");
                }
                await repo.SetStepAsync(tenantId, workOrderId, seq, "Done", done.GoodQty, done.ScrapQty, ct);
                break;
            default:
                throw AppException.Validation($"An operation that is {step.Status.ToLowerInvariant()} cannot be {action}ed.");
        }
        await audit.WriteAsync($"wostep.{action}", "workorder", workOrderId.ToString(), new { seq }, ct);
    }
}
