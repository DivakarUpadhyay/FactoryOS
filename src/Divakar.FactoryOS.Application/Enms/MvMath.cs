namespace Divakar.FactoryOS.Application.Enms;

/// <summary>Baseline model fit statistics in the form ASHRAE Guideline 14 judges them.</summary>
public sealed record ModelFit(int N, int Parameters, double MeanKwh, double CvRmsePct, double NmbePct, double Autocorrelation, double EffectiveN);

/// <summary>Savings and their uncertainty for one reporting period (ASHRAE Guideline 14 Annex B, IPMVP uncertainty assessment).</summary>
public sealed record SavingsUncertainty(int ReportingDays, double AdjustedBaselineKwh, double ActualKwh, double SavingsKwh, double SavingsFraction,
    double ConfidencePct, double TValue, double ModelUncertaintyKwh, double MeterUncertaintyKwh, double CombinedUncertaintyKwh,
    double? FractionalUncertaintyPct, double? Fsu68Pct);

/// <summary>
/// Measurement and verification arithmetic. CV(RMSE) and NMBE use the residuals of the baseline model with n − p degrees of
/// freedom; the fractional savings uncertainty is ASHRAE 14's ΔS/S = t · 1.26 · CV · √((n/n′)(1 + 2/n′)(1/m)) / F, with
/// n′ = n(1 − ρ)/(1 + ρ) for lag-1 autocorrelated residuals. Meter uncertainty is added in quadrature.
/// </summary>
public static class MvMath
{
    /// <summary>ASHRAE 14 calibration limits: monthly data CV(RMSE) ≤ 15 %, NMBE ±5 %; hourly data CV(RMSE) ≤ 30 %, NMBE ±10 %.</summary>
    public const double MonthlyCvLimit = 15, MonthlyNmbeLimit = 5, HourlyCvLimit = 30, HourlyNmbeLimit = 10;

    /// <summary>ASHRAE 14: savings are reportable when the fractional savings uncertainty is under 50 % at 68 % confidence.</summary>
    public const double FsuLimitPct = 50;

    public static ModelFit Fit(IReadOnlyList<double> actual, IReadOnlyList<double> predicted, int parameters)
    {
        var n = actual.Count;
        if (n < 3 || predicted.Count != n)
        {
            throw new ArgumentException("Need at least three baseline points.");
        }
        var mean = actual.Average();
        var residuals = actual.Zip(predicted, (y, p) => y - p).ToArray();
        var dof = Math.Max(1, n - parameters);
        var rmse = Math.Sqrt(residuals.Sum(e => e * e) / dof);
        var cv = mean == 0 ? 0 : rmse / mean * 100;
        var nmbe = mean == 0 ? 0 : residuals.Sum() / (dof * mean) * 100;
        var rho = LagOneAutocorrelation(residuals);
        var nEff = Math.Max(2, n * (1 - rho) / (1 + rho));
        return new ModelFit(n, parameters, Math.Round(mean, 2), Math.Round(cv, 2), Math.Round(nmbe, 2), Math.Round(rho, 3), Math.Round(nEff, 1));
    }

    public static double LagOneAutocorrelation(IReadOnlyList<double> e)
    {
        if (e.Count < 3)
        {
            return 0;
        }
        var m = e.Average();
        double num = 0, den = 0;
        for (var i = 0; i < e.Count; i++)
        {
            den += (e[i] - m) * (e[i] - m);
            if (i > 0)
            {
                num += (e[i] - m) * (e[i - 1] - m);
            }
        }
        // a negative ρ would inflate n′ above n; ASHRAE only corrects for positive autocorrelation
        return den == 0 ? 0 : Math.Clamp(num / den, 0, 0.95);
    }

    public static SavingsUncertainty Savings(ModelFit fit, double adjustedBaselineKwh, double actualKwh, int reportingDays, double confidencePct,
        double meterUncertaintyPct)
    {
        var savings = adjustedBaselineKwh - actualKwh;
        var fraction = adjustedBaselineKwh > 0 ? savings / adjustedBaselineKwh : 0;
        var m = Math.Max(1, reportingDays);
        var dof = Math.Max(1, fit.N - fit.Parameters);
        var t = StudentT(confidencePct, dof);
        var shape = 1.26 * (fit.CvRmsePct / 100) * Math.Sqrt(fit.N / fit.EffectiveN * (1 + 2 / fit.EffectiveN) * (1.0 / m));
        // ΔS = t · shape · E_baseline (the fraction form multiplied by the savings, F cancels)
        var modelKwh = t * shape * adjustedBaselineKwh;
        // a meter's stated uncertainty is taken as expanded at 95 % (k ≈ 1.96) and rescaled to the confidence asked for
        var meterStd = actualKwh * meterUncertaintyPct / 100 / 1.96;
        var meterKwh = meterStd * Normal(p: 0.5 + confidencePct / 200);
        var combined = Math.Sqrt(modelKwh * modelKwh + meterKwh * meterKwh);
        double? fractional = savings > 0 ? combined / savings * 100 : null;
        var t68 = StudentT(68, dof);
        double? fsu68 = savings > 0 ? Math.Sqrt(Math.Pow(t68 * shape * adjustedBaselineKwh, 2) + Math.Pow(meterStd * Normal(0.84), 2)) / savings * 100 : null;
        return new SavingsUncertainty(m, Math.Round(adjustedBaselineKwh, 1), Math.Round(actualKwh, 1), Math.Round(savings, 1), Math.Round(fraction * 100, 2),
            confidencePct, Math.Round(t, 3), Math.Round(modelKwh, 1), Math.Round(meterKwh, 1), Math.Round(combined, 1),
            fractional is { } f ? Math.Round(f, 1) : null, fsu68 is { } g ? Math.Round(g, 1) : null);
    }

    /// <summary>Two-sided Student t value for a confidence level (e.g. 90 → t at 0.95) and degrees of freedom (Cornish–Fisher expansion).</summary>
    public static double StudentT(double confidencePct, int dof)
    {
        var p = 0.5 + confidencePct / 200;
        var z = Normal(p);
        double v = Math.Max(1, dof), z2 = z * z;
        var g1 = (z2 + 1) * z / 4;
        var g2 = ((5 * z2 + 16) * z2 + 3) * z / 96;
        var g3 = (((3 * z2 + 19) * z2 + 17) * z2 - 15) * z / 384;
        var g4 = ((((79 * z2 + 776) * z2 + 1482) * z2 - 1920) * z2 - 945) * z / 92160;
        return z + g1 / v + g2 / (v * v) + g3 / (v * v * v) + g4 / (v * v * v * v);
    }

    /// <summary>Standard normal quantile (Acklam's rational approximation, relative error below 1.2 × 10⁻⁹).</summary>
    public static double Normal(double p)
    {
        if (p is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(p));
        }
        double[] a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
        double[] b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01];
        double[] c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
        double[] d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00];
        const double low = 0.02425;
        if (p < low)
        {
            var q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p > 1 - low)
        {
            var q = Math.Sqrt(-2 * Math.Log(1 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        var r = p - 0.5;
        var s = r * r;
        return (((((a[0] * s + a[1]) * s + a[2]) * s + a[3]) * s + a[4]) * s + a[5]) * r / (((((b[0] * s + b[1]) * s + b[2]) * s + b[3]) * s + b[4]) * s + 1);
    }
}
