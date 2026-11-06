using Divakar.FactoryOS.Application.Enms;

namespace Divakar.FactoryOS.UnitTests;

public sealed class EnmsTests
{
    private static EmissionFactor Factor(SaveFactorRequest r, Guid? id = null) => new(id ?? Guid.NewGuid(), r.Source, r.Name, r.Scope, r.Method, r.Region,
        r.Year, r.Unit, r.HeatKcalPerUnit, r.Co2KgPerTj, r.Ch4KgPerTj, r.N2oKgPerTj, r.KgCo2ePerUnit, r.GwpSet ?? "AR5", r.Reference, r.IsActive);

    private static readonly IReadOnlyList<EmissionFactor> Library = [.. EmissionMath.TaiwanDefaults().Select(r => Factor(r))];

    [Fact]
    public void Diesel_factor_is_built_from_the_published_per_TJ_factors_heat_value_and_AR5_GWPs()
    {
        // 8,629 kcal/L × 4.1868e-9 TJ/kcal × (74,100 + 3 × 28 + 0.6 × 265) kg/TJ
        var diesel = EmissionMath.Resolve(Library, "diesel", "Combustion", 2026)!;
        var tj = 8629 * 4.1868e-9;
        Assert.Equal(tj * (74100 + 84 + 159), (double)diesel.Co2e, 5);
        Assert.InRange((double)diesel.Co2e, 2.685, 2.687);
    }

    [Fact]
    public void Natural_gas_factor_uses_the_NG2_heat_value()
    {
        var gas = EmissionMath.Resolve(Library, "natural_gas", "Combustion", 2026)!;
        Assert.Equal("Nm3", gas.Unit);
        Assert.InRange((double)gas.Co2e, 2.057, 2.060);
    }

    [Fact]
    public void AR6_gives_a_higher_factor_than_AR5_for_the_same_fuel()
    {
        var ar5 = EmissionMath.Resolve(Library, "diesel", "Combustion", 2026)!;
        var ar6 = ar5 with { GwpSet = "AR6" };
        Assert.True(ar6.Co2e > ar5.Co2e);
    }

    [Theory]
    [InlineData(2024, 0.474)]
    [InlineData(2023, 0.494)]
    [InlineData(2025, 0.466)]
    [InlineData(2030, 0.466)]   // no newer factor yet: the latest published one applies
    [InlineData(2010, 0.518)]   // before the library starts: the earliest one
    public void Grid_factor_is_the_one_published_for_the_year(int year, double expected)
    {
        Assert.Equal(expected, (double)EmissionMath.Resolve(Library, "electricity", "Location", year)!.Co2e, 3);
    }

    [Fact]
    public void An_inactive_factor_is_never_used()
    {
        var lib = Library.Select(f => f.Source == "electricity" && f.Year == 2024 ? f with { IsActive = false } : f).ToList();
        Assert.Equal(0.494, (double)EmissionMath.Resolve(lib, "electricity", "Location", 2024)!.Co2e, 3);
    }

    [Fact]
    public void Entries_count_only_the_share_of_their_period_inside_the_window()
    {
        // a 30-day bill (1–30 Sep), report covers 16 Sep onwards: 15 of 30 days
        Assert.Equal(0.5, EmissionMath.Overlap(new DateTime(2026, 9, 1), new DateTime(2026, 9, 30), new DateTime(2026, 9, 16), new DateTime(2026, 12, 1)), 6);
        Assert.Equal(1, EmissionMath.Overlap(new DateTime(2026, 9, 1), new DateTime(2026, 9, 30), new DateTime(2026, 1, 1), new DateTime(2027, 1, 1)));
        Assert.Equal(0, EmissionMath.Overlap(new DateTime(2026, 9, 1), new DateTime(2026, 9, 30), new DateTime(2026, 10, 1), new DateTime(2026, 11, 1)));
    }

    [Fact]
    public void Regression_recovers_a_known_line_and_its_fit()
    {
        double[] parts = [100, 200, 300, 400, 500];
        var kwh = parts.Select(p => 1200 + 0.8 * p).ToArray();
        var (a, b, r2) = Regression.Fit(parts, kwh);
        Assert.Equal(1200, a, 6);
        Assert.Equal(0.8, b, 6);
        Assert.Equal(1, r2, 6);

        var noisy = kwh.Select((k, i) => k + (i % 2 == 0 ? 60 : -60)).ToArray();
        Assert.InRange(Regression.Fit(parts, noisy).R2, 0.5, 0.99);
    }

    private static Enpi Enpi(string kind) => new(Guid.NewGuid(), "Site kWh", null, null, kind, new DateTime(2026, 10, 1), new DateTime(2026, 10, 15),
        BaselineKwh: 14000, BaselineParts: 3500, BaselineDays: 14, Intercept: 600, Slope: 0.1, R2: 0.9, CreatedAt: DateTime.UtcNow);

    [Fact]
    public void Regression_EnPI_compares_actual_with_expected_energy_for_the_same_production()
    {
        // expected 600 + 0.1 × 4000 = 1000 kWh/day; the plant used 950 and 980
        var r = EnmsService.Evaluate(Enpi("Regression"), [(new DateTime(2026, 11, 1), 950, 4000), (new DateTime(2026, 11, 2), 980, 4000)],
            new DateTime(2026, 11, 1), new DateTime(2026, 11, 3));
        Assert.Equal(2000, r.ExpectedKwh, 1);
        Assert.Equal(70, r.SavingsKwh, 1);
        Assert.Equal(3.5, r.ImprovementPct, 1);
        Assert.Equal([50.0, 70.0], r.Days.Select(d => d.Cusum));
    }

    [Fact]
    public void Ratio_EnPI_normalises_by_parts_and_measured_EnPI_does_not()
    {
        var day = new List<(DateTime, double, double)> { (new DateTime(2026, 11, 1), 1000, 500) };
        var ratio = EnmsService.Evaluate(Enpi("Ratio"), day, day[0].Item1, day[0].Item1.AddDays(1));
        Assert.Equal(2000, ratio.ExpectedKwh, 1);   // 4 kWh/part × 500 parts
        var measured = EnmsService.Evaluate(Enpi("Measured"), day, day[0].Item1, day[0].Item1.AddDays(1));
        Assert.Equal(1000, measured.ExpectedKwh, 1);   // 14,000 kWh over 14 days
        Assert.Contains(measured.Notes, n => n.Contains("Not normalised"));
    }

    [Fact]
    public void A_weak_baseline_model_is_flagged()
    {
        var weak = Enpi("Regression") with { R2 = 0.4 };
        var r = EnmsService.Evaluate(weak, [(new DateTime(2026, 11, 1), 1000, 4000)], new DateTime(2026, 11, 1), new DateTime(2026, 11, 2));
        Assert.Contains(r.Notes, n => n.Contains("relevant variable"));
    }

    [Fact]
    public void A_model_where_energy_falls_with_production_is_flagged()
    {
        var odd = Enpi("Regression") with { Slope = -0.1, R2 = 0.95 };
        var r = EnmsService.Evaluate(odd, [(new DateTime(2026, 11, 1), 1000, 4000)], new DateTime(2026, 11, 1), new DateTime(2026, 11, 2));
        Assert.Contains(r.Notes, n => n.Contains("energy falls as production rises"));
    }
}
