using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Alarms;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Application.Settings;

namespace Divakar.FactoryOS.Application.Energy;

public sealed record DayCost(DateTime Day, double Kwh, decimal Cost, bool Forecast);

/// <summary>This month's electricity bill so far and where it is heading.</summary>
public sealed record BillForecast(string Currency, DateTime MonthStart, int DaysElapsed, int DaysInMonth, double KwhToDate, decimal EnergyCostToDate,
    double ForecastKwh, decimal ForecastEnergyCost, decimal BasicCharge, double PeakKw, decimal? ContractKw, double ExcessKw, decimal ExcessPenalty,
    decimal ForecastTotal, decimal? DemandChargePerKw, IReadOnlyList<DayCost> Days, IReadOnlyList<string> Notes);

public sealed record ShiftEnergy(string Shift, DateTime From, DateTime To, double Kwh, decimal Cost, double PeakKwh, IReadOnlyList<AssetEnergy> TopMachines);

public sealed record LoadShedRule(Guid RuleId, string Name, Guid AssetId, string AssetName, byte Priority, decimal ShedAboveKw, decimal RestoreBelowKw,
    string Action, Guid? WriteTagId, string? WriteTagName, double? ShedValue, double? RestoreValue, bool IsActive, bool ShedNow);

public sealed record SaveLoadShedRuleRequest(string Name, Guid AssetId, byte Priority, decimal ShedAboveKw, decimal RestoreBelowKw, string Action,
    Guid? WriteTagId, double? ShedValue, double? RestoreValue, bool IsActive = true);

public sealed record ShedEvent(long EventId, Guid RuleId, string RuleName, string AssetName, double DemandKw, DateTime ShedAt, DateTime? RestoredAt, string? Result);

/// <summary>A tenant's load-shed state for one pass of the engine: demand now, its rules, and which are shed.</summary>
public sealed record ShedRuleState(Guid TenantId, Guid RuleId, string Name, Guid AssetId, string AssetName, byte Priority, double ShedAboveKw, double RestoreBelowKw,
    string Action, Guid? WriteTagId, double? ShedValue, double? RestoreValue, long? OpenEventId, DateTime? ShedAt);

public sealed record TenantDemand(Guid TenantId, double DemandKw, DateTime Ts);

public interface ILoadShedRepository
{
    Task<IReadOnlyList<LoadShedRule>> RulesAsync(Guid tenantId, CancellationToken ct);
    Task<Guid> SaveRuleAsync(Guid tenantId, Guid? id, SaveLoadShedRuleRequest r, CancellationToken ct);
    Task DeleteRuleAsync(Guid tenantId, Guid id, CancellationToken ct);
    Task<IReadOnlyList<ShedEvent>> EventsAsync(Guid tenantId, int take, CancellationToken ct);
    Task<bool> TagOnAssetAsync(Guid tenantId, Guid tagId, Guid assetId, CancellationToken ct);

    // worker side (system connection)
    Task<IReadOnlyList<TenantDemand>> DemandsAsync(CancellationToken ct);
    Task<IReadOnlyList<ShedRuleState>> StatesAsync(CancellationToken ct);
    Task<long> OpenEventAsync(Guid tenantId, Guid ruleId, double demandKw, string result, CancellationToken ct);
    Task CloseEventAsync(long eventId, string result, CancellationToken ct);
}

/// <summary>Writes a value to a device (Modbus register/coil, MQTT topic). Used to switch loads off and on.</summary>
public interface IConnectorWriter
{
    bool Supports(string protocolCode);
    Task WriteAsync(AssetBinding asset, TagBinding tag, double value, CancellationToken ct);
}

/// <summary>The device binding (endpoint, settings, tags) of one asset, for writing.</summary>
public interface IAssetBindingSource
{
    Task<AssetBinding?> BindingAsync(Guid assetId, CancellationToken ct);
}

public sealed class EnergyPlanningService(IEnergyRepository repo, ILoadShedRepository shed, SettingsService settings, ModuleGate gate, ICurrentUser me, IAuditLog audit)
{
    /// <summary>
    /// Taiwan-style penalty for exceeding the contract capacity: the excess up to 10 % of the contract pays twice the basic
    /// charge, anything beyond pays three times.
    /// </summary>
    public static decimal ExcessPenalty(double peakKw, decimal? contractKw, decimal? chargePerKw)
    {
        if (contractKw is not > 0 || chargePerKw is not > 0 || peakKw <= (double)contractKw.Value)
        {
            return 0;
        }
        var excess = (decimal)peakKw - contractKw.Value;
        var band = contractKw.Value * 0.10m;
        return Math.Round(Math.Min(excess, band) * chargePerKw.Value * 2 + Math.Max(0, excess - band) * chargePerKw.Value * 3, 0);
    }

    /// <summary>Remaining days are projected from the average of the last (up to) 7 complete days.</summary>
    public static (double Kwh, decimal Cost) Project(IReadOnlyList<DayCost> actual, int remainingDays)
    {
        var basis = actual.Where(d => !d.Forecast).OrderByDescending(d => d.Day).Skip(actual.Count > 1 ? 1 : 0).Take(7).ToList();   // skip today (partial)
        if (basis.Count == 0 || remainingDays <= 0)
        {
            return (0, 0);
        }
        return (basis.Average(d => d.Kwh) * remainingDays, Math.Round(basis.Average(d => d.Cost) * remainingDays, 0));
    }

    public async Task<BillForecast> ForecastAsync(CancellationToken ct)
    {
        await gate.EnsureAsync("energy", ct);
        var tenantId = me.RequireTenant();
        var s = await settings.GetAsync(ct);
        var zone = s.Zone();
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        var monthStartLocal = new DateTime(nowLocal.Year, nowLocal.Month, 1);
        var from = TimeZoneInfo.ConvertTimeToUtc(monthStartLocal, zone);
        var to = DateTime.UtcNow;
        var offset = (int)zone.GetUtcOffset(to).TotalMinutes;

        var energy = (await repo.CounterDeltasAsync(tenantId, ["energy_kwh"], from, to, ct)).ToList();
        var main = EnergyService.MainMeters(energy, s.MainMeterAssetId);
        var basis = main.Count > 0 ? main : energy.Where(e => e.Category != "Energy").ToList();
        var hourly = EnergyService.Consumption(await repo.CounterBucketsAsync(tenantId, [.. basis.Select(b => b.TagId)], from, to, 60, offset, ct));
        var days = hourly.GroupBy(h => h.Bucket.Date)
            .Select(g => new DayCost(g.Key, Math.Round(g.Sum(h => h.Value), 1), Math.Round(g.Sum(h => (decimal)h.Value * s.RateAt(h.Bucket)), 0), false)).OrderBy(d => d.Day).ToList();

        var daysInMonth = DateTime.DaysInMonth(nowLocal.Year, nowLocal.Month);
        var remaining = daysInMonth - nowLocal.Day;
        var (fKwh, fCost) = Project(days, remaining);
        var perDayKwh = remaining > 0 ? fKwh / remaining : 0;
        var perDayCost = remaining > 0 ? fCost / remaining : 0;
        var forecastDays = Enumerable.Range(1, Math.Max(0, remaining)).Select(i => new DayCost(nowLocal.Date.AddDays(i), Math.Round(perDayKwh, 1), Math.Round(perDayCost, 0), true));

        var peaks = await repo.DemandPeaksAsync(tenantId, [.. main.Select(m => m.AssetId)], from, to, ct);
        var peak = peaks.Count > 0 ? peaks.Max(p => p.PeakKw) : 0;
        var basic = s.ContractKw is { } c && s.DemandChargePerKw is { } r ? Math.Round(c * r, 0) : 0;
        var penalty = ExcessPenalty(peak, s.ContractKw, s.DemandChargePerKw);
        var kwhToDate = days.Sum(d => d.Kwh);
        var costToDate = days.Sum(d => d.Cost);
        var notes = new List<string>();
        if (s.DemandChargePerKw is null)
        {
            notes.Add("Add the basic charge per contract kW in Settings to include it and any excess-demand penalty.");
        }
        if (penalty > 0)
        {
            notes.Add($"Peak demand {peak:N0} kW is above the {s.ContractKw:N0} kW contract: about {s.Currency} {penalty:N0} penalty this month. Load shedding can prevent this.");
        }
        if (days.Count < 3)
        {
            notes.Add("Less than three days of data this month — the forecast will settle as more days are recorded.");
        }
        return new BillForecast(s.Currency, monthStartLocal, nowLocal.Day, daysInMonth, Math.Round(kwhToDate, 1), costToDate, Math.Round(kwhToDate + fKwh, 0),
            costToDate + fCost, basic, Math.Round(peak, 1), s.ContractKw, s.ContractKw is { } ck ? Math.Round(Math.Max(0, peak - (double)ck), 1) : 0, penalty,
            costToDate + fCost + basic + penalty, s.DemandChargePerKw, [.. days, .. forecastDays], notes);
    }

    /// <summary>Energy and cost of each configured shift on a day, with the biggest machines.</summary>
    public async Task<IReadOnlyList<ShiftEnergy>> ShiftsAsync(DateOnly? date, CancellationToken ct)
    {
        await gate.EnsureAsync("energy", ct);
        var tenantId = me.RequireTenant();
        var s = await settings.GetAsync(ct);
        var zone = s.Zone();
        var day = date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
        var result = new List<ShiftEnergy>();
        foreach (var sh in s.Shifts)
        {
            var startLocal = day.ToDateTime(TimeOnly.Parse(sh.Start));
            var endLocal = day.ToDateTime(TimeOnly.Parse(sh.End));
            if (endLocal <= startLocal)
            {
                endLocal = endLocal.AddDays(1);
            }
            var f = TimeZoneInfo.ConvertTimeToUtc(startLocal, zone);
            var t = TimeZoneInfo.ConvertTimeToUtc(endLocal, zone);
            if (f >= DateTime.UtcNow)
            {
                result.Add(new ShiftEnergy(sh.Name, startLocal, endLocal, 0, 0, 0, []));
                continue;
            }
            var energy = (await repo.CounterDeltasAsync(tenantId, ["energy_kwh"], f, t, ct)).ToList();
            var main = EnergyService.MainMeters(energy, s.MainMeterAssetId);
            var basis = main.Count > 0 ? main : energy.Where(e => e.Category != "Energy").ToList();
            var hourly = EnergyService.Consumption(await repo.CounterBucketsAsync(tenantId, [.. basis.Select(b => b.TagId)], f, t, 60, (int)zone.GetUtcOffset(f).TotalMinutes, ct));
            var kwh = hourly.Sum(h => h.Value);
            var cost = Math.Round(hourly.Sum(h => (decimal)h.Value * s.RateAt(h.Bucket)), 0);
            var avg = kwh > 0 ? cost / (decimal)kwh : s.OffPeakRate;
            var machines = energy.Where(e => e.Category != "Energy").OrderByDescending(e => e.Delta).Take(5)
                .Select(e => new AssetEnergy(e.AssetId, e.AssetCode, e.AssetName, e.AssetTypeCode, e.Category, Math.Round(e.Delta, 1), Math.Round((decimal)e.Delta * avg, 0),
                    kwh > 0 ? Math.Round(e.Delta / kwh * 100, 1) : 0, null, null)).ToList();
            result.Add(new ShiftEnergy(sh.Name, startLocal, endLocal, Math.Round(kwh, 1), cost, Math.Round(hourly.Count > 0 ? hourly.Max(h => h.Value) : 0, 1), machines));
        }
        return result;
    }

    public async Task<IReadOnlyList<LoadShedRule>> RulesAsync(CancellationToken ct)
    {
        await gate.EnsureAsync("power", ct);
        return await shed.RulesAsync(me.RequireTenant(), ct);
    }

    public async Task<Guid> SaveRuleAsync(Guid? id, SaveLoadShedRuleRequest r, CancellationToken ct)
    {
        await gate.EnsureAsync("power", ct);
        var tenantId = me.RequireTenant();
        if (r.RestoreBelowKw >= r.ShedAboveKw || r.RestoreBelowKw < 0)
        {
            throw AppException.Validation("Restore below must be lower than shed above (a gap stops the load switching back and forth).");
        }
        if (r.Priority is < 1 or > 99)
        {
            throw AppException.Validation("Priority is 1 (shed first) to 99.");
        }
        if (r.Action is not ("Notify" or "Write"))
        {
            throw AppException.Validation("Action is Notify or Write.");
        }
        if (r.Action == "Write")
        {
            if (r.WriteTagId is not { } tag || r.ShedValue is null || r.RestoreValue is null)
            {
                throw AppException.Validation("To switch a load, choose the tag to write and the values for off (shed) and on (restore).");
            }
            if (!await shed.TagOnAssetAsync(tenantId, tag, r.AssetId, ct))
            {
                throw AppException.NotFound("Tag on this asset");
            }
        }
        if (id is { } existing && (await shed.RulesAsync(tenantId, ct)).All(x => x.RuleId != existing))
        {
            throw AppException.NotFound("Rule");
        }
        var saved = await shed.SaveRuleAsync(tenantId, id, r with
        {
            Name = Guard.Required(r.Name, "Name", 150), WriteTagId = r.Action == "Write" ? r.WriteTagId : null,
            ShedValue = r.Action == "Write" ? r.ShedValue : null, RestoreValue = r.Action == "Write" ? r.RestoreValue : null,
        }, ct);
        await audit.WriteAsync(id is null ? "loadshed.create" : "loadshed.update", "loadshed", saved.ToString(), null, ct);
        return saved;
    }

    public async Task DeleteRuleAsync(Guid id, CancellationToken ct)
    {
        await gate.EnsureAsync("power", ct);
        var tenantId = me.RequireTenant();
        if ((await shed.RulesAsync(tenantId, ct)).All(x => x.RuleId != id))
        {
            throw AppException.NotFound("Rule");
        }
        await shed.DeleteRuleAsync(tenantId, id, ct);
    }

    public async Task<IReadOnlyList<ShedEvent>> EventsAsync(CancellationToken ct)
    {
        await gate.EnsureAsync("power", ct);
        return await shed.EventsAsync(me.RequireTenant(), 200, ct);
    }
}

/// <summary>
/// Worker job, every 10 seconds per company: when the main meter's demand passes a rule's shed level, sheds one more load
/// (priority 1 first); when demand has fallen below a shed load's restore level and the load has been off at least
/// 2 minutes, restores one load (the last shed first). One step per pass, so demand settles between steps.
/// </summary>
public sealed class LoadShedEngine(ILoadShedRepository repo, IEnumerable<IConnectorWriter> writers, IAssetBindingSource bindings, IAlarmNotifier notifier)
{
    public static readonly TimeSpan MinOff = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(2);

    /// <summary>The next action for one company: which rule to shed or restore, or none.</summary>
    public static (ShedRuleState? Shed, ShedRuleState? Restore) Decide(double demandKw, IReadOnlyList<ShedRuleState> rules, DateTime now)
    {
        var next = rules.Where(r => r.OpenEventId is null && demandKw > r.ShedAboveKw).OrderBy(r => r.Priority).FirstOrDefault();
        if (next is not null)
        {
            return (next, null);
        }
        var back = rules.Where(r => r.OpenEventId is not null && demandKw < r.RestoreBelowKw && r.ShedAt is { } at && now - at >= MinOff)
            .OrderByDescending(r => r.Priority).ThenByDescending(r => r.ShedAt).FirstOrDefault();
        return (null, back);
    }

    public async Task<int> RunOnceAsync(DateTime now, CancellationToken ct)
    {
        var demands = (await repo.DemandsAsync(ct)).Where(d => now - d.Ts <= Stale).ToDictionary(d => d.TenantId);
        var steps = 0;
        foreach (var group in (await repo.StatesAsync(ct)).GroupBy(r => r.TenantId))
        {
            if (!demands.TryGetValue(group.Key, out var demand))
            {
                continue;
            }
            var (toShed, toRestore) = Decide(demand.DemandKw, [.. group], now);
            if (toShed is { } s)
            {
                var result = await ApplyAsync(s, s.ShedValue, ct);
                await repo.OpenEventAsync(s.TenantId, s.RuleId, demand.DemandKw, result, ct);
                await notifier.NotifyAsync(s.TenantId, $"[Load shed] {s.AssetName} switched off", $"Demand {demand.DemandKw:N0} kW passed {s.ShedAboveKw:N0} kW — {s.Name}. {result}",
                    new { loadShed = true, rule = s.Name, asset = s.AssetName, demandKw = demand.DemandKw }, ct);
                steps++;
            }
            else if (toRestore is { } r)
            {
                var result = await ApplyAsync(r, r.RestoreValue, ct);
                await repo.CloseEventAsync(r.OpenEventId!.Value, result, ct);
                await notifier.NotifyAsync(r.TenantId, $"[Load restored] {r.AssetName}", $"Demand is back to {demand.DemandKw:N0} kW — {r.Name}. {result}",
                    new { loadRestored = true, rule = r.Name, asset = r.AssetName, demandKw = demand.DemandKw }, ct);
                steps++;
            }
        }
        return steps;
    }

    private async Task<string> ApplyAsync(ShedRuleState rule, double? value, CancellationToken ct)
    {
        if (rule.Action != "Write" || rule.WriteTagId is not { } tagId || value is not { } v)
        {
            return "Notification sent (no automatic switching).";
        }
        try
        {
            var binding = await bindings.BindingAsync(rule.AssetId, ct) ?? throw new InvalidOperationException("the asset has no connection");
            var tag = binding.Tags.FirstOrDefault(t => t.TagId == tagId) ?? throw new InvalidOperationException("the tag is not active");
            var writer = writers.FirstOrDefault(w => w.Supports(binding.ProtocolCode))
                         ?? throw new InvalidOperationException($"writing over {binding.ProtocolCode} is not supported (Modbus TCP and MQTT are)");
            await writer.WriteAsync(binding, tag, v, ct);
            return $"Wrote {v} to {tag.Code}.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Could not write: {ex.Message}";
        }
    }
}
