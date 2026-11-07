using System.Text.Json;
using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Settings;

namespace Divakar.FactoryOS.Application.Ai;

public sealed record Insight(long InsightId, Guid AssetId, string AssetName, Guid? TagId, string? TagName, string Kind, string Severity, string Title,
    string? Detail, double Score, string? Evidence, string Source, DateTime DetectedAt, DateTime LastSeenAt, DateTime? ClosedAt, string? Feedback,
    string? FeedbackNote);

public sealed record NewInsight(Guid TenantId, Guid AssetId, Guid? TagId, string Kind, string Severity, string Title, string? Detail, double Score,
    string? Evidence, string Source);

public sealed record OpenInsight(long InsightId, Guid TenantId, Guid AssetId, Guid? TagId, string Kind, string Source, DateTime LastSeenAt);

public sealed record Tuning(Guid TenantId, Guid TagKey, string Kind, double Sensitivity, int RealCount, int FalseCount, string? Name);

public sealed record EnergyHour(Guid TenantId, Guid AssetId, string AssetName, Guid TagId, DateTime Hour, double Rise);

public sealed record AnalogHour(Guid TenantId, Guid AssetId, string AssetName, Guid TagId, string TagName, string SensorTypeCode, string Unit,
    double? MaxValue, double? RuleLimit, DateTime Hour, double Avg);

public sealed record RecentValue(Guid TagId, DateTime Ts, double Value);

public sealed record IdleCandidate(Guid TenantId, Guid AssetId, string AssetName, int State, DateTime IdleSince, double PowerKw, decimal? RatedPowerKw);

public sealed record FeedbackRequest(string Verdict, string? Note);

public sealed record AiSummary(int Open, int Critical, int Warning, int Confirmed, int FalseAlarms, IReadOnlyList<Insight> Insights, IReadOnlyList<Tuning> Tuning);

/// <summary>Worker side: system connection, every company.</summary>
public interface IOtAiStore
{
    Task<IReadOnlyList<EnergyHour>> EnergyHoursAsync(DateTime since, CancellationToken ct);
    Task<IReadOnlyList<AnalogHour>> AnalogHoursAsync(IReadOnlyCollection<string> sensorTypes, DateTime since, DateTime until, CancellationToken ct);
    Task<IReadOnlyList<RecentValue>> RecentValuesAsync(IReadOnlyCollection<string> sensorTypes, DateTime since, CancellationToken ct);
    Task<IReadOnlyList<IdleCandidate>> IdleCandidatesAsync(DateTime now, CancellationToken ct);
    Task<IReadOnlyList<Tuning>> AllTuningAsync(CancellationToken ct);
    Task<IReadOnlyList<OpenInsight>> OpenInsightsAsync(CancellationToken ct);
    /// <summary>Adds the insight, or refreshes the open one for the same asset, tag and kind; true when it is new.</summary>
    Task<bool> UpsertAsync(NewInsight i, DateTime now, CancellationToken ct);
    Task CloseAsync(long insightId, DateTime now, CancellationToken ct);
}

/// <summary>API side: the signed-in company.</summary>
public interface IOtAiRepository
{
    Task<IReadOnlyList<Insight>> InsightsAsync(Guid tenantId, bool openOnly, int max, CancellationToken ct);
    Task<Insight?> InsightAsync(Guid tenantId, long id, CancellationToken ct);
    Task FeedbackAsync(Guid tenantId, long id, string verdict, Guid userId, string? note, double sensitivity, CancellationToken ct);
    Task CloseAsync(Guid tenantId, long id, CancellationToken ct);
    Task<IReadOnlyList<Tuning>> TuningAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>
/// Runs the OT detectors over every company's data (every 5 minutes from the worker): energy use against each meter's own
/// hour-of-week profile, meters that stop counting, drift and frozen values on process and condition signals, the time until a
/// rising signal reaches its limit, and machines idling at high power. Findings go to ai.Insights; a finding that no longer
/// holds is closed.
/// </summary>
public sealed class OtAiJob(IOtAiStore store)
{
    public static readonly string[] AnalogSensors =
    [
        "temperature", "oil_temperature", "pressure", "vibration_rms", "vibration_peak", "bearing_temp", "motor_current", "robot_joint_temp",
        "spindle_load", "flow_air", "flow_water", "current", "thd_current", "thd_voltage", "torque", "level", "humidity",
    ];

    public const double EnergyZ = 4, EnergyCriticalZ = 8, CusumH = 5;

    private static string Json(object o) => JsonSerializer.Serialize(o, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public async Task<int> RunOnceAsync(DateTime now, CancellationToken ct)
    {
        var tuning = (await store.AllTuningAsync(ct)).ToDictionary(t => (t.TagKey, t.Kind), t => t.Sensitivity);
        double Sens(Guid key, string kind) => tuning.GetValueOrDefault((key, kind), 1);
        var found = new List<NewInsight>();

        found.AddRange(EnergyFindings(await store.EnergyHoursAsync(now.AddDays(-28), ct), now, Sens));
        var hours = await store.AnalogHoursAsync(AnalogSensors, now.AddDays(-7), now.AddHours(-2), ct);
        var recent = (await store.RecentValuesAsync(AnalogSensors, now.AddHours(-1), ct)).ToLookup(r => r.TagId);
        found.AddRange(AnalogFindings(hours, recent, now, Sens));
        found.AddRange(IdleFindings(await store.IdleCandidatesAsync(now, ct), now, Sens));

        var changes = 0;
        foreach (var f in found)
        {
            if (await store.UpsertAsync(f, now, ct))
            {
                changes++;
            }
        }
        // close what no longer holds: cloud findings at once, an edge agent's after two hours without it repeating them
        var still = found.Select(f => (f.AssetId, f.TagId, f.Kind)).ToHashSet();
        foreach (var o in await store.OpenInsightsAsync(ct))
        {
            var gone = o.Source == "cloud" ? !still.Contains((o.AssetId, o.TagId, o.Kind)) : o.LastSeenAt < now.AddHours(-2);
            if (gone)
            {
                await store.CloseAsync(o.InsightId, now, ct);
                changes++;
            }
        }
        return changes;
    }

    public static IEnumerable<NewInsight> EnergyFindings(IReadOnlyList<EnergyHour> rows, DateTime now, Func<Guid, string, double> sens)
    {
        var current = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        var h1 = current.AddHours(-1);
        var h2 = current.AddHours(-2);
        var h3 = current.AddHours(-3);
        foreach (var g in rows.GroupBy(r => r.TagId))
        {
            var first = g.First();
            var byHour = g.GroupBy(r => r.Hour).ToDictionary(x => x.Key, x => x.Sum(r => r.Rise));
            var history = byHour.Where(kv => kv.Key < h3).Select(kv => (kv.Key, kv.Value)).ToList();
            if (history.Count < 48)
            {
                continue;   // still learning: two days of hourly history at least
            }
            double V(DateTime h) => byHour.GetValueOrDefault(h);
            var n1 = OtModels.Normal(history, h1, 0.2);
            var n2 = OtModels.Normal(history, h2, 0.2);
            var n3 = OtModels.Normal(history, h3, 0.2);
            var z1 = OtModels.Score(V(h1), n1.Median, n1.Sigma);
            var z2 = OtModels.Score(V(h2), n2.Median, n2.Sigma);
            var threshold = OtModels.Tuned(EnergyZ, sens(first.TagId, "EnergyAnomaly"));
            if (z1 > threshold && z2 > threshold && V(h1) > n1.Median * 1.2 + 0.5)
            {
                var extra = V(h1) + V(h2) - n1.Median - n2.Median;
                yield return new NewInsight(first.TenantId, first.AssetId, first.TagId, "EnergyAnomaly", Math.Min(z1, z2) > OtModels.Tuned(EnergyCriticalZ, sens(first.TagId, "EnergyAnomaly")) ? "Critical" : "Warning",
                    $"{first.AssetName}: energy use well above its normal",
                    $"{V(h2):N1} and {V(h1):N1} kWh in the last two complete hours against a normal {n2.Median:N1} and {n1.Median:N1} kWh ({n1.Basis}, {n1.Samples} samples); "
                    + $"{extra:N1} kWh more than usual. Look for a machine left running, a leak (compressed air), a stuck valve or a changed setting.",
                    Math.Round(Math.Min(z1, z2), 2), Json(new { hours = new[] { h2, h1 }, kwh = new[] { V(h2), V(h1) }, normal = new[] { n2.Median, n1.Median }, sigma = n1.Sigma, threshold }),
                    "cloud");
            }
            if (V(h1) == 0 && V(h2) == 0 && V(h3) == 0 && n1.Median > 1 && n2.Median > 1 && n3.Median > 1)
            {
                yield return new NewInsight(first.TenantId, first.AssetId, first.TagId, "MeterFlat", "Warning", $"{first.AssetName}: meter has stopped counting",
                    $"No energy counted for three hours while this meter normally reads about {(n1.Median + n2.Median + n3.Median) / 3:N1} kWh an hour now. "
                    + "Check the meter, its CTs and the communication link, or whether the machine was switched off unexpectedly.",
                    3, Json(new { hours = new[] { h3, h2, h1 }, normal = new[] { n3.Median, n2.Median, n1.Median } }), "cloud");
            }
        }
    }

    public static IEnumerable<NewInsight> AnalogFindings(IReadOnlyList<AnalogHour> hours, ILookup<Guid, RecentValue> recent, DateTime now,
        Func<Guid, string, double> sens)
    {
        foreach (var g in hours.GroupBy(h => h.TagId))
        {
            var first = g.First();
            var hourly = g.OrderBy(h => h.Hour).ToList();
            var values = recent[g.Key].OrderBy(r => r.Ts).ToList();
            if (hourly.Count < 24 || values.Count < 6)
            {
                continue;
            }
            var avgs = hourly.Select(h => h.Avg).ToList();
            var mean = OtModels.Median(avgs);
            var sigma = Math.Max(OtModels.RobustSigma(avgs), Math.Max(1e-3, Math.Abs(mean) * 0.01));
            var what = $"{first.TagName} on {first.AssetName}";

            if (OtModels.IsFrozen([.. values.TakeLast(12).Select(v => v.Value)], OtModels.RobustSigma(avgs)))
            {
                yield return new NewInsight(first.TenantId, first.AssetId, first.TagId, "Frozen", "Warning", $"{what} is frozen",
                    $"The last {Math.Min(12, values.Count)} readings are all {values[^1].Value:0.###} {first.Unit} while this signal normally varies (σ {OtModels.RobustSigma(avgs):0.###}). "
                    + "A stuck sensor or a controller holding its last value — check the transmitter and its wiring before trusting this signal.",
                    12, Json(new { last = values[^1].Value, sigma = OtModels.RobustSigma(avgs) }), "cloud");
                continue;
            }

            // drift: EWMA and CUSUM of 5-minute means against the last week's normal
            var buckets = values.GroupBy(v => new DateTime(v.Ts.Year, v.Ts.Month, v.Ts.Day, v.Ts.Hour, v.Ts.Minute / 5 * 5, 0, DateTimeKind.Utc))
                .OrderBy(b => b.Key).Select(b => b.Average(v => v.Value)).ToList();
            var s = sens(first.TagId, "Drift");
            var ewma = OtModels.Ewma(buckets, mean);
            var limit = OtModels.Tuned(OtModels.EwmaLimit(sigma), s);
            var (hi, lo) = OtModels.Cusum(buckets, mean, sigma);
            if (Math.Abs(ewma - mean) > limit && Math.Max(hi, lo) > OtModels.Tuned(CusumH, s))
            {
                var up = ewma > mean;
                yield return new NewInsight(first.TenantId, first.AssetId, first.TagId, "Drift", Math.Abs(ewma - mean) > 2 * limit ? "Critical" : "Warning",
                    $"{what} is drifting {(up ? "up" : "down")}",
                    $"Running average {ewma:0.##} {first.Unit} against a normal {mean:0.##} {first.Unit} for the last week (EWMA limit ±{limit:0.##}, CUSUM {Math.Max(hi, lo):0.0}σ). "
                    + (first.SensorTypeCode.StartsWith("vibration", StringComparison.Ordinal) ? "Rising vibration points to bearing wear, imbalance or looseness."
                        : first.SensorTypeCode.Contains("temp") ? "Rising temperature points to lubrication, cooling or overload problems." : "Check the process and the sensor."),
                    Math.Round(Math.Max(hi, lo), 2), Json(new { ewma, mean, sigma, limit, cusum = Math.Max(hi, lo) }), "cloud");
            }

            // time to limit: trend of the last 72 h of hourly averages plus the recent 5-minute means
            var cap = first.RuleLimit ?? first.MaxValue;
            if (cap is { } top && top > mean)
            {
                var since = now.AddHours(-72);
                var pts = hourly.Where(h => h.Hour >= since).Select(h => ((h.Hour - now).TotalHours + 0.5, h.Avg))
                    .Concat(values.Select(v => ((v.Ts - now).TotalHours, v.Value))).ToList();
                if (OtModels.HoursToLimit(pts, top, 0) is { } hoursLeft && hoursLeft <= 168 / sens(first.TagId, "TimeToLimit"))
                {
                    var (slope, _, t, r2) = OtModels.Trend(pts);
                    yield return new NewInsight(first.TenantId, first.AssetId, first.TagId, "TimeToLimit", hoursLeft < 24 ? "Critical" : "Warning",
                        $"{what} reaches its limit in about {(hoursLeft < 48 ? $"{hoursLeft:N0} h" : $"{hoursLeft / 24:N1} days")}",
                        $"Rising {slope:0.###} {first.Unit} per hour over the last 72 h (t = {t:0.0}, R² {r2:0.00}); at this rate it reaches "
                        + $"{(first.RuleLimit is not null ? "the alarm limit" : "the sensor's maximum")} {top:0.##} {first.Unit} around {now.AddHours(hoursLeft):yyyy-MM-dd HH:mm} UTC. "
                        + "Plan the intervention before then.",
                        Math.Round(hoursLeft, 1), Json(new { slope, t, r2, limit = top, hoursLeft }), "cloud");
                }
            }
        }
    }

    public static IEnumerable<NewInsight> IdleFindings(IReadOnlyList<IdleCandidate> rows, DateTime now, Func<Guid, string, double> sens)
    {
        foreach (var r in rows)
        {
            var minutes = (now - r.IdleSince).TotalMinutes;
            var floor = Math.Max(0.5, (double)(r.RatedPowerKw ?? 5) * 0.2);
            if (minutes < 20 * sens(r.AssetId, "IdleWaste") || r.PowerKw < OtModels.Tuned(floor, sens(r.AssetId, "IdleWaste")))
            {
                continue;
            }
            var wasted = r.PowerKw * minutes / 60;
            yield return new NewInsight(r.TenantId, r.AssetId, null, "IdleWaste", wasted > 20 ? "Warning" : "Info",
                $"{r.AssetName} idles at {r.PowerKw:N1} kW",
                $"{(r.State == 3 ? "Stopped" : "Idle")} for {minutes:N0} min while drawing {r.PowerKw:N1} kW — about {wasted:N1} kWh so far. "
                + "Put it in standby or switch off auxiliaries (hydraulics, heaters, coolant pumps) when it waits this long.",
                Math.Round(wasted, 1), Json(new { minutes, r.PowerKw, r.RatedPowerKw, wasted }), "cloud");
        }
    }
}

/// <summary>The AI insights page: what the detectors found, operator feedback that tunes them, and the tuning in force.</summary>
public sealed class OtAiService(IOtAiRepository repo, OtAiJob job, ModuleGate gate, ICurrentUser me, IAuditLog audit)
{
    private async Task<Guid> Begin(CancellationToken ct)
    {
        await gate.EnsureAsync("alarms", ct);
        return me.RequireTenant();
    }

    public async Task<AiSummary> SummaryAsync(bool openOnly, CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        var list = await repo.InsightsAsync(tenantId, openOnly, 300, ct);
        var all = openOnly ? await repo.InsightsAsync(tenantId, false, 1000, ct) : list;
        var open = all.Where(i => i.ClosedAt is null).ToList();
        return new AiSummary(open.Count, open.Count(i => i.Severity == "Critical"), open.Count(i => i.Severity == "Warning"),
            all.Count(i => i.Feedback == "Real"), all.Count(i => i.Feedback == "False"), list, await repo.TuningAsync(tenantId, ct));
    }

    public async Task FeedbackAsync(long id, FeedbackRequest r, CancellationToken ct)
    {
        var tenantId = await Begin(ct);
        if (r.Verdict is not ("Real" or "False"))
        {
            throw AppException.Validation("Verdict is Real (a true problem) or False (a false alarm).");
        }
        var i = await repo.InsightAsync(tenantId, id, ct) ?? throw AppException.NotFound("Insight");
        if (i.Feedback is not null)
        {
            throw AppException.Conflict("This insight already has feedback.");
        }
        var key = i.TagId ?? i.AssetId;
        var current = (await repo.TuningAsync(tenantId, ct)).FirstOrDefault(t => t.TagKey == key && t.Kind == i.Kind)?.Sensitivity ?? 1;
        var next = OtModels.AfterFeedback(current, r.Verdict == "Real");
        await repo.FeedbackAsync(tenantId, id, r.Verdict, me.UserId, Guard.Optional(r.Note, "Note", 300), next, ct);
        await audit.WriteAsync("ai.feedback", "Insight", id.ToString(), new { r.Verdict, i.Kind, sensitivity = next }, ct);
    }

    public async Task CloseAsync(long id, CancellationToken ct)
    {
        await repo.CloseAsync(await Begin(ct), id, ct);
        await audit.WriteAsync("ai.close", "Insight", id.ToString(), null, ct);
    }

    /// <summary>Runs the detectors now (normally the worker does every 5 minutes).</summary>
    public async Task<int> RunNowAsync(CancellationToken ct)
    {
        await Begin(ct);
        return await job.RunOnceAsync(DateTime.UtcNow, ct);
    }
}
