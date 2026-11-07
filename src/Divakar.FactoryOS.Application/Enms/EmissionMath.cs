namespace Divakar.FactoryOS.Application.Enms;

/// <summary>One emission factor version (the library behind Carbon / ESG and the ISO 50001 energy review).</summary>
public sealed record EmissionFactor(Guid FactorId, string Source, string Name, byte Scope, string Method, string Region, short Year, string Unit,
    decimal? HeatKcalPerUnit, decimal? Co2KgPerTj, decimal? Ch4KgPerTj, decimal? N2oKgPerTj, decimal? KgCo2ePerUnit, string GwpSet,
    string Reference, bool IsActive, byte? Scope3Category = null)
{
    /// <summary>kg CO2e per unit: the published total, else built from the per-TJ factors, the heat value and the GWP set.</summary>
    public decimal Co2e => EmissionMath.KgCo2ePerUnit(this);
}

public sealed record SaveFactorRequest(string Source, string Name, byte Scope, string Method, string Region, short Year, string Unit,
    decimal? HeatKcalPerUnit, decimal? Co2KgPerTj, decimal? Ch4KgPerTj, decimal? N2oKgPerTj, decimal? KgCo2ePerUnit, string? GwpSet,
    string Reference, bool IsActive, byte? Scope3Category = null);

public static class EmissionMath
{
    /// <summary>1 kcal = 4.1868 × 10⁻⁹ TJ (the conversion the Ministry of Environment's factor table states).</summary>
    public const decimal TjPerKcal = 4.1868e-9m;

    /// <summary>kcal in one kWh, used to put fuels and electricity on one energy scale (Energy Administration heat table).</summary>
    public const double KcalPerKwh = 860;

    public static readonly string[] Sources = ["electricity", "natural_gas", "diesel", "gasoline", "lpg", "fuel_oil", "steam"];

    /// <summary>Codes of factors that are not energy: ref_ (refrigerant and other GHG losses), proc_ (process emissions), s3_ (Scope 3 activities).</summary>
    public static readonly System.Text.RegularExpressions.Regex ActivityCode = new("^(ref|proc|s3)_[a-z0-9_]{1,25}$");

    /// <summary>energy, refrigerant, process or scope3, from the factor's source code.</summary>
    public static string Category(string source) =>
        source.StartsWith("ref_", StringComparison.Ordinal) ? "refrigerant"
        : source.StartsWith("proc_", StringComparison.Ordinal) ? "process"
        : source.StartsWith("s3_", StringComparison.Ordinal) ? "scope3" : "energy";

    /// <summary>GHG Protocol Scope 3 category names.</summary>
    public static string Scope3Name(int category) => category switch
    {
        1 => "1 Purchased goods and services", 2 => "2 Capital goods", 3 => "3 Fuel- and energy-related activities", 4 => "4 Upstream transportation",
        5 => "5 Waste generated in operations", 6 => "6 Business travel", 7 => "7 Employee commuting", 8 => "8 Upstream leased assets",
        9 => "9 Downstream transportation", 10 => "10 Processing of sold products", 11 => "11 Use of sold products", 12 => "12 End-of-life treatment",
        13 => "13 Downstream leased assets", 14 => "14 Franchises", 15 => "15 Investments", _ => "Uncategorised",
    };

    /// <summary>100-year GWPs. AR5 is what Taiwan's 2024 factor announcement prescribes (CH4 28, N2O 265); AR6 uses fossil CH4 29.8, N2O 273.</summary>
    public static (decimal Ch4, decimal N2o) Gwp(string set) => set == "AR6" ? (29.8m, 273m) : (28m, 265m);

    public static decimal KgCo2ePerUnit(EmissionFactor f)
    {
        if (f.KgCo2ePerUnit is { } total)
        {
            return total;
        }
        var tj = (f.HeatKcalPerUnit ?? 0) * TjPerKcal;
        var (ch4, n2o) = Gwp(f.GwpSet);
        return Math.Round(tj * ((f.Co2KgPerTj ?? 0) + (f.Ch4KgPerTj ?? 0) * ch4 + (f.N2oKgPerTj ?? 0) * n2o), 6);
    }

    /// <summary>kWh-equivalent of one unit of a source (fuels by their net heat value), for comparing energy across sources.</summary>
    public static double KwhPerUnit(EmissionFactor? f, string source) =>
        source is "electricity" or "green_power" ? 1 : f?.HeatKcalPerUnit is { } kcal ? (double)kcal / KcalPerKwh : 0;

    /// <summary>
    /// The factor that applies to a source in a year: active, matching method, the latest year not after the one asked for,
    /// else the earliest one on file (so a period before the library starts still gets a factor).
    /// </summary>
    public static EmissionFactor? Resolve(IEnumerable<EmissionFactor> factors, string source, string method, int year)
    {
        var list = factors.Where(f => f.IsActive && f.Source == source && f.Method == method).ToList();
        return list.Where(f => f.Year <= year).MaxBy(f => f.Year) ?? list.MinBy(f => f.Year);
    }

    /// <summary>The share of an entry's period [from, to] (whole days, inclusive) that falls inside [winFrom, winTo).</summary>
    public static double Overlap(DateTime from, DateTime to, DateTime winFrom, DateTime winTo)
    {
        var start = from.Date;
        var end = to.Date.AddDays(1);
        var days = (end - start).TotalDays;
        var inside = (Min(end, winTo) - Max(start, winFrom)).TotalDays;
        return days <= 0 || inside <= 0 ? 0 : Math.Min(1, inside / days);
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private const string MoeaElectricity = "MOEA Energy Administration, annual electricity emission factor (public utility power)";
    private const string MoenvFuel = "MOENV announcement 2024-02-05 「溫室氣體排放係數」 Table 1 (IPCC 2006 stationary combustion, kg/TJ); "
        + "net heat value: Energy Administration 「能源產品單位熱值表」 (from 2024, 2025 revision)";

    /// <summary>
    /// Published Taiwan factors a new company starts with. Electricity: MOEA Energy Administration annual factors 2014–2024
    /// and the 2025 industrial factor. Fuels: MOENV stationary combustion factors with the Energy Administration's net heat values.
    /// </summary>
    public static IReadOnlyList<SaveFactorRequest> TaiwanDefaults()
    {
        var grid = new (short Year, decimal Kg)[]
        {
            (2014, 0.518m), (2015, 0.525m), (2016, 0.530m), (2017, 0.554m), (2018, 0.533m), (2019, 0.509m), (2020, 0.502m),
            (2021, 0.509m), (2022, 0.495m), (2023, 0.494m), (2024, 0.474m),
        };
        var list = grid.Select(g => new SaveFactorRequest("electricity", $"Taiwan grid electricity {g.Year}", 2, "Location", "TW", g.Year, "kWh",
            null, null, null, null, g.Kg, "AR5", $"{MoeaElectricity} {g.Year}", true)).ToList();
        list.Add(new SaveFactorRequest("electricity", "Taiwan grid electricity 2025 (industrial)", 2, "Location", "TW", 2025, "kWh",
            null, null, null, null, 0.466m, "AR5", $"{MoeaElectricity} 2025, industrial factor for enterprise inventories (announced 2026)", true));

        SaveFactorRequest Fuel(string source, string name, string unit, decimal kcal, decimal co2, decimal ch4, decimal n2o) =>
            new(source, name, 1, "Combustion", "TW", 2024, unit, kcal, co2, ch4, n2o, null, "AR5", MoenvFuel, true);
        list.Add(Fuel("natural_gas", "Natural gas, pipeline NG2", "Nm3", 8755, 56100, 1, 0.1m));
        list.Add(Fuel("diesel", "Diesel (gas/diesel oil)", "L", 8629, 74100, 3, 0.6m));
        list.Add(Fuel("gasoline", "Motor gasoline", "L", 7520, 69300, 3, 0.6m));
        list.Add(Fuel("lpg", "Liquefied petroleum gas", "L", 5958, 63100, 1, 0.1m));
        list.Add(Fuel("fuel_oil", "Residual fuel oil", "L", 9320, 77400, 3, 0.6m));
        return list;
    }

    private const string Desnz = "UK DESNZ/Defra, Government GHG conversion factors for company reporting 2025 (flat file, published June 2025)";

    /// <summary>
    /// Published grid factors for other countries, added switched off: a company turns on the region it is in
    /// (Emission factors, "Use region"). Location-based, kg CO2e (or CO2 where the source gives only CO2) per kWh.
    /// </summary>
    public static IReadOnlyList<SaveFactorRequest> RegionalGridFactors() =>
    [
        new("electricity", "UK grid electricity 2025 (generated)", 2, "Location", "UK", 2025, "kWh", null, null, null, null, 0.177m, "AR5",
            $"{Desnz}: UK electricity, electricity generated, kg CO2e/kWh", false),
        new("electricity", "US grid electricity 2023 (national average)", 2, "Location", "US", 2023, "kWh", null, null, null, null, 0.349674m, "AR5",
            "US EPA eGRID2023 revision 2 (June 2025), U.S. total output CO2e rate 770.9 lb/MWh = 0.349674 kg/kWh; use the eGRID subregion when known", false),
        new("electricity", "India grid electricity FY 2022-23 (CO2)", 2, "Location", "IN", 2022, "kWh", null, null, null, null, 0.716m, "AR5",
            "Central Electricity Authority, CO2 Baseline Database for the Indian Power Sector, weighted average incl. RE, FY 2022-23 (t CO2/MWh, CO2 only)", false),
        new("electricity", "India grid electricity FY 2023-24 (CO2)", 2, "Location", "IN", 2023, "kWh", null, null, null, null, 0.727m, "AR5",
            "Central Electricity Authority, CO2 Baseline Database version 20.0 (Dec 2024), weighted average incl. RE, FY 2023-24 (t CO2/MWh, CO2 only; later versions may revise it)", false),
    ];

    /// <summary>
    /// Factors that are not energy, the same in every country: refrigerant and SF6 losses (AR5 100-year GWPs, as DESNZ 2025 and
    /// Taiwan's MOENV use), process CO2 from carbonates (IPCC 2006), and Scope 3 activity factors (DESNZ 2025, UK-based averages
    /// that are commonly used where no local factor exists).
    /// </summary>
    public static IReadOnlyList<SaveFactorRequest> ActivityDefaults()
    {
        SaveFactorRequest Gas(string code, string name, decimal gwp) =>
            new(code, name, 1, "Fugitive", "GLOBAL", 2025, "kg", null, null, null, null, gwp, "AR5", $"{Desnz}: Refrigerant & other, kg CO2e per kg released", true);
        SaveFactorRequest S3(string code, string name, byte cat, string unit, decimal kg, string what) =>
            new(code, name, 3, "Activity", "UK", 2025, unit, null, null, null, null, kg, "AR5", $"{Desnz}: {what}", true, cat);
        return
        [
            Gas("ref_r134a", "HFC-134a (R-134a)", 1300), Gas("ref_r32", "HFC-32 (R-32)", 677), Gas("ref_r125", "HFC-125 (R-125)", 3170),
            Gas("ref_r143a", "HFC-143a (R-143a)", 4800), Gas("ref_r410a", "R-410A blend", 1924), Gas("ref_r404a", "R-404A blend", 3943),
            Gas("ref_r407c", "R-407C blend", 1624), Gas("ref_r290", "R-290 propane", 0.06m), Gas("ref_r1234yf", "R-1234yf", 1), Gas("ref_r1234ze", "R-1234ze", 1),
            Gas("ref_r744", "R-744 carbon dioxide", 1), Gas("ref_sf6", "Sulphur hexafluoride (SF6, switchgear)", 23500),
            new("proc_calcite", "Calcination of limestone (CaCO3)", 1, "Process", "GLOBAL", 2006, "kg", null, null, null, null, 0.43971m, "AR5",
                "IPCC 2006 Guidelines Vol. 3 ch. 2 Table 2.1: 0.43971 t CO2 per t calcite", true),
            new("proc_dolomite", "Calcination of dolomite (CaMg(CO3)2)", 1, "Process", "GLOBAL", 2006, "kg", null, null, null, null, 0.47732m, "AR5",
                "IPCC 2006 Guidelines Vol. 3 ch. 2 Table 2.1: 0.47732 t CO2 per t dolomite", true),
            S3("s3_steel", "Steel (primary production)", 1, "t", 2863.90131m, "Material use, Metal: steel cans, primary material production, per tonne"),
            S3("s3_aluminium", "Aluminium (primary production)", 1, "t", 9115.90131m, "Material use, Metal: aluminium cans and foil, primary material production, per tonne"),
            S3("s3_plastics", "Plastics, average (primary production)", 1, "t", 3172.49932m, "Material use, Plastics: average plastics, primary material production, per tonne"),
            S3("s3_board", "Paper and board (primary production)", 1, "t", 1199.72542m, "Material use, Paper and board: board, primary material production, per tonne"),
            S3("s3_water_supply", "Water supply", 1, "m3", 0.1913m, "Water supply, per cubic metre"),
            S3("s3_td_losses", "Grid transmission and distribution losses (UK)", 3, "kWh", 0.01853m, "T&D - UK electricity, per kWh"),
            S3("s3_wtt_electricity", "Well-to-tank, grid electricity (UK)", 3, "kWh", 0.0459m, "WTT - UK electricity (generation), per kWh"),
            S3("s3_freight_hgv", "Road freight, HGV average laden", 4, "tkm", 0.10163m, "Freighting goods, HGV (all diesel), all HGVs, average laden, per tonne.km"),
            S3("s3_freight_van", "Road freight, van up to 3.5 t", 4, "tkm", 0.6313m, "Freighting goods, vans average (up to 3.5 tonnes), diesel, per tonne.km"),
            S3("s3_freight_ship", "Sea freight, container ship average", 4, "tkm", 0.01612m, "Freighting goods, cargo ship, container ship average, per tonne.km"),
            S3("s3_freight_air", "Air freight, international", 4, "tkm", 0.89939m, "Freighting goods, freight flights, international to/from non-UK, with RF, per tonne.km"),
            S3("s3_freight_rail", "Rail freight", 4, "tkm", 0.02779m, "Freighting goods, rail, freight train, per tonne.km"),
            S3("s3_waste_landfill", "Commercial and industrial waste to landfill", 5, "t", 520.5327m, "Waste disposal, commercial and industrial waste, landfill, per tonne"),
            S3("s3_waste_incineration", "Commercial and industrial waste, incineration with energy recovery", 5, "t", 4.68568m,
                "Waste disposal, commercial and industrial waste, incineration with energy recovery, per tonne"),
            S3("s3_water_treatment", "Waste water treatment", 5, "m3", 0.17088m, "Water treatment, per cubic metre"),
            S3("s3_travel_air_short", "Flights, short-haul economy", 6, "pkm", 0.12576m, "Business travel - air, short-haul, economy, with RF, per passenger.km"),
            S3("s3_travel_air_long", "Flights, international economy", 6, "pkm", 0.10916m, "Business travel - air, international to/from non-UK, economy, with RF, per passenger.km"),
            S3("s3_travel_rail", "National rail", 6, "pkm", 0.03546m, "Business travel - land, national rail, per passenger.km"),
            S3("s3_travel_car", "Car, average (business travel)", 6, "km", 0.16725m, "Business travel - land, cars (by size), average car, unknown fuel, per km"),
            S3("s3_commute_car", "Car, average (employee commuting)", 7, "km", 0.16725m, "Business travel - land, cars (by size), average car, unknown fuel, per km"),
        ];
    }

    /// <summary>The grid region in use: the region of the active location-based electricity factors (TW when none).</summary>
    public static string GridRegion(IEnumerable<EmissionFactor> factors) =>
        factors.Where(f => f.Source == "electricity" && f.Method == "Location" && f.IsActive).GroupBy(f => f.Region)
            .MaxBy(g => g.Count())?.Key ?? "TW";
}


/// <summary>Ordinary least squares for one relevant variable, as ISO 50006 uses for model-based EnPIs: E = a + b·x.</summary>
public static class Regression
{
    public static (double Intercept, double Slope, double R2) Fit(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var n = x.Count;
        if (n < 2 || y.Count != n)
        {
            throw new ArgumentException("Need at least two points.");
        }
        var mx = x.Average();
        var my = y.Average();
        double sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < n; i++)
        {
            sxy += (x[i] - mx) * (y[i] - my);
            sxx += (x[i] - mx) * (x[i] - mx);
            syy += (y[i] - my) * (y[i] - my);
        }
        if (sxx == 0)
        {
            return (my, 0, 0);   // the variable never changed: the model is the mean
        }
        var b = sxy / sxx;
        var a = my - b * mx;
        var r2 = syy == 0 ? 1 : sxy * sxy / (sxx * syy);
        return (a, b, r2);
    }
}
