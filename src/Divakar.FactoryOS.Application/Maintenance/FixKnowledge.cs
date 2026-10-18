using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Settings;

namespace Divakar.FactoryOS.Application.Maintenance;

/// <summary>What is known about a problem: the machine, its type, the alarm rule and sensor that fired, and the words used to describe it.</summary>
public sealed record FixCase(Guid AssetId, string AssetTypeCode, Guid? RuleId, string? SensorTypeCode, string Text);

/// <summary>A repaired breakdown with its recorded cause and fix.</summary>
public sealed record PastFix(Guid MaintWoId, string Code, string Title, Guid AssetId, string AssetName, string AssetTypeCode, Guid? RuleId,
    string? SensorTypeCode, string? FailureCode, string Cause, string Fix, DateTime RecordedAt, string? RecordedBy, int Helpful);

public sealed record FixSuggestion(PastFix Fix, double Score, IReadOnlyList<string> Reasons);

public sealed record Resolution(string? FailureCode, string Cause, string Fix, DateTime RecordedAt, string? RecordedBy);

public sealed record SaveResolutionRequest(string? FailureCode, string Cause, string Fix);

public sealed record KnowledgeSearch(IReadOnlyList<PastFix> Fixes, IReadOnlyList<string> FailureCodes);

public interface IFixKnowledgeRepository
{
    Task<FixCase?> CaseForWorkOrderAsync(Guid tenantId, Guid maintWoId, CancellationToken ct);
    Task<FixCase?> CaseForAlarmAsync(Guid tenantId, long alarmId, CancellationToken ct);
    Task<IReadOnlyList<PastFix>> PastFixesAsync(Guid tenantId, Guid? excludeWoId, string? search, int max, CancellationToken ct);
    Task<Resolution?> ResolutionAsync(Guid tenantId, Guid maintWoId, CancellationToken ct);
    Task SaveResolutionAsync(Guid tenantId, Guid maintWoId, string? failureCode, string cause, string fix, Guid? userId, CancellationToken ct);
    Task MarkHelpfulAsync(Guid tenantId, Guid maintWoId, CancellationToken ct);
    Task<IReadOnlyList<string>> FailureCodesAsync(Guid tenantId, CancellationToken ct);
}

public static class FixMatcher
{
    private static readonly HashSet<string> Stop = ["the", "a", "an", "and", "or", "of", "on", "in", "to", "for", "is", "at", "by", "with", "from", "alarm",
        "opened", "automatically", "pm", "check", "high", "low", "too"];

    public static HashSet<string> Words(string text) =>
        [.. System.Text.RegularExpressions.Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 2 && !Stop.Contains(w))];

    /// <summary>
    /// Scores a past fix against a new problem. The same alarm rule is the strongest sign, then the same sensor, the same machine,
    /// the same machine type and shared words; fixes that technicians reused before rank a little higher.
    /// </summary>
    public static FixSuggestion Score(FixCase problem, PastFix past, HashSet<string>? problemWords = null)
    {
        var reasons = new List<string>();
        double score = 0;
        if (problem.RuleId is { } r && r == past.RuleId)
        {
            score += 5;
            reasons.Add("same alarm rule");
        }
        if (problem.SensorTypeCode is { } s && s == past.SensorTypeCode)
        {
            score += 3;
            reasons.Add("same sensor");
        }
        if (problem.AssetId == past.AssetId)
        {
            score += 3;
            reasons.Add("same machine");
        }
        else if (problem.AssetTypeCode == past.AssetTypeCode)
        {
            score += 1;
            reasons.Add("same machine type");
        }
        var a = problemWords ?? Words(problem.Text);
        var b = Words($"{past.Title} {past.FailureCode} {past.Cause}");
        var shared = a.Intersect(b).ToList();
        if (shared.Count > 0)
        {
            score += 4.0 * shared.Count / Math.Max(1, Math.Min(a.Count, b.Count));
            reasons.Add($"mentions {string.Join(", ", shared.Take(3))}");
        }
        score += Math.Min(1, past.Helpful * 0.2);
        return new FixSuggestion(past, Math.Round(score, 2), reasons);
    }

    public static IReadOnlyList<FixSuggestion> Rank(FixCase problem, IEnumerable<PastFix> fixes, int take = 5)
    {
        var words = Words(problem.Text);
        return [.. fixes.Select(f => Score(problem, f, words)).Where(s => s.Score >= 2)
            .OrderByDescending(s => s.Score).ThenByDescending(s => s.Fix.RecordedAt).Take(take)];
    }
}

public sealed class FixKnowledgeService(IFixKnowledgeRepository repo, IMaintenanceRepository maintenance, ModuleGate gate, ICurrentUser me, IAuditLog audit)
{
    public async Task<IReadOnlyList<FixSuggestion>> ForWorkOrderAsync(Guid maintWoId, CancellationToken ct)
    {
        await gate.EnsureAsync("maintenance", ct);
        var tenantId = me.RequireTenant();
        var problem = await repo.CaseForWorkOrderAsync(tenantId, maintWoId, ct) ?? throw AppException.NotFound("Work order");
        return FixMatcher.Rank(problem, await repo.PastFixesAsync(tenantId, maintWoId, null, 2000, ct));
    }

    public async Task<IReadOnlyList<FixSuggestion>> ForAlarmAsync(long alarmId, CancellationToken ct)
    {
        await gate.EnsureAsync("maintenance", ct);
        var tenantId = me.RequireTenant();
        var problem = await repo.CaseForAlarmAsync(tenantId, alarmId, ct) ?? throw AppException.NotFound("Alarm");
        return FixMatcher.Rank(problem, await repo.PastFixesAsync(tenantId, null, null, 2000, ct));
    }

    public async Task<KnowledgeSearch> SearchAsync(string? q, CancellationToken ct)
    {
        await gate.EnsureAsync("maintenance", ct);
        var tenantId = me.RequireTenant();
        var search = string.IsNullOrWhiteSpace(q) ? null : q.Trim()[..Math.Min(100, q.Trim().Length)];
        return new KnowledgeSearch(await repo.PastFixesAsync(tenantId, null, search, 200, ct), await repo.FailureCodesAsync(tenantId, ct));
    }

    public async Task<Resolution?> GetAsync(Guid maintWoId, CancellationToken ct)
    {
        await gate.EnsureAsync("maintenance", ct);
        return await repo.ResolutionAsync(me.RequireTenant(), maintWoId, ct);
    }

    /// <summary>Records the cause and the fix. Allowed while the order is open and once after it is done (to fill in a forgotten fix).</summary>
    public async Task SaveAsync(Guid maintWoId, SaveResolutionRequest r, CancellationToken ct)
    {
        await gate.EnsureAsync("maintenance", ct);
        var tenantId = me.RequireTenant();
        var wo = await maintenance.WorkOrderAsync(tenantId, maintWoId, ct) ?? throw AppException.NotFound("Work order");
        if (wo.Status == "Cancelled")
        {
            throw AppException.Validation("A cancelled work order has no fix to record.");
        }
        await repo.SaveResolutionAsync(tenantId, maintWoId, Guard.Optional(r.FailureCode, "Failure code", 40)?.ToUpperInvariant(),
            Guard.Required(r.Cause, "Cause", 500), Guard.Required(r.Fix, "Fix", 1000), me.UserId, ct);
        await audit.WriteAsync("maintwo.resolution", "maintwo", maintWoId.ToString(), null, ct);
    }

    /// <summary>A technician used a suggested fix: it ranks higher next time.</summary>
    public async Task HelpfulAsync(Guid pastWoId, CancellationToken ct)
    {
        await gate.EnsureAsync("maintenance", ct);
        await repo.MarkHelpfulAsync(me.RequireTenant(), pastWoId, ct);
    }

    public static bool NeedsResolution(string type) => type is "Corrective" or "Predictive";
}
