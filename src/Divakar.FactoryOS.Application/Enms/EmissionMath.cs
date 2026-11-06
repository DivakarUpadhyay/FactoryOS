namespace Divakar.FactoryOS.Application.Enms;

/// <summary>One emission factor version (the library behind Carbon / ESG and the ISO 50001 energy review).</summary>
public sealed record EmissionFactor(Guid FactorId, string Source, string Name, byte Scope, string Method, string Region, short Year, string Unit,
    decimal? HeatKcalPerUnit, decimal? Co2KgPerTj, decimal? Ch4KgPerTj, decimal? N2oKgPerTj, decimal? KgCo2ePerUnit, string GwpSet,
    string Reference, bool IsActive)
{
    /// <summary>kg CO2e per unit: the published total, else built from the per-TJ factors, the heat value and the GWP set.</summary>
    public decimal Co2e => EmissionMath.KgCo2ePerUnit(this);
}

public sealed record SaveFactorRequest(string Source, string Name, byte Scope, string Method, string Region, short Year, string Unit,
    decimal? HeatKcalPerUnit, decimal? Co2KgPerTj, decimal? Ch4KgPerTj, decimal? N2oKgPerTj, decimal? KgCo2ePerUnit, string? GwpSet,
    string Reference, bool IsActive);

public static class EmissionMath
{
    /// <summary>1 kcal = 4.1868 × 10⁻⁹ TJ (the conversion the Ministry of Environment's factor table states).</summary>
    public const decimal TjPerKcal = 4.1868e-9m;

    /// <summary>kcal in one kWh, used to put fuels and electricity on one energy scale (Energy Administration heat table).</summary>
    public const double KcalPerKwh = 860;

    public static readonly string[] Sources = ["electricity", "natural_gas", "diesel", "gasoline", "lpg", "fuel_oil", "steam"];

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
