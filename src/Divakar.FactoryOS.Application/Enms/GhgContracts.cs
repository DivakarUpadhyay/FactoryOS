namespace Divakar.FactoryOS.Application.Enms;

/// <summary>Activity data for a non-energy factor (refrigerant top-up, carbonate calcined, freight, travel, waste…).</summary>
public sealed record GhgActivity(Guid ActivityId, Guid FactorId, string Source, string FactorName, byte Scope, byte? Scope3Category, string Method,
    DateTime PeriodFrom, DateTime PeriodTo, decimal Quantity, string Unit, decimal KgCo2ePerUnit, string? Reference, string? Note, DateTime CreatedAt)
{
    public double Co2eKg => Math.Round((double)(Quantity * KgCo2ePerUnit), 2);
}

public sealed record SaveActivityRequest(Guid FactorId, DateTime PeriodFrom, DateTime PeriodTo, decimal Quantity, string? Reference, string? Note);

/// <summary>One line of the inventory from activity data, counted for the share of its period inside the report.</summary>
public sealed record GhgLine(string Source, string Name, byte Scope, byte? Scope3Category, string Method, string Unit, double Quantity, double Kg);

public sealed record Scope3Line(int Category, string Name, double Kg);

/// <summary>A completed work order with the energy its machine used while it ran (hourly roll-ups, so to the hour).</summary>
public sealed record FootprintOrder(Guid WorkOrderId, string Code, Guid ProductId, string ProductCode, string ProductName, decimal? MassKg, Guid AssetId,
    string AssetName, DateTime ActualStart, DateTime ActualEnd, int GoodQty, double Kwh);

public sealed record BomMaterial(Guid ProductId, string ItemCode, string ItemName, string Unit, decimal QtyPer, decimal ScrapPct, decimal? KgCo2ePerUnit,
    string? Co2eSource, Guid ItemId);

public sealed record MaterialFootprint(string ItemCode, string ItemName, string Unit, double QtyPerUnit, double? KgCo2ePerUnit, double KgPerProduct, string? Source, Guid ItemId);

public sealed record ProductFootprint(Guid ProductId, string Code, string Name, int Orders, int Units, decimal? MassKg, double DirectKwh, double OverheadKwh,
    double ElectricityKg, double Scope1Kg, double MaterialsKg, double TotalKg, double KgPerUnit, double? KgPerTonne, double ElectricityKgPerUnit,
    double MaterialsKgPerUnit, int MaterialsWithFactor, int MaterialsTotal, IReadOnlyList<MaterialFootprint> Materials);

public sealed record FootprintReport(DateTime From, DateTime To, string Boundary, decimal ElectricityFactor, string? ElectricityReference, double SiteKwh,
    double MachinesKwh, double OverheadSharePct, double SiteScope1Kg, IReadOnlyList<ProductFootprint> Products, IReadOnlyList<string> Notes);

public interface IGhgRepository
{
    Task<IReadOnlyList<GhgActivity>> ActivitiesAsync(Guid tenantId, DateTime? from, DateTime? to, CancellationToken ct);
    Task<Guid> AddActivityAsync(Guid tenantId, SaveActivityRequest r, CancellationToken ct);
    Task DeleteActivityAsync(Guid tenantId, Guid id, CancellationToken ct);

    /// <summary>Turns on the location-based electricity factors of one region and off those of the others; returns how many are on.</summary>
    Task<int> UseGridRegionAsync(Guid tenantId, string region, CancellationToken ct);

    Task<IReadOnlyList<FootprintOrder>> CompletedOrdersAsync(Guid tenantId, DateTime from, DateTime to, CancellationToken ct);
    Task<IReadOnlyList<BomMaterial>> BomMaterialsAsync(Guid tenantId, IReadOnlyCollection<Guid> productIds, CancellationToken ct);

    Task SetItemFactorAsync(Guid tenantId, Guid itemId, decimal? kgCo2ePerUnit, string? source, CancellationToken ct);
    Task SetProductMassAsync(Guid tenantId, Guid productId, decimal? massKg, CancellationToken ct);
}
