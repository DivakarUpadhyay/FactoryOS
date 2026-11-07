namespace Divakar.FactoryOS.Application.Ai;

/// <summary>
/// The OT-layer detectors, as plain functions over time series, so the cloud worker and the edge agent run the same code.
/// They are statistical models that learn each tag's own normal behaviour from its history (no training step, no cloud
/// service): robust seasonal profiles for energy, EWMA and CUSUM for drift, run-length for frozen sensors, a trend fit for
/// time to a limit. Each returns a score against a threshold that the operators' feedback tunes per tag.
/// </summary>
public static class OtModels
{
    public static double Median(IReadOnlyList<double> xs)
    {
        if (xs.Count == 0)
        {
            return 0;
        }
        var s = xs.Order().ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }

    /// <summary>Median absolute deviation scaled to σ for normal data (× 1.4826).</summary>
    public static double RobustSigma(IReadOnlyList<double> xs)
    {
        var m = Median(xs);
        return 1.4826 * Median([.. xs.Select(x => Math.Abs(x - m))]);
    }

    /// <summary>Hour of the week, 0 = Monday 00:00.</summary>
    public static int HourOfWeek(DateTime t) => ((int)t.DayOfWeek + 6) % 7 * 24 + t.Hour;

    /// <summary>
    /// What an hour normally looks like: the same hour of the week when there are at least three of them, else the same hour of
    /// the day, else every hour. Returns the median and a robust σ with a floor (5 % of the median, or the given floor).
    /// </summary>
    public static (double Median, double Sigma, int Samples, string Basis) Normal(IReadOnlyList<(DateTime Hour, double Value)> history, DateTime hour, double floor)
    {
        var week = history.Where(h => HourOfWeek(h.Hour) == HourOfWeek(hour)).Select(h => h.Value).ToList();
        var (values, basis) = week.Count >= 3 ? (week, "same hour of the week")
            : history.Count(h => h.Hour.Hour == hour.Hour) >= 5 ? (history.Where(h => h.Hour.Hour == hour.Hour).Select(h => h.Value).ToList(), "same hour of the day")
            : (history.Select(h => h.Value).ToList(), "all hours");
        var median = Median(values);
        return (median, Math.Max(RobustSigma(values), Math.Max(floor, Math.Abs(median) * 0.05)), values.Count, basis);
    }

    /// <summary>Robust z-score of a value against its normal.</summary>
    public static double Score(double value, double median, double sigma) => sigma <= 0 ? 0 : (value - median) / sigma;

    /// <summary>EWMA of a series (λ = 0.2 by default), and its control limit width in σ for the given L (steady-state).</summary>
    public static double Ewma(IReadOnlyList<double> xs, double start, double lambda = 0.2)
    {
        var z = start;
        foreach (var x in xs)
        {
            z = lambda * x + (1 - lambda) * z;
        }
        return z;
    }

    public static double EwmaLimit(double sigma, double lambda = 0.2, double l = 3) => l * sigma * Math.Sqrt(lambda / (2 - lambda));

    /// <summary>Upper and lower tabular CUSUM in σ units (k = 0.5σ); a value above h ≈ 5 means a sustained shift.</summary>
    public static (double High, double Low) Cusum(IReadOnlyList<double> xs, double mean, double sigma, double k = 0.5)
    {
        if (sigma <= 0)
        {
            return (0, 0);
        }
        double hi = 0, lo = 0;
        foreach (var x in xs)
        {
            var z = (x - mean) / sigma;
            hi = Math.Max(0, hi + z - k);
            lo = Math.Max(0, lo - z - k);
        }
        return (hi, lo);
    }

    /// <summary>
    /// A frozen sensor: the last readings are all identical while the tag normally moves (its baseline σ is clearly above the
    /// resolution). A stuck transmitter or a PLC holding its last value looks exactly like this.
    /// </summary>
    public static bool IsFrozen(IReadOnlyList<double> recent, double baselineSigma, int minRun = 12) =>
        recent.Count >= minRun && baselineSigma > 1e-6 && recent.Max() - recent.Min() < Math.Max(1e-9, baselineSigma * 1e-3);

    /// <summary>Least-squares trend of (hours, value): slope per hour, t statistic of the slope, R².</summary>
    public static (double Slope, double Intercept, double TStat, double R2) Trend(IReadOnlyList<(double X, double Y)> pts)
    {
        var n = pts.Count;
        if (n < 4)
        {
            return (0, 0, 0, 0);
        }
        var mx = pts.Average(p => p.X);
        var my = pts.Average(p => p.Y);
        double sxx = 0, sxy = 0, syy = 0;
        foreach (var (x, y) in pts)
        {
            sxx += (x - mx) * (x - mx);
            sxy += (x - mx) * (y - my);
            syy += (y - my) * (y - my);
        }
        if (sxx == 0)
        {
            return (0, my, 0, 0);
        }
        var b = sxy / sxx;
        var a = my - b * mx;
        var sse = Math.Max(0, syy - b * sxy);
        var se = Math.Sqrt(sse / (n - 2) / sxx);
        var r2 = syy == 0 ? 0 : 1 - sse / syy;
        return (b, a, se == 0 ? (b == 0 ? 0 : double.PositiveInfinity) : b / se, r2);
    }

    /// <summary>
    /// Hours until the trend reaches the limit, when the rise is real (t ≥ 3, R² ≥ 0.5) and the crossing is ahead; null otherwise.
    /// </summary>
    public static double? HoursToLimit(IReadOnlyList<(double X, double Y)> pts, double limit, double now)
    {
        var (slope, intercept, t, r2) = Trend(pts);
        if (slope <= 0 || t < 3 || r2 < 0.5)
        {
            return null;
        }
        var current = intercept + slope * now;
        if (current >= limit)
        {
            return 0;
        }
        return (limit - current) / slope;
    }

    /// <summary>A threshold scaled by operator feedback (sensitivity 1 = default; false alarms raise it, confirmed ones lower it).</summary>
    public static double Tuned(double threshold, double sensitivity) => threshold * Math.Clamp(sensitivity, 0.5, 3);

    /// <summary>New sensitivity after feedback: a false alarm raises the threshold by 25 % (to at most 3×), a real one lowers it 5 % (to 0.7×).</summary>
    public static double AfterFeedback(double sensitivity, bool real) => real ? Math.Max(0.7, sensitivity * 0.95) : Math.Min(3, sensitivity * 1.25);
}

/// <summary>
/// A streaming detector for one tag, for the edge agent: learns mean and variance with a slow exponential window, scores each
/// new value, flags spikes (robust jump against the learnt σ), slow drift (CUSUM) and frozen values (run length).
/// </summary>
public sealed class OnlineDetector(double alpha = 0.01, double spikeZ = 6, double cusumH = 8, int frozenRun = 30)
{
    private double mean, variance;
    private long count;
    private double cusumHi, cusumLo;
    private double? last;
    private int sameRun;

    public long Count => count;
    public double Mean => mean;
    public double Sigma => Math.Sqrt(variance);

    public sealed record Finding(string Kind, double Score, string Detail);

    /// <summary>Feeds one value; returns what it found (empty while still learning, the first 60 values).</summary>
    public IReadOnlyList<Finding> Add(double x, double sensitivity = 1)
    {
        var found = new List<Finding>();
        if (!double.IsFinite(x))
        {
            return found;
        }
        sameRun = last is { } l && l == x ? sameRun + 1 : 0;
        last = x;
        if (count == 0)
        {
            mean = x;
        }
        var sigma = Math.Sqrt(variance);
        if (count >= 60 && sigma > 1e-9)
        {
            var z = (x - mean) / sigma;
            if (Math.Abs(z) > OtModels.Tuned(spikeZ, sensitivity))
            {
                found.Add(new Finding("Spike", Math.Abs(z), $"value {x:0.###} is {z:+0.0;-0.0}σ from its learnt normal {mean:0.###}"));
            }
            cusumHi = Math.Max(0, cusumHi + z - 0.5);
            cusumLo = Math.Max(0, cusumLo - z - 0.5);
            var h = OtModels.Tuned(cusumH, sensitivity);
            if (cusumHi > h || cusumLo > h)
            {
                found.Add(new Finding("Drift", Math.Max(cusumHi, cusumLo), $"sustained {(cusumHi > h ? "rise" : "fall")} away from {mean:0.###}"));
                cusumHi = cusumLo = 0;
            }
            if (sameRun == frozenRun)
            {
                found.Add(new Finding("Frozen", sameRun, $"the same value {x:0.###} {sameRun + 1} times in a row while the tag normally varies (σ {sigma:0.###})"));
            }
        }
        // a spike is not allowed to teach the model that it is normal
        if (found.All(f => f.Kind != "Spike"))
        {
            var d = x - mean;
            var a = count < 60 ? 1.0 / (count + 1) : alpha;
            mean += a * d;
            variance = (1 - a) * (variance + a * d * d);
        }
        count++;
        return found;
    }
}
