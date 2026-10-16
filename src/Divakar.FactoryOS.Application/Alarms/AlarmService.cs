using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Settings;

namespace Divakar.FactoryOS.Application.Alarms;

public sealed record Rule(Guid RuleId, string Name, string SensorTypeCode, Guid? AssetId, string? AssetName, string? AssetTypeCode, string Operator,
    double Threshold, int DurationSec, string Severity, bool Notify, bool CreateWorkOrder, bool IsActive, int Matches, int OpenAlarms);

public sealed record SaveRuleRequest(string Name, string SensorTypeCode, Guid? AssetId, string? AssetTypeCode, string Operator, double Threshold,
    int DurationSec, string Severity, bool Notify = true, bool CreateWorkOrder = false, bool IsActive = true);

public sealed record Alarm(long AlarmId, string Kind, Guid? RuleId, string? RuleName, Guid AssetId, string AssetName, Guid? TagId, string Severity,
    string Message, double? Value, string State, DateTime RaisedAt, DateTime? AckAt, string? AckByName, string? AckComment, DateTime? ClearedAt);

public sealed record AlarmSummary(int Active, int Critical, int Warning, int Info, int Unacknowledged, long? LatestId, string? LatestMessage);

public sealed record AckRequest(string? Comment);

public interface IAlarmRepository
{
    Task<IReadOnlyList<Rule>> RulesAsync(Guid tenantId, CancellationToken ct);
    Task<Guid> SaveRuleAsync(Guid tenantId, Guid? id, SaveRuleRequest r, CancellationToken ct);
    Task DeleteRuleAsync(Guid tenantId, Guid id, CancellationToken ct);
    Task<IReadOnlyList<Alarm>> AlarmsAsync(Guid tenantId, string? state, DateTime? from, DateTime? to, Guid? assetId, int take, CancellationToken ct);
    Task<Alarm?> AlarmAsync(Guid tenantId, long id, CancellationToken ct);
    Task AcknowledgeAsync(Guid tenantId, long id, Guid userId, string? comment, CancellationToken ct);
    Task<AlarmSummary> SummaryAsync(Guid tenantId, CancellationToken ct);
    Task<bool> SensorTypeExistsAsync(string code, CancellationToken ct);
    Task<bool> AssetExistsAsync(Guid tenantId, Guid assetId, CancellationToken ct);
}

/// <summary>One rule applied to one tag, with the tag's newest value and the rule's progress on it.</summary>
public sealed record RuleTag(Guid TenantId, Guid RuleId, string RuleName, string Operator, double Threshold, int DurationSec, string Severity,
    bool Notify, bool CreateWorkOrder, Guid AssetId, string AssetName, Guid TagId, string TagName, string Unit, double? Value, DateTime? Ts,
    DateTime? PendingSince, long? OpenAlarmId);

/// <summary>Recent average of a tag against its own normal behaviour over the last day.</summary>
public sealed record AnomalyCandidate(Guid TenantId, Guid AssetId, string AssetName, Guid TagId, string TagName, string SensorTypeCode, string Unit,
    double BaselineMean, double BaselineStd, int BaselineCount, double RecentAvg, int RecentCount, long? OpenAlarmId);

public sealed record Escalation(Guid TenantId, long AlarmId, string Message, DateTime RaisedAt);

/// <summary>Alarm bookkeeping for every company (system connection), used by the worker.</summary>
public interface IAlarmStore
{
    Task<IReadOnlyList<RuleTag>> RuleTagsAsync(CancellationToken ct);
    Task SetPendingAsync(Guid ruleId, Guid tagId, DateTime since, CancellationToken ct);
    Task ClearPendingAsync(Guid ruleId, Guid tagId, CancellationToken ct);
    /// <summary>Raises an alarm unless one is already open for the rule and tag (or the anomaly tag); returns its id, or null.</summary>
    Task<long?> RaiseAsync(Guid tenantId, string kind, Guid? ruleId, Guid assetId, Guid? tagId, string severity, string message, double? value, CancellationToken ct);
    Task ClearAsync(long alarmId, CancellationToken ct);
    Task<IReadOnlyList<AnomalyCandidate>> AnomalyCandidatesAsync(IReadOnlyCollection<string> sensorTypes, DateTime now, CancellationToken ct);
    Task<IReadOnlyList<Escalation>> DueEscalationsAsync(DateTime olderThan, CancellationToken ct);
    Task MarkEscalatedAsync(long alarmId, CancellationToken ct);
}

public sealed class AlarmService(IAlarmRepository repo, ModuleGate gate, ICurrentUser me, IAuditLog audit)
{
    public static readonly string[] Operators = [">", ">=", "<", "<=", "=", "!="];
    public static readonly string[] Severities = ["Info", "Warning", "Critical"];

    public static bool Holds(double value, string op, double threshold) => op switch
    {
        ">" => value > threshold,
        ">=" => value >= threshold,
        "<" => value < threshold,
        "<=" => value <= threshold,
        "=" => Math.Abs(value - threshold) < 1e-9,
        "!=" => Math.Abs(value - threshold) >= 1e-9,
        _ => false,
    };

    public async Task<IReadOnlyList<Rule>> RulesAsync(CancellationToken ct)
    {
        await gate.EnsureAsync("alarms", ct);
        return await repo.RulesAsync(me.RequireTenant(), ct);
    }

    public async Task<Guid> SaveRuleAsync(Guid? id, SaveRuleRequest r, CancellationToken ct)
    {
        await gate.EnsureAsync("alarms", ct);
        var tenantId = me.RequireTenant();
        var name = Guard.Required(r.Name, "Name", 150);
        if (!Operators.Contains(r.Operator))
        {
            throw AppException.Validation("The operator must be one of > >= < <= = !=.");
        }
        if (!Severities.Contains(r.Severity))
        {
            throw AppException.Validation("Severity must be Info, Warning or Critical.");
        }
        if (r.DurationSec is < 0 or > 86_400)
        {
            throw AppException.Validation("The duration must be between 0 seconds and 24 hours.");
        }
        if (double.IsNaN(r.Threshold) || double.IsInfinity(r.Threshold))
        {
            throw AppException.Validation("The threshold must be a number.");
        }
        if (!await repo.SensorTypeExistsAsync(r.SensorTypeCode, ct))
        {
            throw AppException.Validation("Unknown sensor type.");
        }
        if (r.AssetId is { } a && !await repo.AssetExistsAsync(tenantId, a, ct))
        {
            throw AppException.NotFound("Asset");
        }
        if (id is { } existing && (await repo.RulesAsync(tenantId, ct)).All(x => x.RuleId != existing))
        {
            throw AppException.NotFound("Rule");
        }
        var saved = await repo.SaveRuleAsync(tenantId, id, r with { Name = name, AssetTypeCode = r.AssetId is null ? r.AssetTypeCode : null }, ct);
        await audit.WriteAsync(id is null ? "rule.create" : "rule.update", "rule", saved.ToString(), new { name }, ct);
        return saved;
    }

    public async Task DeleteRuleAsync(Guid id, CancellationToken ct)
    {
        await gate.EnsureAsync("alarms", ct);
        var tenantId = me.RequireTenant();
        if ((await repo.RulesAsync(tenantId, ct)).All(x => x.RuleId != id))
        {
            throw AppException.NotFound("Rule");
        }
        await repo.DeleteRuleAsync(tenantId, id, ct);
        await audit.WriteAsync("rule.delete", "rule", id.ToString(), null, ct);
    }

    public async Task<IReadOnlyList<Alarm>> AlarmsAsync(string? state, DateTime? from, DateTime? to, Guid? assetId, CancellationToken ct)
    {
        await gate.EnsureAsync("alarms", ct);
        if (state is not (null or "open" or "Active" or "Acknowledged" or "Cleared"))
        {
            throw AppException.Validation("state is open, Active, Acknowledged or Cleared.");
        }
        return await repo.AlarmsAsync(me.RequireTenant(), state, from?.ToUniversalTime(), to?.ToUniversalTime(), assetId, 500, ct);
    }

    public async Task<AlarmSummary> SummaryAsync(CancellationToken ct)
    {
        await gate.EnsureAsync("alarms", ct);
        return await repo.SummaryAsync(me.RequireTenant(), ct);
    }

    public async Task AcknowledgeAsync(long id, AckRequest r, CancellationToken ct)
    {
        await gate.EnsureAsync("alarms", ct);
        var tenantId = me.RequireTenant();
        var alarm = await repo.AlarmAsync(tenantId, id, ct) ?? throw AppException.NotFound("Alarm");
        if (alarm.State != "Active")
        {
            throw AppException.Validation($"The alarm is already {alarm.State.ToLowerInvariant()}.");
        }
        await repo.AcknowledgeAsync(tenantId, id, me.UserId, Guard.Optional(r.Comment, "Comment", 300), ct);
        await audit.WriteAsync("alarm.ack", "alarm", id.ToString(), null, ct);
    }
}

/// <summary>Creates maintenance work orders from alarms (implemented by the maintenance module).</summary>
public interface IAlarmWorkOrders
{
    Task CreateForAlarmAsync(Guid tenantId, Guid assetId, long alarmId, string type, string title, int priority, CancellationToken ct);
}

/// <summary>Queues e-mail and webhook messages for a company's alarm recipients (implemented by the notification module).</summary>
public interface IAlarmNotifier
{
    Task NotifyAsync(Guid tenantId, string subject, string body, object payload, CancellationToken ct);
}

/// <summary>
/// The rules engine, run by the worker every few seconds for every company: a rule must hold for its duration before
/// it raises an alarm; the alarm clears by itself when the value is back. Stale values (older than 5 minutes) are not judged.
/// Critical alarms nobody acknowledged within 15 minutes are escalated once.
/// </summary>
public sealed class AlarmEngine(IAlarmStore store, IAlarmWorkOrders workOrders, IAlarmNotifier notifier)
{
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan EscalateAfter = TimeSpan.FromMinutes(15);

    public async Task<int> RunOnceAsync(DateTime now, CancellationToken ct)
    {
        var changes = 0;
        foreach (var r in await store.RuleTagsAsync(ct))
        {
            if (r.Value is not { } value || r.Ts is not { } ts || now - ts > Stale)
            {
                continue;
            }
            if (AlarmService.Holds(value, r.Operator, r.Threshold))
            {
                if (r.OpenAlarmId is not null)
                {
                    continue;
                }
                var since = r.PendingSince ?? ts;
                if (r.PendingSince is null && r.DurationSec > 0)
                {
                    await store.SetPendingAsync(r.RuleId, r.TagId, ts, ct);
                }
                if ((now - since).TotalSeconds + 0.5 < r.DurationSec)
                {
                    continue;
                }
                var message = $"{r.RuleName} on {r.AssetName}: {r.TagName} {Fmt(value)} {r.Unit} ({r.Operator} {Fmt(r.Threshold)})".Trim();
                var id = await store.RaiseAsync(r.TenantId, "Rule", r.RuleId, r.AssetId, r.TagId, r.Severity, message, value, ct);
                await store.ClearPendingAsync(r.RuleId, r.TagId, ct);
                if (id is not { } alarmId)
                {
                    continue;
                }
                changes++;
                if (r.Notify)
                {
                    await notifier.NotifyAsync(r.TenantId, $"[{r.Severity}] {r.RuleName} — {r.AssetName}", message,
                        new { alarmId, r.Severity, rule = r.RuleName, asset = r.AssetName, tag = r.TagName, value, r.Unit, raisedAt = now }, ct);
                }
                if (r.CreateWorkOrder)
                {
                    await workOrders.CreateForAlarmAsync(r.TenantId, r.AssetId, alarmId, "Corrective", $"{r.RuleName} — {r.AssetName}",
                        r.Severity == "Critical" ? 1 : 2, ct);
                }
            }
            else
            {
                if (r.PendingSince is not null)
                {
                    await store.ClearPendingAsync(r.RuleId, r.TagId, ct);
                }
                if (r.OpenAlarmId is { } open)
                {
                    await store.ClearAsync(open, ct);
                    changes++;
                }
            }
        }

        foreach (var e in await store.DueEscalationsAsync(now - EscalateAfter, ct))
        {
            await notifier.NotifyAsync(e.TenantId, $"[Escalation] Critical alarm not acknowledged for 15 minutes",
                $"{e.Message}\nRaised {e.RaisedAt:yyyy-MM-dd HH:mm} UTC and still not acknowledged.", new { alarmId = e.AlarmId, escalation = true }, ct);
            await store.MarkEscalatedAsync(e.AlarmId, ct);
            changes++;
        }
        return changes;
    }

    private static string Fmt(double v) => Math.Abs(v) >= 100 ? v.ToString("0.#") : v.ToString("0.##");
}

/// <summary>
/// Predictive maintenance: compares each vibration, bearing and joint temperature with its own last 24 hours.
/// A recent 5-minute average more than three standard deviations above normal (and 30 % above the mean) raises an
/// anomaly and a predictive work order, so a technician looks before the machine breaks down.
/// </summary>
public sealed class AnomalyDetector(IAlarmStore store, IAlarmWorkOrders workOrders, IAlarmNotifier notifier)
{
    public static readonly string[] Sensors = ["vibration_rms", "vibration_peak", "bearing_temp", "robot_joint_temp", "motor_current"];

    public static bool IsAnomaly(AnomalyCandidate c) =>
        c.BaselineCount >= 20 && c.RecentCount >= 2 && c.RecentAvg > c.BaselineMean + 3 * Math.Max(c.BaselineStd, Math.Abs(c.BaselineMean) * 0.02)
        && c.RecentAvg > c.BaselineMean * 1.3;

    public static bool IsBackToNormal(AnomalyCandidate c) => c.RecentAvg < c.BaselineMean + 2 * Math.Max(c.BaselineStd, Math.Abs(c.BaselineMean) * 0.02);

    public async Task<int> RunOnceAsync(DateTime now, CancellationToken ct)
    {
        var changes = 0;
        foreach (var c in await store.AnomalyCandidatesAsync(Sensors, now, ct))
        {
            if (c.OpenAlarmId is null && IsAnomaly(c))
            {
                var what = c.SensorTypeCode.StartsWith("vibration") ? "possible bearing wear, imbalance or looseness"
                    : c.SensorTypeCode == "motor_current" ? "possible mechanical overload" : "possible lubrication or cooling problem";
                var message = $"{c.TagName} on {c.AssetName} is rising: {c.RecentAvg:0.##} {c.Unit} against a normal {c.BaselineMean:0.##} — {what}";
                if (await store.RaiseAsync(c.TenantId, "Anomaly", null, c.AssetId, c.TagId, "Warning", message, c.RecentAvg, ct) is { } id)
                {
                    await workOrders.CreateForAlarmAsync(c.TenantId, c.AssetId, id, "Predictive", $"Inspect {c.AssetName}: {c.TagName} above normal", 2, ct);
                    await notifier.NotifyAsync(c.TenantId, $"[Predictive] {c.AssetName}", message, new { alarmId = id, anomaly = true, asset = c.AssetName, c.RecentAvg, c.BaselineMean }, ct);
                    changes++;
                }
            }
            else if (c.OpenAlarmId is { } open && IsBackToNormal(c))
            {
                await store.ClearAsync(open, ct);
                changes++;
            }
        }
        return changes;
    }
}
