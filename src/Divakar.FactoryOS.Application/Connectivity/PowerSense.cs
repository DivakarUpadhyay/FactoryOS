using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Domain.Plant;

namespace Divakar.FactoryOS.Application.Connectivity;

public sealed record PowerProfile(Guid AssetId, Guid TenantId, Guid SourceTagId, string SourceTagName, string SourceUnit, Guid StateTagId, Guid? CountTagId,
    double OffBelow, double RunAbove, bool CountCycles, int MinCycleSec, bool AutoLearn, DateTime? LearnedAt, int? LearnedFrom, DateTime? LastTs,
    bool LastAbove, DateTime? LastCycleAt, double CycleCount);

public sealed record SavePowerProfileRequest(bool Enabled, Guid? SourceTagId, double? OffBelow, double? RunAbove, bool CountCycles = true, int MinCycleSec = 5,
    bool AutoLearn = true);

public sealed record LearnResult(double OffBelow, double RunAbove, double OffLevel, double IdleLevel, double RunLevel, int Readings, string Note);

public sealed record SourceReading(DateTime Ts, double Value);

/// <summary>Virtual tags written by power sense carry this address; connectors never read them.</summary>
public static class PowerSenseTags
{
    public const string Prefix = "virtual:";
    public const string State = "virtual:power-sense-state";
    public const string Count = "virtual:power-sense-cycles";
}

public interface IPowerSenseRepository
{
    Task<PowerProfile?> ProfileAsync(Guid tenantId, Guid assetId, CancellationToken ct);
    Task<IReadOnlyList<(Guid TagId, string Code, string Name, string SensorTypeCode, string Unit)>> PowerTagsAsync(Guid tenantId, Guid assetId, CancellationToken ct);
    Task SaveAsync(Guid tenantId, Guid assetId, Guid sourceTagId, double offBelow, double runAbove, bool countCycles, int minCycleSec, bool autoLearn, CancellationToken ct);
    Task DisableAsync(Guid tenantId, Guid assetId, CancellationToken ct);
    Task<decimal?> RatedKwAsync(Guid tenantId, Guid assetId, CancellationToken ct);

    // worker side (system connection)
    Task<IReadOnlyList<PowerProfile>> AllAsync(CancellationToken ct);
    Task<IReadOnlyList<SourceReading>> ReadingsAsync(Guid tagId, DateTime? after, DateTime? from, int max, CancellationToken ct);
    Task SaveStateAsync(Guid assetId, DateTime lastTs, bool lastAbove, DateTime? lastCycleAt, double cycleCount, CancellationToken ct);
    Task SaveLearnedAsync(Guid assetId, double offBelow, double runAbove, int readings, CancellationToken ct);
}

/// <summary>
/// Machine state and cycles from one power or current signal. Off / idle / running bands are learned from the machine's own
/// history with 1-D k-means (three groups); a cycle is a rise above the running level, at least MinCycleSec after the last one.
/// </summary>
public static class PowerSenseMath
{
    /// <summary>Learns the bands from readings; falls back to fractions of rated power when the history is too short.</summary>
    public static LearnResult Learn(IReadOnlyList<double> values, double? ratedKw)
    {
        if (values.Count < 60)
        {
            var rated = ratedKw is > 0 ? ratedKw.Value : Math.Max(1, values.DefaultIfEmpty(10).Max());
            return new LearnResult(Math.Round(rated * 0.05, 3), Math.Round(rated * 0.35, 3), 0, rated * 0.15, rated * 0.6, values.Count,
                "Not enough history yet — bands set from the rated power (5 % off, 35 % running). They are learned automatically after about an hour of data.");
        }
        var sorted = values.Order().ToArray();
        double P(double q) => sorted[(int)Math.Clamp(Math.Round(q * (sorted.Length - 1)), 0, sorted.Length - 1)];
        var c = new[] { P(0.05), P(0.5), P(0.95) };
        for (var iter = 0; iter < 50; iter++)
        {
            var groups = sorted.GroupBy(v => Enumerable.Range(0, 3).MinBy(i => Math.Abs(v - c[i]))).ToDictionary(g => g.Key, g => g.Average());
            var next = Enumerable.Range(0, 3).Select(i => groups.TryGetValue(i, out var m) ? m : c[i]).Order().ToArray();
            if (next.Zip(c).All(p => Math.Abs(p.First - p.Second) < 1e-9))
            {
                break;
            }
            c = next;
        }
        var spread = c[2] - c[0];
        if (spread < Math.Max(0.05 * Math.Max(c[2], 1e-6), 1e-6))
        {
            return new LearnResult(Math.Round(c[1] * 0.5, 3), Math.Round(c[1] * 0.8 + 1e-6, 3), c[0], c[1], c[2], values.Count,
                "The machine drew almost the same power the whole time — run it through its normal cycle and learn again.");
        }
        // two real levels only (e.g. off and running): treat the middle group as belonging to the nearer one
        var off = (c[0] + c[1]) / 2;
        var run = (c[1] + c[2]) / 2;
        if (c[1] - c[0] < 0.1 * spread)
        {
            off = c[0] + 0.25 * spread;
            run = c[0] + 0.5 * spread;
        }
        else if (c[2] - c[1] < 0.1 * spread)
        {
            run = c[0] + 0.5 * spread;
            off = c[0] + 0.25 * spread;
        }
        return new LearnResult(Math.Round(off, 3), Math.Round(run, 3), Math.Round(c[0], 3), Math.Round(c[1], 3), Math.Round(c[2], 3), values.Count,
            "Learned from the machine's own power: off, idle and running levels.");
    }

    public static int StateOf(double value, double offBelow, double runAbove) =>
        value < offBelow ? MachineStates.Stopped : value < runAbove ? MachineStates.Idle : MachineStates.Running;

    /// <summary>Turns new source readings into state values and the running cycle count.</summary>
    public static (List<(DateTime Ts, int State, double Count)> Rows, bool LastAbove, DateTime? LastCycleAt, double Count) Step(
        IReadOnlyList<SourceReading> readings, double offBelow, double runAbove, bool countCycles, int minCycleSec, bool lastAbove, DateTime? lastCycleAt, double count)
    {
        var rows = new List<(DateTime, int, double)>(readings.Count);
        foreach (var r in readings.OrderBy(r => r.Ts))
        {
            var above = r.Value >= runAbove;
            if (countCycles && above && !lastAbove && (lastCycleAt is null || (r.Ts - lastCycleAt.Value).TotalSeconds >= minCycleSec))
            {
                count++;
                lastCycleAt = r.Ts;
            }
            lastAbove = above;
            rows.Add((r.Ts, StateOf(r.Value, offBelow, runAbove), count));
        }
        return (rows, lastAbove, lastCycleAt, count);
    }
}

public sealed class PowerSenseService(IPowerSenseRepository repo, ICurrentUser me, IAuditLog audit)
{
    public async Task<object> GetAsync(Guid assetId, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        var tags = await repo.PowerTagsAsync(tenantId, assetId, ct);
        var profile = await repo.ProfileAsync(tenantId, assetId, ct);
        return new { profile, sources = tags.Select(t => new { t.TagId, t.Code, t.Name, t.SensorTypeCode, t.Unit }) };
    }

    public async Task SaveAsync(Guid assetId, SavePowerProfileRequest r, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        if (!r.Enabled)
        {
            await repo.DisableAsync(tenantId, assetId, ct);
            await audit.WriteAsync("powersense.disable", "asset", assetId.ToString(), null, ct);
            return;
        }
        var tags = await repo.PowerTagsAsync(tenantId, assetId, ct);
        var match = tags.Where(t => t.TagId == r.SourceTagId).ToList();
        if (match.Count == 0)
        {
            throw AppException.Validation("Choose the asset's power (kW) or current (A) tag as the source.");
        }
        var source = match[0];
        if (r.MinCycleSec is < 1 or > 86_400)
        {
            throw AppException.Validation("The shortest cycle is 1 second to 24 hours.");
        }
        var (off, run) = (r.OffBelow, r.RunAbove);
        if (off is null || run is null)
        {
            var learned = PowerSenseMath.Learn([], (double?)await repo.RatedKwAsync(tenantId, assetId, ct));
            (off, run) = (off ?? learned.OffBelow, run ?? learned.RunAbove);
        }
        if (off < 0 || run <= off)
        {
            throw AppException.Validation("The running level must be above the off level.");
        }
        await repo.SaveAsync(tenantId, assetId, source.TagId, off!.Value, run!.Value, r.CountCycles, r.MinCycleSec, r.AutoLearn, ct);
        await audit.WriteAsync("powersense.save", "asset", assetId.ToString(), new { off, run }, ct);
    }

    /// <summary>Learns the bands from the last 24 hours of the source tag and saves them.</summary>
    public async Task<LearnResult> LearnAsync(Guid assetId, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        var p = await repo.ProfileAsync(tenantId, assetId, ct) ?? throw AppException.Validation("Turn power sense on first.");
        var readings = await repo.ReadingsAsync(p.SourceTagId, null, DateTime.UtcNow.AddHours(-24), 20_000, ct);
        var result = PowerSenseMath.Learn([.. readings.Select(x => x.Value)], (double?)await repo.RatedKwAsync(tenantId, assetId, ct));
        await repo.SaveLearnedAsync(assetId, result.OffBelow, result.RunAbove, result.Readings, ct);
        return result;
    }
}

/// <summary>
/// Worker job: for every power-sense machine, turns new power readings into virtual state and cycle readings, sets the
/// machine's status from them, and re-learns the bands once a day (when automatic learning is on).
/// </summary>
public sealed class PowerSenseJob(IPowerSenseRepository repo, IIngestionStore store)
{
    public async Task<int> RunOnceAsync(DateTime now, CancellationToken ct)
    {
        var written = 0;
        foreach (var p in await repo.AllAsync(ct))
        {
            var off = p.OffBelow;
            var run = p.RunAbove;
            if (p.AutoLearn && (p.LearnedAt is null || now - p.LearnedAt.Value > TimeSpan.FromHours(24)))
            {
                var history = await repo.ReadingsAsync(p.SourceTagId, null, now.AddHours(-24), 20_000, ct);
                if (history.Count >= 60)
                {
                    var learned = PowerSenseMath.Learn([.. history.Select(h => h.Value)], null);
                    (off, run) = (learned.OffBelow, learned.RunAbove);
                    await repo.SaveLearnedAsync(p.AssetId, off, run, learned.Readings, ct);
                }
            }
            var fresh = await repo.ReadingsAsync(p.SourceTagId, p.LastTs, p.LastTs is null ? now.AddMinutes(-10) : null, 5000, ct);
            if (fresh.Count == 0)
            {
                continue;
            }
            var (rows, lastAbove, lastCycleAt, count) = PowerSenseMath.Step(fresh, off, run, p.CountCycles && p.CountTagId is not null, p.MinCycleSec,
                p.LastAbove, p.LastCycleAt, p.CycleCount);
            var output = new List<ReadingRow>(rows.Count * 2);
            foreach (var (ts, state, c) in rows)
            {
                output.Add(new ReadingRow(p.TenantId, p.StateTagId, ts, state, Quality.Good, true));
                if (p.CountTagId is { } countTag && p.CountCycles)
                {
                    output.Add(new ReadingRow(p.TenantId, countTag, ts, c, Quality.Good, true));
                }
            }
            await store.WriteAsync(output, ct);
            var last = rows[^1];
            await store.UpdateHealthAsync([new AssetHealth(p.AssetId, MachineStates.ToStatus(last.State), last.Ts, null)], ct);
            await repo.SaveStateAsync(p.AssetId, fresh.Max(f => f.Ts), lastAbove, lastCycleAt, count, ct);
            written += output.Count;
        }
        return written;
    }
}
