using Divakar.FactoryOS.Application.Common;

namespace Divakar.FactoryOS.Application.Enms;

/// <summary>Non-energy GHG activity data (fugitive, process, Scope 3), the grid region in use, and product footprint inputs.</summary>
public sealed partial class EnmsService
{
    public async Task<IReadOnlyList<GhgActivity>> ActivitiesAsync(CancellationToken ct) => await ghg.ActivitiesAsync(await Begin(ct), null, null, ct);

    public async Task<Guid> AddActivityAsync(SaveActivityRequest r, CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        var factor = (await factors.FactorsAsync(tenantId, ct)).FirstOrDefault(f => f.FactorId == r.FactorId) ?? throw AppException.NotFound("Emission factor");
        if (EmissionMath.Category(factor.Source) == "energy")
        {
            throw AppException.Validation("Fuel and electricity go under Energy data (they also count as energy use); activities are refrigerant, process and Scope 3.");
        }
        if (!factor.IsActive)
        {
            throw AppException.Validation("This factor is switched off.");
        }
        if (r.Quantity <= 0)
        {
            throw AppException.Validation($"Give the quantity in {factor.Unit} (more than zero).");
        }
        if (r.PeriodFrom.Date > r.PeriodTo.Date)
        {
            throw AppException.Validation("The period must start on or before its end.");
        }
        var id = await ghg.AddActivityAsync(tenantId, r with
        {
            Reference = Guard.Optional(r.Reference, "Reference", 200), Note = Guard.Optional(r.Note, "Note", 500),
        }, ct);
        await audit.WriteAsync("enms.activity.create", "GhgActivity", id.ToString(), new { factor.Source, r.Quantity, r.PeriodFrom, r.PeriodTo }, ct);
        return id;
    }

    public async Task DeleteActivityAsync(Guid id, CancellationToken ct)
    {
        await ghg.DeleteActivityAsync(await Begin(ct), id, ct);
        await audit.WriteAsync("enms.activity.delete", "GhgActivity", id.ToString(), null, ct);
    }

    /// <summary>Uses the grid electricity factors of one region (location-based Scope 2) from the library.</summary>
    public async Task<string> UseGridRegionAsync(string region, CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        var code = (region ?? "").Trim().ToUpperInvariant();
        var lib = await factors.FactorsAsync(tenantId, ct);
        var regions = lib.Where(f => f.Source == "electricity" && f.Method == "Location").Select(f => f.Region).Distinct().ToList();
        if (!regions.Contains(code))
        {
            throw AppException.Validation($"No grid factors for '{code}'. Regions in the library: {string.Join(", ", regions.Order())}. Add the factor first.");
        }
        await ghg.UseGridRegionAsync(tenantId, code, ct);
        factors.Forget();
        await audit.WriteAsync("enms.factor.region", "EmissionFactor", null, new { region = code }, ct);
        return code;
    }

    public async Task SetItemFactorAsync(Guid itemId, decimal? kgCo2ePerUnit, string? source, CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        if (kgCo2ePerUnit < 0)
        {
            throw AppException.Validation("The factor cannot be negative.");
        }
        if (kgCo2ePerUnit is not null && string.IsNullOrWhiteSpace(source))
        {
            throw AppException.Validation("Say where the factor comes from (supplier EPD, database and version…).");
        }
        await ghg.SetItemFactorAsync(tenantId, itemId, kgCo2ePerUnit, Guard.Optional(source, "Source", 200), ct);
        await audit.WriteAsync("enms.item.co2e", "Item", itemId.ToString(), new { kgCo2ePerUnit, source }, ct);
    }

    public async Task SetProductMassAsync(Guid productId, decimal? massKg, CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        if (massKg is <= 0)
        {
            throw AppException.Validation("The mass of one unit is more than zero kg.");
        }
        await ghg.SetProductMassAsync(tenantId, productId, massKg, ct);
        await audit.WriteAsync("enms.product.mass", "Product", productId.ToString(), new { massKg }, ct);
    }
}
