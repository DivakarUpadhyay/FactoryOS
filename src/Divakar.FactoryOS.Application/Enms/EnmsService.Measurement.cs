using Divakar.FactoryOS.Application.Common;

namespace Divakar.FactoryOS.Application.Enms;

/// <summary>Meter register and calibration records (ISO 50001 9.1), and measurement and verification of savings (ISO 50015, IPMVP).</summary>
public sealed partial class EnmsService
{
    public static readonly string[] MeterMeasures = ["electricity", "gas", "water", "air", "steam", "fuel", "temperature", "production", "other"];
    public static readonly string[] MeterKinds = ["Main", "Sub", "Portable", "Virtual"];
    public static readonly string[] CalibrationResults = ["Pass", "Adjusted", "Fail"];
    public static readonly string[] MvOptions = ["A", "B", "C", "D"];

    /// <summary>Calibration state on a date: overdue after the interval, due within 30 days of it.</summary>
    public static MeterStatus Status(Meter m, DateTime today)
    {
        if (m.IntervalMonths is not > 0)
        {
            return new(m, null, m.Kind == "Virtual" ? "virtual" : "untracked");
        }
        if (m.LastCalibratedOn is not { } last)
        {
            return new(m, null, "never");
        }
        var due = last.Date.AddMonths(m.IntervalMonths.Value);
        var state = m.LastResult == "Fail" ? "failed" : due < today ? "overdue" : due <= today.AddDays(30) ? "due" : "ok";
        return new(m, due, state);
    }

    public async Task<IReadOnlyList<MeterStatus>> MetersAsync(CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        return [.. (await measures.MetersAsync(await Begin(ct), ct)).Select(m => Status(m, today))];
    }

    public async Task<Guid> SaveMeterAsync(Guid? id, SaveMeterRequest r, CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        if (!MeterMeasures.Contains(r.Measures))
        {
            throw AppException.Validation($"Measures is one of: {string.Join(", ", MeterMeasures)}.");
        }
        if (!MeterKinds.Contains(r.Kind))
        {
            throw AppException.Validation("Kind is Main, Sub, Portable or Virtual.");
        }
        if (r.UncertaintyPct is < 0 or > 50)
        {
            throw AppException.Validation("Uncertainty is a percentage of the reading between 0 and 50.");
        }
        if (r.IntervalMonths is < 1 or > 240)
        {
            throw AppException.Validation("The calibration interval is 1 to 240 months.");
        }
        var saved = await measures.SaveMeterAsync(tenantId, id, r with
        {
            Name = Guard.Required(r.Name, "Name", 150), SerialNo = Guard.Optional(r.SerialNo, "Serial number", 60), Make = Guard.Optional(r.Make, "Make", 100),
            AccuracyClass = Guard.Optional(r.AccuracyClass, "Accuracy class", 30), Location = Guard.Optional(r.Location, "Location", 150),
        }, ct);
        await audit.WriteAsync(id is null ? "enms.meter.create" : "enms.meter.update", "Meter", saved.ToString(), new { r.Name, r.Kind, r.IntervalMonths }, ct);
        return saved;
    }

    public async Task DeleteMeterAsync(Guid id, CancellationToken ct)
    {
        await measures.DeleteMeterAsync(await Begin(ct), id, ct);
        await audit.WriteAsync("enms.meter.delete", "Meter", id.ToString(), null, ct);
    }

    public async Task<int> RegisterConnectedMetersAsync(CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        var s = await settings.GetAsync(ct);
        var added = await measures.RegisterConnectedMetersAsync(tenantId, s.MainMeterAssetId, ct);
        await audit.WriteAsync("enms.meter.register", "Meter", null, new { added }, ct);
        return added;
    }

    public async Task<IReadOnlyList<Calibration>> CalibrationsAsync(Guid meterId, CancellationToken ct) =>
        await measures.CalibrationsAsync(await Begin(ct), meterId, ct);

    public async Task<Guid> AddCalibrationAsync(Guid meterId, SaveCalibrationRequest r, CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        if (!CalibrationResults.Contains(r.Result))
        {
            throw AppException.Validation("Result is Pass, Adjusted or Fail.");
        }
        if (r.CalibratedOn.Date > DateTime.UtcNow.Date.AddDays(1))
        {
            throw AppException.Validation("A calibration cannot be in the future.");
        }
        if ((await measures.MetersAsync(tenantId, ct)).All(m => m.MeterId != meterId))
        {
            throw AppException.NotFound("Meter");
        }
        var id = await measures.AddCalibrationAsync(tenantId, meterId, r with
        {
            Lab = Guard.Optional(r.Lab, "Laboratory", 150), CertificateNo = Guard.Optional(r.CertificateNo, "Certificate number", 80),
            Note = Guard.Optional(r.Note, "Note", 500),
        }, ct);
        await audit.WriteAsync("enms.calibration.create", "Calibration", id.ToString(), new { meterId, r.CalibratedOn, r.Result }, ct);
        return id;
    }

    // ---------- measurement and verification ----------

    public async Task<IReadOnlyList<MvPlan>> MvPlansAsync(CancellationToken ct) => await measures.MvPlansAsync(await Begin(ct), ct);

    public async Task<Guid> SaveMvPlanAsync(Guid? id, SaveMvPlanRequest r, CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        if (!MvOptions.Contains(r.MvOption))
        {
            throw AppException.Validation("The IPMVP option is A, B, C or D.");
        }
        if (r.ReportingFrom.Date >= r.ReportingTo.Date)
        {
            throw AppException.Validation("The reporting period must start before it ends.");
        }
        if (r.ConfidencePct is not (68 or 80 or 90 or 95))
        {
            throw AppException.Validation("Confidence is 68, 80, 90 or 95 %.");
        }
        if (r.MvOption is "B" or "C")
        {
            var enpi = (await repo.EnpisAsync(tenantId, ct)).FirstOrDefault(e => e.EnpiId == r.EnpiId)
                       ?? throw AppException.Validation("Options B and C are measured against an EnPI's baseline model — pick the EnPI.");
            if (r.MvOption == "B" && enpi.SeuId is null)
            {
                throw AppException.Validation("Option B isolates the retrofit: use an EnPI whose boundary is a significant energy use, or pick option C for the whole site.");
            }
            if (r.MvOption == "C" && enpi.SeuId is not null)
            {
                throw AppException.Validation("Option C is the whole facility: use a site EnPI (main meters), or pick option B for an SEU.");
            }
            if (r.ReportingFrom.Date < enpi.BaselineTo.Date)
            {
                throw AppException.Validation("The reporting period must start after the baseline period ends.");
            }
        }
        else if (r.ManualSavingsKwh is null || r.ManualUncertaintyPct is not >= 0)
        {
            throw AppException.Validation("Options A and D record the savings the engineer measured or simulated, with their uncertainty in %.");
        }
        if (r.ActionId is { } aid && (await repo.ActionsAsync(tenantId, ct)).All(a => a.ActionId != aid))
        {
            throw AppException.NotFound("Action");
        }
        var saved = await measures.SaveMvPlanAsync(tenantId, id, r with
        {
            Name = Guard.Required(r.Name, "Name", 150), Boundary = Guard.Optional(r.Boundary, "Measurement boundary", 300),
            StaticFactors = Guard.Optional(r.StaticFactors, "Static factors", 500),
        }, ct);
        await audit.WriteAsync(id is null ? "enms.mv.create" : "enms.mv.update", "MvPlan", saved.ToString(), new { r.Name, r.MvOption }, ct);
        return saved;
    }

    public async Task DeleteMvPlanAsync(Guid id, CancellationToken ct)
    {
        await measures.DeleteMvPlanAsync(await Begin(ct), id, ct);
        await audit.WriteAsync("enms.mv.delete", "MvPlan", id.ToString(), null, ct);
    }

    /// <summary>Residuals of the daily baseline data against the EnPI's own model, as ASHRAE 14 judges model quality.</summary>
    public static ModelFit BaselineFit(Enpi e, IReadOnlyList<(double Kwh, double Parts)> days) =>
        MvMath.Fit([.. days.Select(d => d.Kwh)], [.. days.Select(d => Expected(e, d.Parts))], e.Kind == "Regression" ? 2 : 1);

    public static IReadOnlyList<MvCheck> Checks(ModelFit fit, SavingsUncertainty? s, string? meterNote)
    {
        var list = new List<MvCheck>
        {
            new("Model CV(RMSE)", fit.CvRmsePct <= MvMath.HourlyCvLimit ? (fit.CvRmsePct <= MvMath.MonthlyCvLimit ? "ok" : "warn") : "bad",
                $"{fit.CvRmsePct:N1} % on {fit.N} daily points (ASHRAE 14: ≤ {MvMath.MonthlyCvLimit} % monthly, ≤ {MvMath.HourlyCvLimit} % hourly data)."),
            new("Model bias NMBE", Math.Abs(fit.NmbePct) <= MvMath.MonthlyNmbeLimit ? "ok" : Math.Abs(fit.NmbePct) <= MvMath.HourlyNmbeLimit ? "warn" : "bad",
                $"{fit.NmbePct:N2} % (ASHRAE 14: within ±{MvMath.MonthlyNmbeLimit} % monthly, ±{MvMath.HourlyNmbeLimit} % hourly)."),
            new("Residual autocorrelation", fit.Autocorrelation < 0.5 ? "ok" : "warn",
                $"ρ = {fit.Autocorrelation:N2}; effective points n′ = {fit.EffectiveN:N0} of {fit.N}."),
        };
        if (s is not null)
        {
            list.Add(s.SavingsKwh <= 0
                ? new MvCheck("Savings", "bad", "No savings in the reporting period: consumption is at or above the adjusted baseline.")
                : new MvCheck("Fractional savings uncertainty", s.Fsu68Pct <= MvMath.FsuLimitPct ? "ok" : "bad",
                    $"{s.Fsu68Pct:N1} % at 68 % confidence (ASHRAE 14 asks for under {MvMath.FsuLimitPct} %); ±{s.CombinedUncertaintyKwh:N0} kWh at {s.ConfidencePct} %."));
        }
        if (meterNote is not null)
        {
            list.Add(new MvCheck("Meter accuracy", meterNote.StartsWith("No ") ? "warn" : "ok", meterNote));
        }
        return list;
    }

    public async Task<MvResult> MvResultAsync(Guid id, CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        var plan = (await measures.MvPlansAsync(tenantId, ct)).FirstOrDefault(p => p.PlanId == id) ?? throw AppException.NotFound("M&V plan");
        var days = (plan.ReportingTo.Date - plan.ReportingFrom.Date).Days;
        if (plan.MvOption is "A" or "D")
        {
            var kwh = plan.ManualSavingsKwh ?? 0;
            var u = Math.Abs(kwh) * (plan.ManualUncertaintyPct ?? 0) / 100;
            var method = plan.MvOption == "A" ? "IPMVP Option A — retrofit isolation, key parameter measured" : "IPMVP Option D — calibrated simulation";
            return new MvResult(plan.PlanId, plan.Name, plan.MvOption, method, null, null, null, Math.Round(kwh / Math.Max(1, days) * 365, 0),
                Math.Round(u / Math.Max(1, days) * 365, 0), 0,
                [new("Documented savings", kwh > 0 ? "ok" : "bad", $"{kwh:N0} kWh ± {plan.ManualUncertaintyPct:N0} % as documented by the engineer.")], [],
                ["Options A and D are not computed from the meters: keep the measurement records or simulation calibration with the plan."]);
        }
        var enpi = (await repo.EnpisAsync(tenantId, ct)).FirstOrDefault(e => e.EnpiId == plan.EnpiId) ?? throw AppException.NotFound("EnPI");
        var seu = enpi.SeuId is { } sid ? (await repo.SeusAsync(tenantId, ct)).FirstOrDefault(x => x.SeuId == sid) : null;
        var baseline = (await DailyAsync(tenantId, seu, enpi.BaselineFrom, enpi.BaselineTo, ct)).Where(d => d.Kwh > 0).ToList();
        var reporting = (await DailyAsync(tenantId, seu, plan.ReportingFrom, plan.ReportingTo, ct)).Where(d => d.Kwh > 0).ToList();
        var notes = new List<string>();
        if (baseline.Count < 3)
        {
            throw AppException.Validation("The EnPI's baseline period has fewer than 3 days of metered energy left, so its model cannot be checked.");
        }
        var fit = BaselineFit(enpi, [.. baseline.Select(d => (d.Kwh, d.Parts))]);
        var evaluated = Evaluate(enpi, [.. reporting.Select(d => (d.Date, d.Kwh, d.Parts))], plan.ReportingFrom, plan.ReportingTo);

        // meter accuracy: the least accurate meter inside the boundary (conservative)
        var meters = (await measures.MetersAsync(tenantId, ct)).Where(m => m.IsActive && m.Measures == "electricity").ToList();
        var inside = seu is null ? meters.Where(m => m.Kind == "Main").ToList() : meters.Where(m => m.AssetId is { } a && SeuAssets(seu).Contains(a)).ToList();
        var meterPct = (double)(inside.Where(m => m.UncertaintyPct is not null).Select(m => m.UncertaintyPct!.Value).DefaultIfEmpty(0).Max());
        var meterNote = inside.Count == 0 ? "No meter in the register for this boundary — measurement uncertainty is not included."
            : meterPct == 0 ? $"{inside.Count} meter(s) in the boundary, none with a stated uncertainty — measurement uncertainty is not included."
            : $"Least accurate meter in the boundary: ±{meterPct:N2} % of reading.";
        var today = DateTime.UtcNow.Date;
        var stale = inside.Select(m => Status(m, today)).Where(x => x.State is "overdue" or "failed" or "never").Select(x => x.Meter.Name).ToList();
        if (stale.Count > 0)
        {
            notes.Add($"Meters in the boundary without a valid calibration: {string.Join(", ", stale)}. An auditor will question savings measured on them (ISO 50001 9.1).");
        }

        var adjusted = evaluated.ExpectedKwh + plan.NonRoutineKwh;
        var savings = MvMath.Savings(fit, adjusted, evaluated.ActualKwh, reporting.Count, plan.ConfidencePct, meterPct);
        if (plan.NonRoutineKwh != 0)
        {
            notes.Add($"Non-routine adjustment of {plan.NonRoutineKwh:N0} kWh added to the baseline{(plan.StaticFactors is null ? "" : $" ({plan.StaticFactors})")}.");
        }
        if (reporting.Count < (plan.ReportingTo.Date - plan.ReportingFrom.Date).Days)
        {
            notes.Add($"{reporting.Count} of {(plan.ReportingTo.Date - plan.ReportingFrom.Date).Days} reporting days have metered energy; missing days are left out, not estimated.");
        }
        notes.Add("ASHRAE 14 states its limits for monthly and hourly models; this model uses daily data, so both are shown.");
        notes.AddRange(evaluated.Notes);
        var perYear = reporting.Count > 0 ? 365.0 / reporting.Count : 0;
        var methodName = plan.MvOption == "B" ? "IPMVP Option B — retrofit isolation, all parameters measured" : "IPMVP Option C — whole facility";
        return new MvResult(plan.PlanId, plan.Name, plan.MvOption, methodName, evaluated.Model, fit, savings, Math.Round(savings.SavingsKwh * perYear, 0),
            Math.Round(savings.CombinedUncertaintyKwh * Math.Sqrt(perYear), 0), meterPct, Checks(fit, savings, meterNote), evaluated.Days, notes);
    }

    /// <summary>Records the plan's verified savings on its action (status Verified), with how they were verified.</summary>
    public async Task<MvResult> ApplyMvAsync(Guid id, CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        var plan = (await measures.MvPlansAsync(tenantId, ct)).FirstOrDefault(p => p.PlanId == id) ?? throw AppException.NotFound("M&V plan");
        var action = (await repo.ActionsAsync(tenantId, ct)).FirstOrDefault(a => a.ActionId == plan.ActionId)
                     ?? throw AppException.Validation("Link the plan to an action first.");
        var result = await MvResultAsync(id, ct);
        if (result.AnnualisedKwh is not > 0)
        {
            throw AppException.Validation("The plan shows no savings, so there is nothing to verify.");
        }
        if (result.Checks.Any(c => c.State == "bad"))
        {
            throw AppException.Validation($"The savings do not pass: {string.Join("; ", result.Checks.Where(c => c.State == "bad").Select(c => c.Name))}.");
        }
        var how = $"{result.Method}; {result.AnnualisedKwh:N0} ± {result.AnnualisedUncertaintyKwh:N0} kWh/yr at {plan.ConfidencePct} % confidence";
        await repo.SaveActionAsync(tenantId, action.ActionId, new SaveActionRequest(action.ObjectiveId, action.Title, action.Owner, action.DueDate,
            action.ExpectedKwhYear, action.Investment, how.Length > 300 ? how[..300] : how, result.AnnualisedKwh, "Verified"), ct);
        await audit.WriteAsync("enms.mv.apply", "Action", action.ActionId.ToString(), new { plan = id, result.AnnualisedKwh }, ct);
        return result;
    }
}
