using System.Text.Json;
using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Energy;
using Divakar.FactoryOS.Application.Settings;

namespace Divakar.FactoryOS.Application.Enms;

/// <summary>The company's emission factor library; a company with none gets the published Taiwan factors on first use.</summary>
public sealed class EmissionFactorProvider(IEnmsRepository repo)
{
    private IReadOnlyList<EmissionFactor>? cache;

    public async Task<IReadOnlyList<EmissionFactor>> FactorsAsync(Guid tenantId, CancellationToken ct)
    {
        if (cache is not null)
        {
            return cache;
        }
        var list = await repo.FactorsAsync(tenantId, ct);
        if (list.Count == 0)
        {
            await repo.AddMissingFactorsAsync(tenantId, EmissionMath.TaiwanDefaults(), ct);
            list = await repo.FactorsAsync(tenantId, ct);
        }
        return cache = list;
    }

    public void Forget() => cache = null;
}

/// <summary>
/// ISO 50001:2018 energy management system on top of the metered data: energy review and significant energy uses (6.3),
/// EnPIs and energy baselines with normalisation (6.4, 6.5; ISO 50006), objectives and action plans (6.2), documented
/// information (4–8), monitoring of actual against expected consumption (9.1), internal audit, management review and
/// nonconformities (9.2, 9.3, 10.1).
/// </summary>
public sealed class EnmsService(IEnmsRepository repo, IEnergyRepository energy, EmissionFactorProvider factors, SettingsService settings,
    ModuleGate gate, ICurrentUser me, IAuditLog audit)
{
    public static readonly string[] DocumentKinds =
        ["Context", "Parties", "Scope", "Policy", "Team", "Risk", "Legal", "DataPlan", "Competence", "Communication", "Design", "Procurement"];
    public static readonly string[] ActionStates = ["Planned", "InProgress", "Done", "Verified"];
    public static readonly string[] FindingKinds = ["Audit", "ManagementReview", "Nonconformity"];
    public static readonly string[] EnpiKinds = ["Measured", "Ratio", "Regression"];

    /// <summary>R² below this is flagged: ISO 50006 lets the organisation set its own criteria; 0.75 is a common rule of thumb.</summary>
    public const double MinR2 = 0.75;

    private async Task<Guid> Begin(CancellationToken ct)
    {
        await gate.EnsureAsync("energy", ct);
        return me.RequireTenant();
    }

    // ---------- emission factors ----------

    public async Task<IReadOnlyList<EmissionFactor>> FactorsAsync(CancellationToken ct) =>
        await factors.FactorsAsync(await Begin(ct), ct);

    public async Task<Guid> SaveFactorAsync(Guid? id, SaveFactorRequest r, CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        if (!EmissionMath.Sources.Contains(r.Source))
        {
            throw AppException.Validation($"Source is one of: {string.Join(", ", EmissionMath.Sources)}.");
        }
        if (r.Scope is < 1 or > 3 || r.Method is not ("Combustion" or "Location" or "Market"))
        {
            throw AppException.Validation("Scope is 1–3 and method is Combustion, Location or Market.");
        }
        if (r.Year is < 1990 or > 2100)
        {
            throw AppException.Validation("Year is between 1990 and 2100.");
        }
        if (r.KgCo2ePerUnit is null && (r.HeatKcalPerUnit is not > 0 || r.Co2KgPerTj is null))
        {
            throw AppException.Validation("Give kg CO₂e per unit, or the heat value with the per-TJ factors.");
        }
        if (r.KgCo2ePerUnit < 0 || r.Co2KgPerTj < 0 || r.Ch4KgPerTj < 0 || r.N2oKgPerTj < 0)
        {
            throw AppException.Validation("Factors cannot be negative.");
        }
        var clean = r with
        {
            Name = Guard.Required(r.Name, "Name", 150), Region = Guard.Required(r.Region, "Region", 10), Unit = Guard.Required(r.Unit, "Unit", 10),
            Reference = Guard.Required(r.Reference, "Reference (the published source)", 400), GwpSet = r.GwpSet is "AR6" ? "AR6" : "AR5",
        };
        try
        {
            var saved = await repo.SaveFactorAsync(tenantId, id, clean, ct);
            factors.Forget();
            await audit.WriteAsync(id is null ? "enms.factor.create" : "enms.factor.update", "EmissionFactor", saved.ToString(),
                new { clean.Source, clean.Year, clean.Method, clean.KgCo2ePerUnit }, ct);
            return saved;
        }
        catch (Exception e) when (e.Message.Contains("UX_EmissionFactors"))
        {
            throw AppException.Conflict("A factor for this source, method, region and year already exists — edit that one.");
        }
    }

    // ---------- energy data that no meter reads ----------

    public async Task<IReadOnlyList<EnergyEntry>> EntriesAsync(CancellationToken ct) => await repo.EntriesAsync(await Begin(ct), null, null, ct);

    public async Task<Guid> AddEntryAsync(SaveEntryRequest r, CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        if (r.Source != "green_power" && !EmissionMath.Sources.Contains(r.Source))
        {
            throw AppException.Validation("Source is a fuel, electricity, steam or green_power.");
        }
        if (r.Quantity <= 0)
        {
            throw AppException.Validation("Give the quantity delivered or billed (more than zero).");
        }
        if (r.PeriodFrom.Date > r.PeriodTo.Date)
        {
            throw AppException.Validation("The period must start on or before its end.");
        }
        if (r.Source == "green_power" && r.Instrument is not ("T-REC" or "PPA" or "Self"))
        {
            throw AppException.Validation("Green power needs its instrument: T-REC, PPA or Self.");
        }
        var unit = r.Unit;
        if (string.IsNullOrWhiteSpace(unit))
        {
            var f = EmissionMath.Resolve(await factors.FactorsAsync(tenantId, ct), r.Source, r.Source == "electricity" ? "Location" : "Combustion", r.PeriodTo.Year);
            unit = r.Source == "green_power" ? "kWh" : f?.Unit ?? throw AppException.Validation("Give the unit.");
        }
        var id = await repo.AddEntryAsync(tenantId, r with
        {
            Unit = Guard.Required(unit, "Unit", 10), Reference = Guard.Optional(r.Reference, "Reference", 200), Note = Guard.Optional(r.Note, "Note", 500),
        }, ct);
        await audit.WriteAsync("enms.entry.create", "EnergyEntry", id.ToString(), new { r.Source, r.Quantity, r.PeriodFrom, r.PeriodTo }, ct);
        return id;
    }

    public async Task DeleteEntryAsync(Guid id, CancellationToken ct)
    {
        await repo.DeleteEntryAsync(await Begin(ct), id, ct);
        await audit.WriteAsync("enms.entry.delete", "EnergyEntry", id.ToString(), null, ct);
    }

    // ---------- significant energy uses ----------

    public async Task<IReadOnlyList<Seu>> SeusAsync(CancellationToken ct) => await repo.SeusAsync(await Begin(ct), ct);

    public async Task<Guid> SaveSeuAsync(Guid? id, SaveSeuRequest r, CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        if (r.AssetIds.Count == 0)
        {
            throw AppException.Validation("Pick at least one asset for the significant energy use.");
        }
        var saved = await repo.SaveSeuAsync(tenantId, id, r with
        {
            Name = Guard.Required(r.Name, "Name", 150), RelevantVariables = Guard.Optional(r.RelevantVariables, "Relevant variables", 300),
            OperatingCriteria = Guard.Optional(r.OperatingCriteria, "Operating criteria", 1000), Owner = Guard.Optional(r.Owner, "Owner", 150),
        }, ct);
        await audit.WriteAsync(id is null ? "enms.seu.create" : "enms.seu.update", "Seu", saved.ToString(), new { r.Name }, ct);
        return saved;
    }

    public async Task DeleteSeuAsync(Guid id, CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        if ((await repo.EnpisAsync(tenantId, ct)).Any(e => e.SeuId == id))
        {
            throw AppException.Conflict("An EnPI measures this significant energy use — delete the EnPI first.");
        }
        await repo.DeleteSeuAsync(tenantId, id, ct);
        await audit.WriteAsync("enms.seu.delete", "Seu", id.ToString(), null, ct);
    }

    private static IReadOnlyList<Guid> SeuAssets(Seu seu) => JsonSerializer.Deserialize<List<Guid>>(seu.AssetIds) ?? [];

    // ---------- daily energy and production for a boundary ----------

    private sealed record Day(DateTime Date, double Kwh, double Parts);

    /// <summary>Local midnight of a date, in UTC.</summary>
    private static DateTime LocalDayToUtc(DateTime date, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified), zone);

    /// <summary>
    /// Daily kWh and parts made inside one boundary: the site (main meters, the relevant variable is parts from production
    /// machines) or a significant energy use (its assets' energy, and its own part counters when it has any).
    /// </summary>
    private async Task<List<Day>> DailyAsync(Guid tenantId, Seu? seu, DateTime fromDate, DateTime toDate, CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        var zone = s.Zone();
        var f = LocalDayToUtc(fromDate, zone);
        var t = LocalDayToUtc(toDate, zone);
        var offset = (int)zone.GetUtcOffset(t).TotalMinutes;
        var deltas = await energy.CounterDeltasAsync(tenantId, ["energy_kwh", "part_count"], f, t, ct);
        var kwhTags = deltas.Where(d => d.SensorTypeCode == "energy_kwh").ToList();
        var partTags = deltas.Where(d => d.SensorTypeCode == "part_count").ToList();
        var siteParts = partTags.Where(d => d.Category == "Production" && d.AssetTypeCode != "agv").ToList();
        if (seu is null)
        {
            var main = EnergyService.MainMeters(kwhTags, s.MainMeterAssetId);
            kwhTags = main.Count > 0 ? [.. main] : [.. kwhTags.Where(e => e.Category != "Energy")];
            partTags = siteParts;
        }
        else
        {
            var assets = SeuAssets(seu).ToHashSet();
            kwhTags = [.. kwhTags.Where(e => assets.Contains(e.AssetId))];
            var own = partTags.Where(d => assets.Contains(d.AssetId)).ToList();
            partTags = own.Count > 0 ? own : siteParts;
        }
        var kwh = EnergyService.Consumption(await energy.CounterBucketsAsync(tenantId, [.. kwhTags.Select(x => x.TagId)], f, t, 1440, offset, ct))
            .ToDictionary(x => x.Bucket.Date, x => x.Value);
        var parts = EnergyService.Consumption(await energy.CounterBucketsAsync(tenantId, [.. partTags.Select(x => x.TagId)], f, t, 1440, offset, ct))
            .ToDictionary(x => x.Bucket.Date, x => x.Value);
        return [.. kwh.Keys.Order().Select(d => new Day(d, kwh[d], parts.GetValueOrDefault(d)))];
    }

    // ---------- EnPIs and energy baselines ----------

    public async Task<IReadOnlyList<Enpi>> EnpisAsync(CancellationToken ct) => await repo.EnpisAsync(await Begin(ct), ct);

    public async Task<Guid> AddEnpiAsync(SaveEnpiRequest r, CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        var name = Guard.Required(r.Name, "Name", 150);
        if (!EnpiKinds.Contains(r.Kind))
        {
            throw AppException.Validation("Kind is Measured (kWh), Ratio (kWh per part) or Regression (kWh = a + b × parts).");
        }
        if (r.BaselineFrom.Date >= r.BaselineTo.Date)
        {
            throw AppException.Validation("The baseline period must start before it ends.");
        }
        Seu? seu = null;
        if (r.SeuId is { } seuId)
        {
            seu = (await repo.SeusAsync(tenantId, ct)).FirstOrDefault(x => x.SeuId == seuId) ?? throw AppException.NotFound("Significant energy use");
        }
        var days = (await DailyAsync(tenantId, seu, r.BaselineFrom, r.BaselineTo, ct)).Where(d => d.Kwh > 0).ToList();
        var needed = r.Kind == "Regression" ? 7 : 3;
        if (days.Count < needed)
        {
            throw AppException.Validation($"The baseline period has {days.Count} day(s) of metered energy; a {r.Kind} baseline needs at least {needed}.");
        }
        var kwh = days.Sum(d => d.Kwh);
        var parts = days.Sum(d => d.Parts);
        if (r.Kind is "Ratio" or "Regression" && parts <= 0)
        {
            throw AppException.Validation("No parts were counted in the baseline period, so energy cannot be normalised by production. Use a Measured EnPI.");
        }
        double? a = null, b = null, r2 = null;
        if (r.Kind == "Regression")
        {
            var fit = Regression.Fit([.. days.Select(d => d.Parts)], [.. days.Select(d => d.Kwh)]);
            (a, b, r2) = (fit.Intercept, fit.Slope, fit.R2);
        }
        var id = await repo.AddEnpiAsync(tenantId, r with { Name = name }, kwh, parts, days.Count, a, b, r2, ct);
        await audit.WriteAsync("enms.enpi.create", "Enpi", id.ToString(), new { name, r.Kind, r.BaselineFrom, r.BaselineTo, kwh, parts, r2 }, ct);
        return id;
    }

    public async Task DeleteEnpiAsync(Guid id, CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        if ((await repo.ObjectivesAsync(tenantId, ct)).Any(o => o.EnpiId == id))
        {
            throw AppException.Conflict("An objective is measured by this EnPI — change or delete the objective first.");
        }
        await repo.DeleteEnpiAsync(tenantId, id, ct);
        await audit.WriteAsync("enms.enpi.delete", "Enpi", id.ToString(), null, ct);
    }

    /// <summary>Expected daily energy under baseline conditions (normalised to the day's production).</summary>
    public static double Expected(Enpi e, double parts) => e.Kind switch
    {
        "Regression" => (e.Intercept ?? 0) + (e.Slope ?? 0) * parts,
        "Ratio" => e.BaselineParts is > 0 ? (e.BaselineKwh ?? 0) / e.BaselineParts.Value * parts : 0,
        _ => e.BaselineDays is > 0 ? (e.BaselineKwh ?? 0) / e.BaselineDays.Value : 0,
    };

    public static EnpiResult Evaluate(Enpi e, IReadOnlyList<(DateTime Day, double Kwh, double Parts)> days, DateTime from, DateTime to)
    {
        var rows = new List<EnpiDay>();
        double cusum = 0;
        foreach (var d in days)
        {
            var expected = Math.Max(0, Expected(e, d.Parts));
            var savings = expected - d.Kwh;
            cusum += savings;
            rows.Add(new EnpiDay(d.Day, Math.Round(d.Kwh, 1), Math.Round(d.Parts, 0), Math.Round(expected, 1), Math.Round(savings, 1), Math.Round(cusum, 1)));
        }
        var actual = days.Sum(d => d.Kwh);
        var exp = rows.Sum(r => r.Expected);
        var parts = days.Sum(d => d.Parts);
        var notes = new List<string>();
        var model = e.Kind switch
        {
            "Regression" => $"E = {e.Intercept:N1} + {e.Slope:N4} × parts per day (R² {e.R2:N2})",
            "Ratio" => $"{(e.BaselineParts is > 0 ? e.BaselineKwh / e.BaselineParts : 0):N4} kWh per part",
            _ => $"{(e.BaselineDays is > 0 ? e.BaselineKwh / e.BaselineDays : 0):N1} kWh per day",
        };
        if (e.Kind == "Regression" && e.R2 < MinR2)
        {
            notes.Add($"The baseline model explains {e.R2:P0} of the daily variation (below {MinR2:P0}). Look for another relevant variable "
                + "(shift hours, outdoor temperature, product mix) before relying on the savings figure.");
        }
        if (e.Kind == "Regression" && e.Slope < 0)
        {
            notes.Add("In this baseline model energy falls as production rises, which rarely makes engineering sense. Check the meters "
                + "and part counters in the baseline period, or pick another period, before using it (ISO 50006 asks for a model that fits the process).");
        }
        if (e.Kind == "Measured")
        {
            notes.Add("Not normalised: a Measured EnPI compares raw consumption, so production changes show up as savings or losses.");
        }
        if (days.Count == 0)
        {
            notes.Add("No metered energy in the reporting period.");
        }
        return new EnpiResult(e.EnpiId, e.Name, e.Kind, from, to, Math.Round(actual, 1), Math.Round(exp, 1), Math.Round(exp - actual, 1),
            exp > 0 ? Math.Round((exp - actual) / exp * 100, 1) : 0,
            e.BaselineParts is > 0 ? Math.Round((e.BaselineKwh ?? 0) / e.BaselineParts.Value, 4) : null,
            parts > 0 ? Math.Round(actual / parts, 4) : null, e.R2 is { } r2 ? Math.Round(r2, 3) : null, model, rows, notes);
    }

    /// <summary>Reporting period: the given dates, else the last 7 complete local days.</summary>
    private async Task<(DateTime From, DateTime To)> ReportingAsync(DateTime? from, DateTime? to, CancellationToken ct)
    {
        var zone = (await settings.GetAsync(ct)).Zone();
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone).Date;
        var t = (to ?? today).Date;
        var f = (from ?? t.AddDays(-7)).Date;
        if (f >= t)
        {
            throw AppException.Validation("'from' must be before 'to'.");
        }
        return (f, t);
    }

    public async Task<EnpiResult> EnpiResultAsync(Guid id, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        var e = (await repo.EnpisAsync(tenantId, ct)).FirstOrDefault(x => x.EnpiId == id) ?? throw AppException.NotFound("EnPI");
        var (f, t) = await ReportingAsync(from, to, ct);
        return await ResultAsync(tenantId, e, f, t, ct);
    }

    private async Task<EnpiResult> ResultAsync(Guid tenantId, Enpi e, DateTime f, DateTime t, CancellationToken ct)
    {
        var seu = e.SeuId is { } sid ? (await repo.SeusAsync(tenantId, ct)).FirstOrDefault(x => x.SeuId == sid) : null;
        var days = await DailyAsync(tenantId, seu, f, t, ct);
        return Evaluate(e, [.. days.Where(d => d.Kwh > 0).Select(d => (d.Date, d.Kwh, d.Parts))], f, t);
    }

    // ---------- energy review (6.3) ----------

    public async Task<EnergyReview> ReviewAsync(DateTime? from, DateTime? to, CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        var s = await settings.GetAsync(ct);
        var (fd, td) = await ReportingAsync(from ?? DateTime.UtcNow.Date.AddDays(-30), to, ct);
        var zone = s.Zone();
        var f = LocalDayToUtc(fd, zone);
        var t = LocalDayToUtc(td, zone);
        var lib = await factors.FactorsAsync(tenantId, ct);
        var deltas = await energy.CounterDeltasAsync(tenantId, ["energy_kwh", "volume_gas"], f, t, ct);
        var kwhTags = deltas.Where(d => d.SensorTypeCode == "energy_kwh").ToList();
        var main = EnergyService.MainMeters(kwhTags, s.MainMeterAssetId);
        var machines = kwhTags.Where(e => e.Category != "Energy").GroupBy(e => e.AssetId)
            .Select(g => (g.Key, g.First().AssetName, g.First().Category, Kwh: g.Sum(x => x.Delta))).OrderByDescending(x => x.Kwh).ToList();
        var machinesKwh = machines.Sum(m => m.Kwh);
        var grid = main.Count > 0 ? main.Sum(m => m.Delta) : machinesKwh;
        var solar = kwhTags.Where(e => e.AssetTypeCode == "solar-inverter").Sum(e => e.Delta);
        var gas = deltas.Where(d => d.SensorTypeCode == "volume_gas").Sum(d => d.Delta);
        var year = td.AddDays(-1).Year;

        var sources = new List<ReviewSource>();
        void Add(string source, string unit, double qty, string method, bool emits = true)
        {
            if (qty <= 0)
            {
                return;
            }
            var fac = EmissionMath.Resolve(lib, source, method, year);
            var kwhEq = qty * EmissionMath.KwhPerUnit(fac, source);
            sources.Add(new ReviewSource(source, unit, Math.Round(qty, 2), Math.Round(kwhEq, 1),
                emits && fac is not null ? Math.Round(qty * (double)fac.Co2e, 1) : 0, emits ? fac?.Name : "no emissions"));
        }
        Add("electricity", "kWh", grid, "Location");
        Add("natural_gas", "Nm3", gas, "Combustion");
        foreach (var g in (await repo.EntriesAsync(tenantId, fd, td, ct)).Where(x => x.Source != "green_power").GroupBy(x => x.Source))
        {
            var qty = g.Sum(x => (double)x.Quantity * EmissionMath.Overlap(x.PeriodFrom, x.PeriodTo, fd, td));
            Add(g.Key, g.First().Unit, qty, g.Key == "electricity" ? "Location" : "Combustion");
        }
        if (solar > 0)
        {
            sources.Add(new ReviewSource("solar", "kWh", Math.Round(solar, 1), Math.Round(solar, 1), 0, "on-site generation"));
        }
        var total = sources.Sum(x => x.Kwh);

        var seus = await repo.SeusAsync(tenantId, ct);
        var inSeu = seus.SelectMany(x => SeuAssets(x).Select(a => (a, x.Name))).GroupBy(x => x.a).ToDictionary(g => g.Key, g => g.First().Name);
        var uses = new List<ReviewUse>();
        double cumulative = 0;
        foreach (var m in machines)
        {
            var share = grid > 0 ? m.Kwh / grid * 100 : 0;
            var before = cumulative;
            cumulative += share;
            // Pareto: the largest uses that together reach 80 % of electricity, each at least 3 % of it
            uses.Add(new ReviewUse(m.Key, m.AssetName, m.Category, Math.Round(m.Kwh, 1), Math.Round(share, 1), Math.Round(cumulative, 1),
                before < 80 && share >= 3, inSeu.GetValueOrDefault(m.Key)));
        }
        var notes = new List<string>();
        var unmetered = Math.Max(0, grid - machinesKwh);
        if (grid > 0 && unmetered / grid > 0.15)
        {
            notes.Add($"{unmetered / grid:P0} of grid electricity ({unmetered:N0} kWh) is not on any machine meter: lighting, HVAC, compressors without "
                + "meters and losses. Add sub-meters to the largest of these (ISO 50001 6.6 data collection plan).");
        }
        var missing = uses.Where(u => u.Suggested && u.Seu is null).Select(u => u.Name).ToList();
        if (missing.Count > 0)
        {
            notes.Add($"Suggested significant energy uses not yet registered: {string.Join(", ", missing)}.");
        }
        if (sources.All(x => x.Source is "electricity" or "solar"))
        {
            notes.Add("Only electricity is recorded. Add fuel deliveries (diesel for forklifts or generators, LPG, boiler gas) under Energy data so the review and Scope 1 are complete.");
        }
        return new EnergyReview(fd, td, Math.Round(total, 1), Math.Round(machinesKwh, 1), Math.Round(unmetered, 1), sources, uses, notes);
    }

    // ---------- objectives, action plans ----------

    public async Task<IReadOnlyList<Objective>> ObjectivesAsync(CancellationToken ct) => await repo.ObjectivesAsync(await Begin(ct), ct);

    public async Task<Guid> SaveObjectiveAsync(Guid? id, SaveObjectiveRequest r, CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        if (r.TargetPct is <= 0 or > 100)
        {
            throw AppException.Validation("The target is an improvement between 0 and 100 %.");
        }
        if (r.EnpiId is { } eid && (await repo.EnpisAsync(tenantId, ct)).All(e => e.EnpiId != eid))
        {
            throw AppException.NotFound("EnPI");
        }
        var saved = await repo.SaveObjectiveAsync(tenantId, id, r with { Title = Guard.Required(r.Title, "Title", 200), Owner = Guard.Optional(r.Owner, "Owner", 150) }, ct);
        await audit.WriteAsync(id is null ? "enms.objective.create" : "enms.objective.update", "Objective", saved.ToString(), new { r.Title, r.TargetPct }, ct);
        return saved;
    }

    public async Task DeleteObjectiveAsync(Guid id, CancellationToken ct)
    {
        await repo.DeleteObjectiveAsync(await Begin(ct), id, ct);
        await audit.WriteAsync("enms.objective.delete", "Objective", id.ToString(), null, ct);
    }

    public async Task<IReadOnlyList<EnmsAction>> ActionsAsync(CancellationToken ct) => await repo.ActionsAsync(await Begin(ct), ct);

    public async Task<Guid> SaveActionAsync(Guid? id, SaveActionRequest r, CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        if (!ActionStates.Contains(r.Status))
        {
            throw AppException.Validation("Status is Planned, InProgress, Done or Verified.");
        }
        if (r.Status == "Verified" && (r.VerifiedKwhYear is null || string.IsNullOrWhiteSpace(r.Verification)))
        {
            throw AppException.Validation("A verified action needs the verified savings and how they were verified (ISO 50001 6.2, 9.1).");
        }
        if (r.ExpectedKwhYear < 0 || r.Investment < 0)
        {
            throw AppException.Validation("Savings and investment cannot be negative.");
        }
        var saved = await repo.SaveActionAsync(tenantId, id, r with
        {
            Title = Guard.Required(r.Title, "Title", 200), Owner = Guard.Optional(r.Owner, "Owner", 150), Verification = Guard.Optional(r.Verification, "Verification", 300),
        }, ct);
        await audit.WriteAsync(id is null ? "enms.action.create" : "enms.action.update", "Action", saved.ToString(), new { r.Title, r.Status }, ct);
        return saved;
    }

    public async Task DeleteActionAsync(Guid id, CancellationToken ct)
    {
        await repo.DeleteActionAsync(await Begin(ct), id, ct);
        await audit.WriteAsync("enms.action.delete", "Action", id.ToString(), null, ct);
    }

    // ---------- documented information ----------

    public async Task<IReadOnlyList<EnmsDocument>> DocumentsAsync(CancellationToken ct) => await repo.DocumentsAsync(await Begin(ct), ct);

    public async Task<Guid> SaveDocumentAsync(Guid? id, SaveDocumentRequest r, CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        if (!DocumentKinds.Contains(r.Kind))
        {
            throw AppException.Validation($"Kind is one of: {string.Join(", ", DocumentKinds)}.");
        }
        var saved = await repo.SaveDocumentAsync(tenantId, id, r with
        {
            Title = Guard.Required(r.Title, "Title", 200), Body = Guard.Optional(r.Body, "Text", 20000), Owner = Guard.Optional(r.Owner, "Owner", 150),
            ApprovedBy = Guard.Optional(r.ApprovedBy, "Approved by", 150),
        }, ct);
        await audit.WriteAsync(id is null ? "enms.document.create" : "enms.document.update", "EnmsDocument", saved.ToString(), new { r.Kind, r.Title }, ct);
        return saved;
    }

    public async Task DeleteDocumentAsync(Guid id, CancellationToken ct)
    {
        await repo.DeleteDocumentAsync(await Begin(ct), id, ct);
        await audit.WriteAsync("enms.document.delete", "EnmsDocument", id.ToString(), null, ct);
    }

    // ---------- audits, management reviews, nonconformities ----------

    public async Task<IReadOnlyList<Finding>> FindingsAsync(CancellationToken ct) => await repo.FindingsAsync(await Begin(ct), ct);

    public async Task<Guid> SaveFindingAsync(Guid? id, SaveFindingRequest r, CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        if (!FindingKinds.Contains(r.Kind))
        {
            throw AppException.Validation("Kind is Audit, ManagementReview or Nonconformity.");
        }
        if (r.Kind == "Nonconformity" && r.ClosedOn is not null && string.IsNullOrWhiteSpace(r.CorrectiveAction))
        {
            throw AppException.Validation("Record the corrective action before closing a nonconformity (ISO 50001 10.1).");
        }
        if (r.ClosedOn is { } closed && closed.Date < r.HeldOn.Date)
        {
            throw AppException.Validation("It cannot close before it was raised.");
        }
        var saved = await repo.SaveFindingAsync(tenantId, id, r with
        {
            Title = Guard.Required(r.Title, "Title", 200), Clause = Guard.Optional(r.Clause, "Clause", 10), Detail = Guard.Optional(r.Detail, "Detail", 2000),
            RootCause = Guard.Optional(r.RootCause, "Root cause", 1000), CorrectiveAction = Guard.Optional(r.CorrectiveAction, "Corrective action", 1000),
            Owner = Guard.Optional(r.Owner, "Owner", 150),
        }, ct);
        await audit.WriteAsync(id is null ? "enms.finding.create" : "enms.finding.update", "Finding", saved.ToString(), new { r.Kind, r.Title }, ct);
        return saved;
    }

    public async Task DeleteFindingAsync(Guid id, CancellationToken ct)
    {
        await repo.DeleteFindingAsync(await Begin(ct), id, ct);
        await audit.WriteAsync("enms.finding.delete", "Finding", id.ToString(), null, ct);
    }

    // ---------- overview: clause-by-clause readiness ----------

    public async Task<EnmsOverview> OverviewAsync(CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        var docs = await repo.DocumentsAsync(tenantId, ct);
        var seus = await repo.SeusAsync(tenantId, ct);
        var enpis = await repo.EnpisAsync(tenantId, ct);
        var objectives = await repo.ObjectivesAsync(tenantId, ct);
        var actions = await repo.ActionsAsync(tenantId, ct);
        var findings = await repo.FindingsAsync(tenantId, ct);
        var (f, t) = await ReportingAsync(null, null, ct);
        var results = new List<EnpiResult>();
        foreach (var e in enpis)
        {
            results.Add(await ResultAsync(tenantId, e, f, t, ct));
        }
        var today = DateTime.UtcNow.Date;
        var year = today.AddYears(-1);

        ClauseCheck Doc(string clause, string title, string kind, bool needsApproval = false)
        {
            var d = docs.Where(x => x.Kind == kind).ToList();
            if (d.Count == 0)
            {
                return new(clause, title, "missing", "No document recorded.");
            }
            if (needsApproval && d.All(x => x.ApprovedOn is null))
            {
                return new(clause, title, "warn", $"{d.Count} recorded, not approved by top management.");
            }
            var overdue = d.Count(x => x.ReviewDue < today);
            return overdue > 0 ? new(clause, title, "warn", $"{d.Count} recorded, {overdue} past its review date.") : new(clause, title, "ok", $"{d.Count} recorded.");
        }
        ClauseCheck Check(string clause, string title, bool ok, string okText, string missingText, bool partial = false, string? partialText = null) =>
            ok ? new(clause, title, "ok", okText) : partial ? new(clause, title, "warn", partialText ?? missingText) : new(clause, title, "missing", missingText);

        var audits = findings.Where(x => x.Kind == "Audit").ToList();
        var reviews = findings.Where(x => x.Kind == "ManagementReview").ToList();
        var ncs = findings.Where(x => x.Kind == "Nonconformity").ToList();
        var openNc = ncs.Where(x => x.ClosedOn is null).ToList();
        var overdueNc = openNc.Count(x => x.DueDate < today);
        var verified = actions.Where(a => a.Status == "Verified").ToList();
        var weakModels = enpis.Count(e => e.Kind == "Regression" && e.R2 < MinR2);
        var lastAudit = audits.MaxBy(a => a.HeldOn)?.HeldOn;
        var lastReview = reviews.MaxBy(a => a.HeldOn)?.HeldOn;

        var clauses = new List<ClauseCheck>
        {
            Doc("4.1–4.2", "Context and interested parties", "Parties"),
            Doc("4.3", "Scope and boundaries of the EnMS", "Scope"),
            Doc("5.2", "Energy policy", "Policy", needsApproval: true),
            Doc("5.3", "Energy management team, roles and authorities", "Team"),
            Doc("6.1", "Risks and opportunities", "Risk"),
            Check("6.2", "Objectives, energy targets and action plans", objectives.Count > 0 && actions.Count > 0,
                $"{objectives.Count} objective(s), {actions.Count} action(s).", "No objectives with action plans.", objectives.Count > 0 || actions.Count > 0,
                "Objectives and action plans must both exist."),
            Check("6.3", "Energy review and significant energy uses", seus.Count > 0 && seus.All(x => !string.IsNullOrWhiteSpace(x.RelevantVariables)),
                $"{seus.Count} SEU(s) with relevant variables; consumption from connected meters.", "No significant energy uses registered.", seus.Count > 0,
                "Some SEUs have no relevant variables."),
            Check("6.4–6.5", "EnPIs and energy baselines", enpis.Count > 0 && weakModels == 0, $"{enpis.Count} EnPI(s) with baselines.",
                "No EnPI or energy baseline.", enpis.Count > 0, $"{weakModels} regression baseline(s) below R² {MinR2:N2}."),
            Doc("6.6", "Energy data collection plan", "DataPlan"),
            Doc("7.2–7.3", "Competence and awareness", "Competence"),
            Doc("7.4", "Communication", "Communication"),
            Check("7.5", "Documented information", docs.Count >= 8, $"{docs.Count} documents under control.", "Few documents recorded.", docs.Count > 0,
                $"{docs.Count} documents recorded; most clauses still need theirs."),
            Check("8.1", "Operational planning and control of SEUs", seus.Count > 0 && seus.All(x => !string.IsNullOrWhiteSpace(x.OperatingCriteria)),
                "Operating criteria set for every SEU.", "No operating criteria for SEUs.", seus.Any(x => !string.IsNullOrWhiteSpace(x.OperatingCriteria)),
                "Some SEUs have no operating criteria."),
            Doc("8.2", "Design (energy performance in new and modified facilities)", "Design"),
            Doc("8.3", "Procurement of energy, services and equipment", "Procurement"),
            Check("9.1", "Monitoring, measurement, analysis; legal compliance", enpis.Count > 0 && docs.Any(d => d.Kind == "Legal"),
                "EnPIs are evaluated against expected consumption; legal register in place.", "Nothing monitored against a baseline.",
                enpis.Count > 0 || docs.Any(d => d.Kind == "Legal"), "Needs both EnPI monitoring and a legal requirements register."),
            Check("9.2", "Internal audit", lastAudit >= year, $"Last internal audit {lastAudit:yyyy-MM-dd}.", "No internal audit recorded.", lastAudit is not null,
                $"Last internal audit {lastAudit:yyyy-MM-dd} is more than a year ago."),
            Check("9.3", "Management review", lastReview >= year, $"Last management review {lastReview:yyyy-MM-dd}.", "No management review recorded.",
                lastReview is not null, $"Last management review {lastReview:yyyy-MM-dd} is more than a year ago."),
            Check("10.1", "Nonconformity and corrective action", overdueNc == 0 && (ncs.Count > 0 || audits.Count > 0),
                $"{ncs.Count} recorded, {openNc.Count} open, none overdue.",
                overdueNc > 0 ? $"{overdueNc} nonconformity(ies) past their due date." : "No audit or nonconformity records yet."),
            Check("10.2", "Continual improvement of energy performance", verified.Count > 0 || results.Any(r => r.SavingsKwh > 0),
                verified.Count > 0 ? $"{verified.Count} action(s) with verified savings." : "EnPIs show savings against the baseline.",
                "No demonstrated improvement yet."),
        };
        double Score(string s) => s switch { "ok" => 1, "warn" => 0.5, _ => 0 };

        var progress = objectives.Select(o =>
        {
            var r = results.FirstOrDefault(x => x.EnpiId == o.EnpiId);
            double? actual = r?.ImprovementPct;
            var state = actual is null ? "no EnPI" : actual >= (double)o.TargetPct ? "met" : actual > 0 ? "on track" : o.TargetDate < today ? "missed" : "behind";
            return new ObjectiveProgress(o.ObjectiveId, o.Title, o.TargetPct, o.TargetDate, actual, state);
        }).ToList();

        return new EnmsOverview(Math.Round(clauses.Average(c => Score(c.State)) * 100, 0), clauses, results, progress,
            actions.Count(a => a.Status is "Planned" or "InProgress"), Math.Round(actions.Sum(a => a.ExpectedKwhYear ?? 0), 0),
            Math.Round(verified.Sum(a => a.VerifiedKwhYear ?? 0), 0), openNc.Count, lastAudit, lastReview);
    }
}
