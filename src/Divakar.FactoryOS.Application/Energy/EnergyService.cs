using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Settings;

namespace Divakar.FactoryOS.Application.Energy;

/// <summary>A counter tag (energy, parts, gas volume…) on one asset and how much it rose in the period.</summary>
public sealed record CounterDelta(Guid AssetId, string AssetCode, string AssetName, string AssetTypeCode, string Category, string NodeLevel,
    Guid TagId, string SensorTypeCode, double Delta);

/// <summary>Highest and lowest counter reading of one tag inside one time bucket (bucket start in local time).</summary>
public sealed record CounterBucket(Guid TagId, DateTime Bucket, double MaxValue, double MinValue);

public sealed record DemandPeak(Guid AssetId, double PeakKw, DateTime At);

public sealed record IdleRow(Guid AssetId, string AssetCode, string AssetName, double IdleKwh, double IdleHours, double RunKwh);

public sealed record SignalStats(Guid AssetId, string AssetName, string AssetTypeCode, string SensorTypeCode, string Unit,
    double? Latest, double? Min, double? Max, double? Avg, DateTime? LatestAt);

public interface IEnergyRepository
{
    Task<IReadOnlyList<CounterDelta>> CounterDeltasAsync(Guid tenantId, IReadOnlyCollection<string> sensorTypes, DateTime from, DateTime to, CancellationToken ct);
    Task<IReadOnlyList<CounterBucket>> CounterBucketsAsync(Guid tenantId, IReadOnlyCollection<Guid> tagIds, DateTime from, DateTime to,
        int bucketMinutes, int utcOffsetMinutes, CancellationToken ct);
    Task<IReadOnlyList<DemandPeak>> DemandPeaksAsync(Guid tenantId, IReadOnlyCollection<Guid> assetIds, DateTime from, DateTime to, CancellationToken ct);
    Task<IReadOnlyList<IdleRow>> IdleEnergyAsync(Guid tenantId, DateTime from, DateTime to, CancellationToken ct);
    Task<IReadOnlyList<SignalStats>> SignalStatsAsync(Guid tenantId, IReadOnlyCollection<string> sensorTypes, DateTime from, DateTime to, CancellationToken ct);
}

public sealed record AssetEnergy(Guid AssetId, string Code, string Name, string AssetTypeCode, string Category, double Kwh, decimal Cost,
    double SharePct, double? Parts, double? KwhPerPart);

public sealed record HourEnergy(DateTime Hour, double Kwh, decimal Cost, bool Peak);

public sealed record EnergySummary(
    DateTime From, DateTime To, string Currency, double GridKwh, double SolarKwh, double ConsumptionKwh, decimal Cost, decimal AvgRate,
    double PeakKw, DateTime? PeakAt, decimal? ContractKw, double? ContractUsePct, double IdleKwh, decimal IdleCost, double Co2Kg,
    IReadOnlyList<string> MainMeters, IReadOnlyList<AssetEnergy> Meters, IReadOnlyList<AssetEnergy> Machines, IReadOnlyList<HourEnergy> Hourly,
    IReadOnlyList<IdleRow> Idle, IReadOnlyList<string> Insights);

public sealed record PqSignal(string SensorTypeCode, string Unit, double? Latest, double? Min, double? Max, double? Avg, string State, string? Note);

public sealed record PqPoint(Guid AssetId, string AssetName, string AssetTypeCode, DateTime? LatestAt, IReadOnlyList<PqSignal> Signals, string Worst);

public sealed record DayCarbon(DateTime Day, double GridKwh, double SolarKwh, double GasM3, double Scope1Kg, double Scope2Kg);

public sealed record CarbonReport(DateTime From, DateTime To, decimal ElectricityFactor, decimal GasFactor, double GridKwh, double SolarKwh,
    double GasM3, double Scope1Kg, double Scope2Kg, double AvoidedKg, double TotalKg, double? KgPerPart, double Parts,
    IReadOnlyList<DayCarbon> Daily, IReadOnlyList<AreaCarbon> ByArea);

public sealed record AreaCarbon(string Meter, double Kg);

/// <summary>
/// Energy, power quality and carbon, computed from the stored readings: energy counters (kWh, Nm³) give consumption by
/// difference, demand and power tags give peaks, machine state plus power gives idle waste.
/// </summary>
public sealed class EnergyService(IEnergyRepository repo, SettingsService settings, ModuleGate gate, ICurrentUser me)
{
    public static readonly string[] PqSensors = ["voltage_ln", "current", "power_factor", "frequency", "thd_voltage", "thd_current"];

    private static (DateTime From, DateTime To) Window(DateTime? from, DateTime? to, TimeSpan fallback)
    {
        var t = (to ?? DateTime.UtcNow).ToUniversalTime();
        var f = (from ?? t - fallback).ToUniversalTime();
        if (f >= t)
        {
            throw AppException.Validation("'from' must be before 'to'.");
        }
        if (t - f > TimeSpan.FromDays(400))
        {
            throw AppException.Validation("Choose a period of at most 400 days.");
        }
        return (f, t);
    }

    /// <summary>Main meters: the configured one, else energy meters on a Site or Plant node, else every energy meter.</summary>
    public static IReadOnlyList<CounterDelta> MainMeters(IReadOnlyList<CounterDelta> energy, Guid? configured)
    {
        var meters = energy.Where(e => e.AssetTypeCode == "energy-meter").ToList();
        if (configured is { } id && meters.Any(m => m.AssetId == id))
        {
            return [.. meters.Where(m => m.AssetId == id)];
        }
        var top = meters.Where(m => m.NodeLevel is "Site" or "Plant").ToList();
        return top.Count > 0 ? top : meters;
    }

    /// <summary>Turns per-bucket max/min counter readings into consumption per bucket (summed over tags).</summary>
    public static IReadOnlyList<(DateTime Bucket, double Value)> Consumption(IEnumerable<CounterBucket> buckets)
    {
        var total = new SortedDictionary<DateTime, double>();
        foreach (var tag in buckets.GroupBy(b => b.TagId))
        {
            double? prevMax = null;
            foreach (var b in tag.OrderBy(b => b.Bucket))
            {
                var used = Math.Max(0, b.MaxValue - (prevMax ?? b.MinValue));
                // a counter that went backwards (meter replaced or reset) counts from its new value
                if (prevMax is { } p && b.MinValue < p)
                {
                    used = Math.Max(0, b.MaxValue - b.MinValue);
                }
                total[b.Bucket] = total.GetValueOrDefault(b.Bucket) + used;
                prevMax = b.MaxValue;
            }
        }
        return [.. total.Select(kv => (kv.Key, kv.Value))];
    }

    public async Task<EnergySummary> SummaryAsync(DateTime? from, DateTime? to, CancellationToken ct)
    {
        await gate.EnsureAsync("energy", ct);
        var tenantId = me.RequireTenant();
        var s = await settings.GetAsync(ct);
        var (f, t) = Window(from, to, TimeSpan.FromHours(24));
        var zone = s.Zone();
        var offset = (int)zone.GetUtcOffset(t).TotalMinutes;

        var deltas = await repo.CounterDeltasAsync(tenantId, ["energy_kwh", "part_count"], f, t, ct);
        var energy = deltas.Where(d => d.SensorTypeCode == "energy_kwh").ToList();
        var parts = deltas.Where(d => d.SensorTypeCode == "part_count").GroupBy(d => d.AssetId).ToDictionary(g => g.Key, g => g.Sum(x => x.Delta));
        var main = MainMeters(energy, s.MainMeterAssetId);
        var solar = energy.Where(e => e.AssetTypeCode == "solar-inverter").Sum(e => e.Delta);

        // hourly profile and cost from the main meters (or all machines when there is no meter)
        var costBasis = main.Count > 0 ? main : energy.Where(e => e.Category != "Energy").ToList();
        var buckets = Consumption(await repo.CounterBucketsAsync(tenantId, [.. costBasis.Select(m => m.TagId)], f, t, 60, offset, ct));
        var hourly = buckets
            .Select(h => new HourEnergy(h.Bucket, Math.Round(h.Value, 2), Math.Round((decimal)h.Value * s.RateAt(h.Bucket), 1), s.IsPeak(h.Bucket)))
            .ToList();
        var grid = costBasis.Sum(m => m.Delta);
        var exactCost = buckets.Sum(h => (decimal)h.Value * s.RateAt(h.Bucket));
        var cost = Math.Round(exactCost, 0);
        var avgRate = grid > 0 ? Math.Round(exactCost / (decimal)grid, 2) : s.OffPeakRate;

        var peaks = await repo.DemandPeaksAsync(tenantId, [.. main.Select(m => m.AssetId)], f, t, ct);
        var peak = peaks.OrderByDescending(p => p.PeakKw).FirstOrDefault();
        // idle analysis looks minute by minute, so it covers at most the last 8 days of the period
        var idle = (await repo.IdleEnergyAsync(tenantId, t - f > TimeSpan.FromDays(8) ? t.AddDays(-8) : f, t, ct)).Where(i => i.IdleKwh > 0.01).OrderByDescending(i => i.IdleKwh).ToList();
        var idleKwh = idle.Sum(i => i.IdleKwh);

        AssetEnergy Row(CounterDelta d, double basis)
        {
            var p = parts.TryGetValue(d.AssetId, out var n) && n > 0 ? n : (double?)null;
            return new AssetEnergy(d.AssetId, d.AssetCode, d.AssetName, d.AssetTypeCode, d.Category, Math.Round(d.Delta, 2),
                Math.Round((decimal)d.Delta * avgRate, 0), basis > 0 ? Math.Round(d.Delta / basis * 100, 1) : 0, p,
                p is { } pp ? Math.Round(d.Delta / pp, 3) : null);
        }
        var machinesTotal = energy.Where(e => e.Category != "Energy").Sum(e => e.Delta);
        var meters = energy.Where(e => e.Category == "Energy").Select(e => Row(e, grid)).OrderByDescending(r => r.Kwh).ToList();
        var machines = energy.Where(e => e.Category != "Energy").Select(e => Row(e, machinesTotal)).OrderByDescending(r => r.Kwh).ToList();

        var insights = new List<string>();
        var contractPct = s.ContractKw is > 0 && peak is not null ? Math.Round(peak.PeakKw / (double)s.ContractKw.Value * 100, 1) : (double?)null;
        if (contractPct > 100)
        {
            insights.Add($"Peak demand {peak!.PeakKw:N0} kW exceeded the contract capacity of {s.ContractKw:N0} kW — expect a penalty; shift loads or raise the contract.");
        }
        else if (contractPct > 90)
        {
            insights.Add($"Peak demand reached {contractPct}% of the contract capacity — close to the limit.");
        }
        if (grid > 0 && idleKwh / grid > 0.05)
        {
            insights.Add($"Machines used {idleKwh:N0} kWh ({idleKwh / grid:P0} of the total) while idle or stopped — about {s.Currency} {(decimal)idleKwh * avgRate:N0}. Switch off or use sleep modes between jobs.");
        }
        var peakShare = hourly.Sum(h => h.Kwh) > 0 ? hourly.Where(h => h.Peak).Sum(h => h.Kwh) / hourly.Sum(h => h.Kwh) : 0;
        if (peakShare > 0.4)
        {
            insights.Add($"{peakShare:P0} of energy was used in peak hours ({s.PeakStartHour}:00–{s.PeakEndHour}:00). Moving flexible loads (compressors charging receivers, chillers, batch jobs) to off-peak saves {s.PeakRate - s.OffPeakRate:N2} per kWh.");
        }
        var worst = machines.Where(m => m.KwhPerPart is not null).OrderByDescending(m => m.KwhPerPart).FirstOrDefault();
        if (worst is not null)
        {
            insights.Add($"Highest energy per part: {worst.Name} at {worst.KwhPerPart:N3} kWh/part.");
        }
        if (solar > 0)
        {
            insights.Add($"Solar produced {solar:N0} kWh ({solar / Math.Max(grid + solar, 1):P0} of consumption), avoiding {solar * (double)s.ElectricityFactor:N0} kg CO₂e.");
        }

        return new EnergySummary(f, t, s.Currency, Math.Round(grid, 1), Math.Round(solar, 1), Math.Round(grid + solar, 1), cost, avgRate,
            Math.Round(peak?.PeakKw ?? 0, 1), peak?.At, s.ContractKw, contractPct, Math.Round(idleKwh, 1), Math.Round((decimal)idleKwh * avgRate, 0),
            Math.Round(grid * (double)s.ElectricityFactor, 1), [.. main.Select(m => m.AssetName)], meters, machines, hourly, idle, insights);
    }

    /// <summary>Grades one power-quality value: ok, warn or bad, with a short reason.</summary>
    public static (string State, string? Note) Grade(string sensor, double? v) => (sensor, v) switch
    {
        (_, null) => ("muted", "no data"),
        ("power_factor", < 0.8) => ("bad", "below 0.80: utility penalty range"),
        ("power_factor", < 0.9) => ("warn", "below 0.90: add capacitor banks"),
        ("thd_voltage", > 8) => ("bad", "above 8 % (IEEE 519 limit for ≤1 kV)"),
        ("thd_voltage", > 5) => ("warn", "above 5 %: harmonic filtering advised"),
        ("thd_current", > 20) => ("bad", "above 20 %: check drives and rectifiers"),
        ("thd_current", > 12) => ("warn", "above 12 %"),
        ("frequency", < 59.5 or > 60.5) => ("bad", "outside 59.5–60.5 Hz"),
        ("voltage_ln", < 198 or > 242) => ("bad", "outside ±10 % of 220 V"),
        ("voltage_ln", < 209 or > 231) => ("warn", "outside ±5 % of 220 V"),
        _ => ("ok", null),
    };

    public async Task<IReadOnlyList<PqPoint>> PowerQualityAsync(CancellationToken ct)
    {
        await gate.EnsureAsync("power", ct);
        var t = DateTime.UtcNow;
        var stats = await repo.SignalStatsAsync(me.RequireTenant(), PqSensors, t.AddHours(-24), t.AddMinutes(5), ct);
        string[] order = ["bad", "warn", "ok", "muted"];
        return [.. stats.GroupBy(x => x.AssetId).Select(g =>
        {
            var signals = g.OrderBy(x => Array.IndexOf(PqSensors, x.SensorTypeCode)).Select(x =>
            {
                // judge the worst value of the day for limits that matter at any moment
                var judged = x.SensorTypeCode switch
                {
                    "power_factor" => x.Min,
                    "thd_voltage" or "thd_current" => x.Max,
                    _ => x.Latest,
                };
                var (state, note) = Grade(x.SensorTypeCode, judged);
                return new PqSignal(x.SensorTypeCode, x.Unit, Round(x.Latest), Round(x.Min), Round(x.Max), Round(x.Avg), state, note);
            }).ToList();
            var first = g.First();
            return new PqPoint(first.AssetId, first.AssetName, first.AssetTypeCode, g.Max(x => x.LatestAt), signals,
                signals.Select(sg => sg.State).OrderBy(st => Array.IndexOf(order, st)).First());
        }).OrderBy(p => p.AssetTypeCode == "power-analyzer" ? 0 : 1).ThenBy(p => p.AssetName)];
    }

    private static double? Round(double? v) => v is { } x ? Math.Round(x, 3) : null;

    public async Task<CarbonReport> CarbonAsync(DateTime? from, DateTime? to, CancellationToken ct)
    {
        await gate.EnsureAsync("carbon", ct);
        var tenantId = me.RequireTenant();
        var s = await settings.GetAsync(ct);
        var (f, t) = Window(from, to, TimeSpan.FromDays(30));
        var offset = (int)s.Zone().GetUtcOffset(t).TotalMinutes;
        var ef = (double)s.ElectricityFactor;
        var gf = (double)s.GasFactor;

        var deltas = await repo.CounterDeltasAsync(tenantId, ["energy_kwh", "volume_gas", "part_count"], f, t, ct);
        var energy = deltas.Where(d => d.SensorTypeCode == "energy_kwh").ToList();
        var main = MainMeters(energy, s.MainMeterAssetId);
        var gridBasis = main.Count > 0 ? main : energy.Where(e => e.Category != "Energy").ToList();
        var solarTags = energy.Where(e => e.AssetTypeCode == "solar-inverter").ToList();
        var gasTags = deltas.Where(d => d.SensorTypeCode == "volume_gas").ToList();
        var grid = gridBasis.Sum(m => m.Delta);
        var solar = solarTags.Sum(x => x.Delta);
        var gas = gasTags.Sum(x => x.Delta);
        var parts = deltas.Where(d => d.SensorTypeCode == "part_count" && d.AssetTypeCode != "agv" && d.Category == "Production").Sum(d => d.Delta);

        async Task<Dictionary<DateTime, double>> Daily(IEnumerable<CounterDelta> tags) =>
            Consumption(await repo.CounterBucketsAsync(tenantId, [.. tags.Select(x => x.TagId)], f, t, 1440, offset, ct)).ToDictionary(x => x.Bucket, x => x.Value);
        var dg = await Daily(gridBasis);
        var ds = await Daily(solarTags);
        var dgas = await Daily(gasTags);
        var days = dg.Keys.Union(ds.Keys).Union(dgas.Keys).Order()
            .Select(d => new DayCarbon(d, Math.Round(dg.GetValueOrDefault(d), 1), Math.Round(ds.GetValueOrDefault(d), 1), Math.Round(dgas.GetValueOrDefault(d), 2),
                Math.Round(dgas.GetValueOrDefault(d) * gf, 1), Math.Round(dg.GetValueOrDefault(d) * ef, 1))).ToList();

        // Scope 2 by area: each area's sub-meters, as a share of the grid total
        var byArea = energy.Where(e => e.AssetTypeCode == "energy-meter" && e.NodeLevel == "Area")
            .Select(e => new AreaCarbon(e.AssetName, Math.Round(e.Delta * ef, 1))).OrderByDescending(x => x.Kg).ToList();

        var scope1 = gas * gf;
        var scope2 = grid * ef;
        return new CarbonReport(f, t, s.ElectricityFactor, s.GasFactor, Math.Round(grid, 1), Math.Round(solar, 1), Math.Round(gas, 2),
            Math.Round(scope1, 1), Math.Round(scope2, 1), Math.Round(solar * ef, 1), Math.Round(scope1 + scope2, 1),
            parts > 0 ? Math.Round((scope1 + scope2) / parts, 4) : null, Math.Round(parts, 0), days, byArea);
    }
}
