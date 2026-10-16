using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Settings;

namespace Divakar.FactoryOS.Application.Inventory;

public sealed record Item(Guid ItemId, string Code, string Name, string Category, string Unit, decimal MinStock, decimal? ReorderQty, string? Location,
    string? Barcode, Guid? ProductId, bool IsActive, decimal OnHand, int Lots, bool Low);

public sealed record SaveItemRequest(string Code, string Name, string Category, string? Unit, decimal MinStock, decimal? ReorderQty, string? Location,
    string? Barcode, Guid? ProductId, bool IsActive = true);

public sealed record Lot(Guid LotId, Guid ItemId, string ItemCode, string ItemName, string LotNo, string Status, decimal OnHand, string? Supplier,
    string? SupplierLot, DateOnly? ExpiresOn, Guid? WorkOrderId, string? WorkOrderCode, DateTime CreatedAt);

public sealed record Movement(long MovementId, Guid ItemId, string ItemName, string? LotNo, string Type, decimal Quantity, Guid? WorkOrderId, string? WorkOrderCode,
    string? MaintWoCode, string? Reference, string? UserName, DateTime Ts);

public sealed record ReceiveRequest(Guid ItemId, decimal Quantity, string? LotNo, string? Supplier, string? SupplierLot, DateOnly? ExpiresOn, string? Reference);

public sealed record IssueRequest(Guid ItemId, decimal Quantity, Guid? LotId, Guid? WorkOrderId, Guid? MaintWoId, string? Reference);

public sealed record ProduceRequest(Guid WorkOrderId, decimal Quantity, string? LotNo);

public sealed record AdjustRequest(Guid ItemId, Guid? LotId, decimal Quantity, string Type, string? Reference);

public sealed record LookupResult(string Kind, Item? Item, Lot? Lot);

public sealed record TraceStep(string Kind, string Title, string? Detail, DateTime? At, string? Link);

public sealed record TraceResult(Lot Lot, IReadOnlyList<TraceStep> Produced, IReadOnlyList<Lot> Inputs, IReadOnlyList<TraceStep> UsedIn,
    IReadOnlyList<TraceStep> Quality, IReadOnlyList<TraceStep> Events, IReadOnlyList<Movement> Movements);

/// <summary>What a work order was: its product, machine and run window (for genealogy and events during the run).</summary>
public sealed record WorkOrderRef(Guid WorkOrderId, string Code, Guid ProductId, string ProductName, Guid AssetId, string AssetName, string Status,
    DateTime? ActualStart, DateTime? ActualEnd, int Produced);

public interface IInventoryRepository
{
    Task<IReadOnlyList<Item>> ItemsAsync(Guid tenantId, CancellationToken ct);
    Task<Guid> SaveItemAsync(Guid tenantId, Guid? id, SaveItemRequest r, CancellationToken ct);
    Task<bool> ItemCodeOrBarcodeTakenAsync(Guid tenantId, string code, string? barcode, Guid? exceptId, CancellationToken ct);
    Task<IReadOnlyList<Lot>> LotsAsync(Guid tenantId, Guid? itemId, string? lotNo, Guid? workOrderId, CancellationToken ct);
    Task<IReadOnlyList<Movement>> MovementsAsync(Guid tenantId, Guid? itemId, Guid? lotId, Guid? workOrderId, int take, CancellationToken ct);
    Task<Guid> CreateLotAsync(Guid tenantId, Guid itemId, string lotNo, string? supplier, string? supplierLot, DateOnly? expiresOn, Guid? workOrderId, CancellationToken ct);
    Task<bool> LotNoExistsAsync(Guid tenantId, string lotNo, CancellationToken ct);
    Task AddMovementAsync(Guid tenantId, Guid itemId, Guid? lotId, string type, decimal quantity, Guid? workOrderId, Guid? maintWoId, string? reference,
        Guid userId, CancellationToken ct);
    Task SetLotStatusFromStockAsync(Guid tenantId, Guid lotId, CancellationToken ct);
    Task<WorkOrderRef?> WorkOrderAsync(Guid tenantId, Guid workOrderId, CancellationToken ct);
    Task<bool> MaintWoExistsAsync(Guid tenantId, Guid maintWoId, CancellationToken ct);
    Task<IReadOnlyList<TraceStep>> QualityForLotAsync(Guid tenantId, string lotNo, Guid? workOrderId, CancellationToken ct);
    Task<IReadOnlyList<TraceStep>> EventsAsync(Guid tenantId, Guid assetId, DateTime from, DateTime to, CancellationToken ct);
}

public sealed class InventoryService(IInventoryRepository repo, ModuleGate gate, ICurrentUser me, IAuditLog audit)
{
    public static readonly string[] Categories = ["Raw", "WIP", "Finished", "Spare", "Consumable"];

    private async Task<Guid> Tenant(CancellationToken ct)
    {
        await gate.EnsureAsync("inventory", ct);
        return me.RequireTenant();
    }

    public async Task<IReadOnlyList<Item>> ItemsAsync(CancellationToken ct) => await repo.ItemsAsync(await Tenant(ct), ct);

    public async Task<Guid> SaveItemAsync(Guid? id, SaveItemRequest r, CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        var code = Guard.Code(r.Code, "Code");
        if (!Categories.Contains(r.Category))
        {
            throw AppException.Validation("Category is Raw, WIP, Finished, Spare or Consumable.");
        }
        if (r.MinStock < 0 || r.ReorderQty < 0)
        {
            throw AppException.Validation("Stock levels cannot be negative.");
        }
        var barcode = Guard.Optional(r.Barcode, "Barcode", 60);
        if (await repo.ItemCodeOrBarcodeTakenAsync(tenantId, code, barcode, id, ct))
        {
            throw AppException.Conflict("Another item already uses this code or barcode.");
        }
        if (id is { } existing && (await repo.ItemsAsync(tenantId, ct)).All(i => i.ItemId != existing))
        {
            throw AppException.NotFound("Item");
        }
        var saved = await repo.SaveItemAsync(tenantId, id, r with
        {
            Code = code, Name = Guard.Required(r.Name, "Name", 150), Unit = Guard.Optional(r.Unit, "Unit", 20) ?? "pcs",
            Location = Guard.Optional(r.Location, "Location", 60), Barcode = barcode,
        }, ct);
        await audit.WriteAsync(id is null ? "item.create" : "item.update", "item", saved.ToString(), null, ct);
        return saved;
    }

    public async Task<IReadOnlyList<Lot>> LotsAsync(Guid? itemId, CancellationToken ct) => await repo.LotsAsync(await Tenant(ct), itemId, null, null, ct);

    public async Task<IReadOnlyList<Movement>> MovementsAsync(Guid? itemId, CancellationToken ct) => await repo.MovementsAsync(await Tenant(ct), itemId, null, null, 300, ct);

    private static string NewLotNo(string prefix) => $"{prefix}{DateTime.UtcNow:yyMMdd}-{Random.Shared.Next(1000, 9999)}";

    public async Task<Guid> ReceiveAsync(ReceiveRequest r, CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        var item = (await repo.ItemsAsync(tenantId, ct)).FirstOrDefault(i => i.ItemId == r.ItemId) ?? throw AppException.NotFound("Item");
        if (r.Quantity <= 0)
        {
            throw AppException.Validation("The quantity must be more than zero.");
        }
        var lotNo = Guard.Optional(r.LotNo, "Lot", 60) ?? NewLotNo("L");
        if (await repo.LotNoExistsAsync(tenantId, lotNo, ct))
        {
            throw AppException.Conflict($"Lot {lotNo} already exists.");
        }
        var lot = await repo.CreateLotAsync(tenantId, item.ItemId, lotNo, Guard.Optional(r.Supplier, "Supplier", 100), Guard.Optional(r.SupplierLot, "Supplier lot", 60),
            r.ExpiresOn, null, ct);
        await repo.AddMovementAsync(tenantId, item.ItemId, lot, "Receive", r.Quantity, null, null, Guard.Optional(r.Reference, "Reference", 100), me.UserId, ct);
        await audit.WriteAsync("inventory.receive", "lot", lot.ToString(), new { lotNo, r.Quantity }, ct);
        return lot;
    }

    /// <summary>Takes stock out to a production order (material consumption, which builds the genealogy) or a maintenance order (spare parts).</summary>
    public async Task IssueAsync(IssueRequest r, CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        var item = (await repo.ItemsAsync(tenantId, ct)).FirstOrDefault(i => i.ItemId == r.ItemId) ?? throw AppException.NotFound("Item");
        if (r.Quantity <= 0)
        {
            throw AppException.Validation("The quantity must be more than zero.");
        }
        if (r.WorkOrderId is { } wo && await repo.WorkOrderAsync(tenantId, wo, ct) is null)
        {
            throw AppException.NotFound("Work order");
        }
        if (r.MaintWoId is { } mwo && !await repo.MaintWoExistsAsync(tenantId, mwo, ct))
        {
            throw AppException.NotFound("Maintenance work order");
        }
        if (r.LotId is { } lotId)
        {
            var lot = (await repo.LotsAsync(tenantId, item.ItemId, null, null, ct)).FirstOrDefault(l => l.LotId == lotId) ?? throw AppException.NotFound("Lot");
            if (lot.Status == "Quarantine")
            {
                throw AppException.Validation($"Lot {lot.LotNo} is in quarantine and cannot be used.");
            }
            if (lot.OnHand < r.Quantity)
            {
                throw AppException.Validation($"Lot {lot.LotNo} has only {lot.OnHand:0.###} {item.Unit}.");
            }
        }
        else if (item.OnHand < r.Quantity)
        {
            throw AppException.Validation($"Only {item.OnHand:0.###} {item.Unit} of {item.Name} in stock.");
        }
        await repo.AddMovementAsync(tenantId, item.ItemId, r.LotId, "Issue", -r.Quantity, r.WorkOrderId, r.MaintWoId, Guard.Optional(r.Reference, "Reference", 100), me.UserId, ct);
        if (r.LotId is { } l)
        {
            await repo.SetLotStatusFromStockAsync(tenantId, l, ct);
        }
        await audit.WriteAsync("inventory.issue", "item", item.ItemId.ToString(), new { r.Quantity, r.WorkOrderId, r.MaintWoId }, ct);
    }

    /// <summary>Books finished goods from a work order into a new lot of the product's item.</summary>
    public async Task<Guid> ProduceAsync(ProduceRequest r, CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        var wo = await repo.WorkOrderAsync(tenantId, r.WorkOrderId, ct) ?? throw AppException.NotFound("Work order");
        var item = (await repo.ItemsAsync(tenantId, ct)).FirstOrDefault(i => i.ProductId == wo.ProductId)
                   ?? throw AppException.Validation($"Create an inventory item linked to the product {wo.ProductName} first.");
        if (r.Quantity <= 0)
        {
            throw AppException.Validation("The quantity must be more than zero.");
        }
        var lotNo = Guard.Optional(r.LotNo, "Lot", 60) ?? NewLotNo("FG");
        if (await repo.LotNoExistsAsync(tenantId, lotNo, ct))
        {
            throw AppException.Conflict($"Lot {lotNo} already exists.");
        }
        var lot = await repo.CreateLotAsync(tenantId, item.ItemId, lotNo, null, null, null, wo.WorkOrderId, ct);
        await repo.AddMovementAsync(tenantId, item.ItemId, lot, "Produce", r.Quantity, wo.WorkOrderId, null, wo.Code, me.UserId, ct);
        await audit.WriteAsync("inventory.produce", "lot", lot.ToString(), new { lotNo, r.Quantity, wo = wo.Code }, ct);
        return lot;
    }

    /// <summary>Stock count corrections (Adjust, plus or minus) and scrap (always out).</summary>
    public async Task AdjustAsync(AdjustRequest r, CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        var item = (await repo.ItemsAsync(tenantId, ct)).FirstOrDefault(i => i.ItemId == r.ItemId) ?? throw AppException.NotFound("Item");
        if (r.Type is not ("Adjust" or "Scrap") || r.Quantity == 0)
        {
            throw AppException.Validation("Type is Adjust or Scrap, with a non-zero quantity.");
        }
        var qty = r.Type == "Scrap" ? -Math.Abs(r.Quantity) : r.Quantity;
        if (item.OnHand + qty < 0)
        {
            throw AppException.Validation("Stock cannot go below zero.");
        }
        await repo.AddMovementAsync(tenantId, item.ItemId, r.LotId, r.Type, qty, null, null, Guard.Optional(r.Reference, "Reference", 100), me.UserId, ct);
        if (r.LotId is { } l)
        {
            await repo.SetLotStatusFromStockAsync(tenantId, l, ct);
        }
        await audit.WriteAsync($"inventory.{r.Type.ToLowerInvariant()}", "item", item.ItemId.ToString(), new { qty }, ct);
    }

    /// <summary>What a scanned code is: an item barcode or code, or a lot number.</summary>
    public async Task<LookupResult> LookupAsync(string code, CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        var c = code.Trim();
        var item = (await repo.ItemsAsync(tenantId, ct)).FirstOrDefault(i =>
            string.Equals(i.Barcode, c, StringComparison.OrdinalIgnoreCase) || string.Equals(i.Code, c, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            return new LookupResult("item", item, null);
        }
        var lot = (await repo.LotsAsync(tenantId, null, c, null, ct)).FirstOrDefault();
        return lot is not null ? new LookupResult("lot", null, lot) : throw AppException.NotFound($"Nothing with the code '{c}'");
    }

    /// <summary>
    /// Lot genealogy: how the lot was made (work order, machine, run window), which input lots went in, where it was used,
    /// its quality records, and the alarms and stops on the machine while it was being made.
    /// </summary>
    public async Task<TraceResult> TraceAsync(string lotNo, CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        var lot = (await repo.LotsAsync(tenantId, null, lotNo.Trim(), null, ct)).FirstOrDefault() ?? throw AppException.NotFound($"Lot {lotNo}");
        var produced = new List<TraceStep>();
        var inputs = new List<Lot>();
        var events = new List<TraceStep>();
        WorkOrderRef? wo = null;
        if (lot.WorkOrderId is { } woId && await repo.WorkOrderAsync(tenantId, woId, ct) is { } w)
        {
            wo = w;
            produced.Add(new TraceStep("workorder", $"Work order {w.Code}", $"{w.ProductName} on {w.AssetName} · {w.Produced} made · {w.Status}", w.ActualStart, "/app/production"));
            produced.Add(new TraceStep("machine", w.AssetName, w.ActualStart is null ? null : $"Ran {w.ActualStart:yyyy-MM-dd HH:mm} – {(w.ActualEnd is { } e ? e.ToString("HH:mm") : "now")} UTC",
                w.ActualStart, $"/app/plant/assets/{w.AssetId}"));
            var consumed = (await repo.MovementsAsync(tenantId, null, null, w.WorkOrderId, 500, ct)).Where(m => m.Type == "Issue" && m.LotNo is not null)
                .Select(m => m.LotNo!).Distinct().ToList();
            foreach (var input in consumed)
            {
                inputs.AddRange(await repo.LotsAsync(tenantId, null, input, null, ct));
            }
            if (w.ActualStart is { } start)
            {
                events.AddRange(await repo.EventsAsync(tenantId, w.AssetId, start, w.ActualEnd ?? DateTime.UtcNow, ct));
            }
        }
        else
        {
            produced.Add(new TraceStep("receipt", "Received", $"{lot.Supplier ?? "supplier not recorded"}{(lot.SupplierLot is null ? "" : $" · supplier lot {lot.SupplierLot}")}", lot.CreatedAt, null));
        }
        var movements = await repo.MovementsAsync(tenantId, null, lot.LotId, null, 500, ct);
        var usedIn = new List<TraceStep>();
        foreach (var m in movements.Where(m => m.Type == "Issue"))
        {
            var downstream = m.WorkOrderId is { } used ? await repo.LotsAsync(tenantId, null, null, used, ct) : [];
            usedIn.Add(new TraceStep("issue", m.WorkOrderCode is not null ? $"Work order {m.WorkOrderCode}" : m.MaintWoCode is not null ? $"Maintenance {m.MaintWoCode}" : "Issued",
                $"{-m.Quantity:0.###} used{(downstream.Count > 0 ? " → lots " + string.Join(", ", downstream.Select(d => d.LotNo)) : "")}", m.Ts, null));
        }
        var quality = await repo.QualityForLotAsync(tenantId, lot.LotNo, wo?.WorkOrderId, ct);
        return new TraceResult(lot, produced, inputs, usedIn, quality, events, movements);
    }
}
