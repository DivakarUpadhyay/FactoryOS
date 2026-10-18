using System.Globalization;

namespace Divakar.FactoryOS.Application.Reports;

/// <summary>One KPI measured in the baseline period and in the current period.</summary>
public sealed record KpiChange(string Area, string Kpi, string Unit, double? Baseline, double? Current, double? Change, double? ChangePct, string Verdict);

public static class TransformationMath
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Compares a KPI. Percent KPIs (unit "%") change in points; the rest also get a relative change.
    /// The verdict ignores changes under 1 % (or 0.5 points) as noise. Totals that grow with running time (kWh, parts, alarms)
    /// are shown as "context": only rates and ratios are judged, so a shorter or quieter period does not look like an improvement.
    /// </summary>
    public static KpiChange Compare(string area, string kpi, string unit, double? baseline, double? current, bool higherIsBetter, bool judge = true)
    {
        if (baseline is not { } b || current is not { } c || double.IsNaN(b) || double.IsNaN(c))
        {
            return new KpiChange(area, kpi, unit, Round(baseline), Round(current), null, null, "no data");
        }
        var change = c - b;
        double? pct = unit == "%" || Math.Abs(b) < 1e-9 ? null : change / Math.Abs(b) * 100;
        var noise = unit == "%" ? Math.Abs(change) < 0.5 : pct is { } p ? Math.Abs(p) < 1 : Math.Abs(change) < 1e-9;
        var verdict = !judge ? "context" : noise ? "same" : (change > 0) == higherIsBetter ? "better" : "worse";
        return new KpiChange(area, kpi, unit, Round(b), Round(c), Round(change), Round(pct), verdict);
    }

    private static double? Round(double? v) => v is { } x ? Math.Round(x, Math.Abs(x) >= 100 ? 0 : Math.Abs(x) >= 10 ? 1 : 3) : null;

    public static string Format(double? v, string unit) => v is null ? "—" : unit == "%" ? $"{v.Value.ToString("0.0", Inv)}%" : v.Value.ToString("#,0.###", Inv);

    public static string FormatChange(KpiChange k) => k.Change is not { } c ? "—"
        : k.Unit == "%" ? $"{(c >= 0 ? "+" : "")}{c.ToString("0.0", Inv)} pts"
        : k.ChangePct is { } p ? $"{(p >= 0 ? "+" : "")}{p.ToString("0.0", Inv)}%" : $"{(c >= 0 ? "+" : "")}{c.ToString("#,0.###", Inv)}";

    /// <summary>The default baseline is the same length of time just before the current period.</summary>
    public static (DateTime From, DateTime To) DefaultBaseline(DateTime from, DateTime to) => (from - (to - from), from);
}
