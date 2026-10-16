namespace Divakar.FactoryOS.Application.QualityControl;

public sealed record SpcPoint(long SubgroupNo, DateTime Ts, double Mean, double Range, int Size, string? LotNo, IReadOnlyList<string> Violations);

public sealed record SpcLimits(double Centre, double Ucl, double Lcl, double RangeCentre, double RangeUcl, double RangeLcl, double Sigma);

public sealed record Capability(double? Cp, double? Cpk, double? Pp, double? Ppk, double Mean, double SigmaWithin, double SigmaOverall, double PctOutOfSpec);

public sealed record SpcChart(string ChartType, int SubgroupSize, SpcLimits? Limits, Capability? Capability, IReadOnlyList<SpcPoint> Points,
    int Violations, string Verdict);

/// <summary>
/// Statistical process control: X̄-R charts for subgroups of 2–10, individuals and moving range (I-MR) for single values,
/// with the classic constants, process capability and the Western Electric run rules.
/// </summary>
public static class Spc
{
    // Shewhart constants by subgroup size n (index = n)
    private static readonly double[] A2 = [0, 2.660, 1.880, 1.023, 0.729, 0.577, 0.483, 0.419, 0.373, 0.337, 0.308];
    private static readonly double[] D3 = [0, 0, 0, 0, 0, 0, 0, 0.076, 0.136, 0.184, 0.223];
    private static readonly double[] D4 = [0, 3.267, 3.267, 2.574, 2.282, 2.114, 2.004, 1.924, 1.864, 1.816, 1.777];
    private static readonly double[] D2 = [0, 1.128, 1.128, 1.693, 2.059, 2.326, 2.534, 2.704, 2.847, 2.970, 3.078];

    public sealed record Sample(long SubgroupNo, DateTime Ts, double Value, string? LotNo);

    public static SpcChart Build(IReadOnlyList<Sample> samples, int subgroupSize, double? lsl, double? usl)
    {
        if (samples.Count == 0)
        {
            return new SpcChart(subgroupSize == 1 ? "I-MR" : "Xbar-R", subgroupSize, null, null, [], 0, "No measurements yet");
        }
        List<(long No, DateTime Ts, double Mean, double Range, int Size, string? Lot)> groups;
        if (subgroupSize == 1)
        {
            var ordered = samples.OrderBy(s => s.Ts).ThenBy(s => s.SubgroupNo).ToList();
            groups = [.. ordered.Select((s, i) => (s.SubgroupNo, s.Ts, s.Value, i == 0 ? double.NaN : Math.Abs(s.Value - ordered[i - 1].Value), 1, s.LotNo))];
        }
        else
        {
            groups = [.. samples.GroupBy(s => s.SubgroupNo).Select(g => (g.Key, g.Min(x => x.Ts), g.Average(x => x.Value),
                g.Max(x => x.Value) - g.Min(x => x.Value), g.Count(), g.Select(x => x.LotNo).FirstOrDefault(l => l is not null))).OrderBy(g => g.Item2)];
        }

        var n = Math.Clamp(subgroupSize, 1, 10);
        var ranges = groups.Where(g => !double.IsNaN(g.Range) && (n == 1 || g.Size >= 2)).Select(g => g.Range).ToList();
        var centre = groups.Average(g => g.Mean);
        var rBar = ranges.Count > 0 ? ranges.Average() : 0;
        var sigmaWithin = rBar / D2[n];
        SpcLimits limits = n == 1
            ? new(centre, centre + 2.66 * rBar, centre - 2.66 * rBar, rBar, 3.267 * rBar, 0, sigmaWithin)
            : new(centre, centre + A2[n] * rBar, centre - A2[n] * rBar, rBar, D4[n] * rBar, D3[n] * rBar, sigmaWithin);

        // sigma of the plotted statistic, for the zone rules
        var s = n == 1 ? sigmaWithin : sigmaWithin / Math.Sqrt(n);
        var violations = Rules(groups.Select(g => g.Mean).ToList(), centre, s, limits);
        var points = groups.Select((g, i) => new SpcPoint(g.No, g.Ts, Math.Round(g.Mean, 5), double.IsNaN(g.Range) ? 0 : Math.Round(g.Range, 5), g.Size, g.Lot,
            violations[i])).ToList();

        var values = samples.Select(x => x.Value).ToList();
        var mean = values.Average();
        var sigmaOverall = values.Count > 1 ? Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1)) : 0;
        var outOfSpec = values.Count(v => v < (lsl ?? double.MinValue) || v > (usl ?? double.MaxValue)) * 100.0 / values.Count;
        double? Index(double sigma, Func<double, double> f) => sigma > 0 ? Math.Round(f(sigma), 2) : null;
        Capability? cap = lsl is null && usl is null ? null : new Capability(
            lsl is { } l1 && usl is { } u1 ? Index(sigmaWithin, sg => (u1 - l1) / (6 * sg)) : null,
            Index(sigmaWithin, sg => Math.Min(usl is { } u ? (u - mean) / (3 * sg) : double.MaxValue, lsl is { } l ? (mean - l) / (3 * sg) : double.MaxValue)),
            lsl is { } l2 && usl is { } u2 ? Index(sigmaOverall, sg => (u2 - l2) / (6 * sg)) : null,
            Index(sigmaOverall, sg => Math.Min(usl is { } u ? (u - mean) / (3 * sg) : double.MaxValue, lsl is { } l ? (mean - l) / (3 * sg) : double.MaxValue)),
            Math.Round(mean, 5), Math.Round(sigmaWithin, 5), Math.Round(sigmaOverall, 5), Math.Round(outOfSpec, 2));

        var flagged = points.Count(p => p.Violations.Count > 0);
        // the verdict is about the process now: the last 25 points (older signals stay marked on the chart)
        var recentFlags = points.TakeLast(25).Count(p => p.Violations.Count > 0);
        var verdict = groups.Count < 5 ? "Too few subgroups for reliable limits (need at least 20)"
            : recentFlags > 0 ? $"Out of control: {recentFlags} of the last {Math.Min(25, points.Count)} points break the run rules"
            : cap?.Cpk is { } cpk ? cpk >= 1.33 ? $"In control and capable (Cpk {cpk})" : cpk >= 1 ? $"In control, marginal capability (Cpk {cpk})" : $"In control but not capable (Cpk {cpk}) — the process spreads beyond the limits"
            : "In control";
        return new SpcChart(n == 1 ? "I-MR" : "Xbar-R", n, limits, cap, points, flagged, verdict);
    }

    /// <summary>Western Electric rules on the plotted means: beyond 3σ, 2 of 3 beyond 2σ, 4 of 5 beyond 1σ, 8 in a row on one side, 6 in a row trending.</summary>
    public static List<List<string>> Rules(IReadOnlyList<double> x, double centre, double sigma, SpcLimits limits)
    {
        var result = x.Select(_ => new List<string>()).ToList();
        if (sigma <= 0)
        {
            return result;
        }
        int Side(double v) => v > centre ? 1 : v < centre ? -1 : 0;
        for (var i = 0; i < x.Count; i++)
        {
            if (x[i] > limits.Ucl || x[i] < limits.Lcl)
            {
                result[i].Add("Beyond control limit");
            }
            if (i >= 2)
            {
                var w = Enumerable.Range(i - 2, 3).ToList();
                foreach (var side in new[] { 1, -1 })
                {
                    if (w.Count(j => Side(x[j]) == side && Math.Abs(x[j] - centre) > 2 * sigma) >= 2 && Side(x[i]) == side && Math.Abs(x[i] - centre) > 2 * sigma)
                    {
                        result[i].Add("2 of 3 beyond 2σ");
                    }
                }
            }
            if (i >= 4)
            {
                var w = Enumerable.Range(i - 4, 5).ToList();
                foreach (var side in new[] { 1, -1 })
                {
                    if (w.Count(j => Side(x[j]) == side && Math.Abs(x[j] - centre) > sigma) >= 4 && Side(x[i]) == side && Math.Abs(x[i] - centre) > sigma)
                    {
                        result[i].Add("4 of 5 beyond 1σ");
                    }
                }
            }
            if (i >= 7 && Enumerable.Range(i - 7, 8).All(j => Side(x[j]) == Side(x[i]) && Side(x[i]) != 0))
            {
                result[i].Add("8 in a row on one side");
            }
            if (i >= 5)
            {
                var up = Enumerable.Range(i - 4, 5).All(j => x[j] > x[j - 1]);
                var down = Enumerable.Range(i - 4, 5).All(j => x[j] < x[j - 1]);
                if (up || down)
                {
                    result[i].Add("6 in a row trending");
                }
            }
        }
        return result;
    }
}
