using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Ai;
using Divakar.FactoryOS.Application.Common;

namespace Divakar.FactoryOS.Application.Connectivity;

public sealed record EdgeAgentInfo(Guid AgentId, string Name, string KeyPrefix, bool IsActive, string? Version, string? Hostname, long? Buffered,
    long? Dropped, string? LastError, DateTime? LastSeenAt, DateTime CreatedAt, string AssetIds)
{
    public bool Online => LastSeenAt is { } seen && seen > DateTime.UtcNow.AddMinutes(-3);
}

public sealed record NewEdgeAgent(Guid AgentId, string Name, string Key);
public sealed record SaveEdgeAgentRequest(string Name);
public sealed record EdgeAssetsRequest(IReadOnlyList<Guid> AssetIds);

/// <summary>What an agent downloads: the assets it reads, with their connection settings (decrypted for it) and tags.</summary>
public sealed record EdgeConfig(Guid AgentId, string Name, DateTime ServerTime, IReadOnlyList<AssetBinding> Assets);

/// <summary>A reading as the agent's ingestion engine produced it: already scaled, deadbanded and marked for history (Store).</summary>
public sealed record EdgeReading(Guid TagId, DateTime Ts, double? Value, byte Quality, bool Store);

public sealed record EdgeUpload(IReadOnlyList<EdgeReading>? Readings, IReadOnlyList<AssetHealth>? Health);
public sealed record EdgeUploadResult(int Accepted, int Rejected);
public sealed record EdgeHeartbeat(string? Version, string? Hostname, long Buffered, long Dropped, string? LastError);
public sealed record EdgeInsight(Guid AssetId, Guid? TagId, string Kind, string Severity, string Title, string? Detail, double Score);

public sealed record EdgeAgentKey(Guid AgentId, Guid TenantId, string Name, bool IsActive);

public interface IEdgeRepository
{
    Task<IReadOnlyList<EdgeAgentInfo>> AgentsAsync(Guid tenantId, CancellationToken ct);
    Task<Guid> CreateAsync(Guid tenantId, string name, string keyHash, string prefix, CancellationToken ct);
    Task RotateKeyAsync(Guid tenantId, Guid agentId, string keyHash, string prefix, CancellationToken ct);
    Task SetActiveAsync(Guid tenantId, Guid agentId, bool active, CancellationToken ct);
    Task DeleteAsync(Guid tenantId, Guid agentId, CancellationToken ct);
    /// <summary>Replaces the agent's assets; fails with the names of assets another agent already reads.</summary>
    Task SetAssetsAsync(Guid tenantId, Guid agentId, IReadOnlyList<Guid> assetIds, CancellationToken ct);

    // agent-facing, system connection
    Task<EdgeAgentKey?> AgentForKeyAsync(string keyHash, CancellationToken ct);
    Task<IReadOnlyList<AssetBinding>> BindingsAsync(Guid agentId, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, Guid>> AgentTagsAsync(Guid agentId, CancellationToken ct);
    Task HeartbeatAsync(Guid agentId, EdgeHeartbeat h, CancellationToken ct);
}

/// <summary>
/// Edge agents: a small program next to the machines (a plant PC or an industrial gateway) that polls them with the same
/// drivers as the cloud worker, keeps readings on disk while the network is down, forwards them when it is back, and runs the
/// OT detectors locally. The cloud worker leaves an agent's assets alone.
/// </summary>
public sealed class EdgeService(IEdgeRepository repo, IIngestionStore ingest, IOtAiStore insights, ITokenService tokens, ICurrentUser me, IAuditLog audit,
    IClock clock)
{
    public const int MaxReadings = 5000;
    public static readonly string[] InsightKinds = ["EnergyAnomaly", "MeterFlat", "Drift", "Frozen", "TimeToLimit", "IdleWaste", "Spike"];

    public async Task<IReadOnlyList<EdgeAgentInfo>> AgentsAsync(CancellationToken ct) => await repo.AgentsAsync(me.RequireTenant(), ct);

    private (string Key, string Hash, string Prefix) NewKey()
    {
        var (token, _) = tokens.CreateRefreshToken();
        var key = "fos_edge_" + token;
        return (key, tokens.HashRefreshToken(key), key[..12]);
    }

    public async Task<NewEdgeAgent> CreateAsync(SaveEdgeAgentRequest r, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        var name = Guard.Required(r.Name, "Name", 100);
        var (key, hash, prefix) = NewKey();
        try
        {
            var id = await repo.CreateAsync(tenantId, name, hash, prefix, ct);
            await audit.WriteAsync("edge.create", "EdgeAgent", id.ToString(), new { name, prefix }, ct);
            return new NewEdgeAgent(id, name, key);
        }
        catch (Exception e) when (e.Message.Contains("UQ_EdgeAgents_Name"))
        {
            throw AppException.Conflict($"An edge agent called '{name}' already exists.");
        }
    }

    public async Task<NewEdgeAgent> RotateKeyAsync(Guid agentId, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        var agent = (await repo.AgentsAsync(tenantId, ct)).FirstOrDefault(a => a.AgentId == agentId) ?? throw AppException.NotFound("Edge agent");
        var (key, hash, prefix) = NewKey();
        await repo.RotateKeyAsync(tenantId, agentId, hash, prefix, ct);
        await audit.WriteAsync("edge.rotate", "EdgeAgent", agentId.ToString(), new { prefix }, ct);
        return new NewEdgeAgent(agentId, agent.Name, key);
    }

    public async Task SetActiveAsync(Guid agentId, bool active, CancellationToken ct)
    {
        await repo.SetActiveAsync(me.RequireTenant(), agentId, active, ct);
        await audit.WriteAsync(active ? "edge.enable" : "edge.disable", "EdgeAgent", agentId.ToString(), null, ct);
    }

    public async Task DeleteAsync(Guid agentId, CancellationToken ct)
    {
        await repo.DeleteAsync(me.RequireTenant(), agentId, ct);
        await audit.WriteAsync("edge.delete", "EdgeAgent", agentId.ToString(), null, ct);
    }

    public async Task SetAssetsAsync(Guid agentId, EdgeAssetsRequest r, CancellationToken ct)
    {
        var ids = (r.AssetIds ?? []).Distinct().ToList();
        if (ids.Count > 500)
        {
            throw AppException.Validation("An agent reads at most 500 assets.");
        }
        await repo.SetAssetsAsync(me.RequireTenant(), agentId, ids, ct);
        await audit.WriteAsync("edge.assets", "EdgeAgent", agentId.ToString(), new { count = ids.Count }, ct);
    }

    // ---------- called by the agent with its key ----------

    private async Task<EdgeAgentKey> AgentAsync(string? key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw AppException.Unauthorized("Missing edge key.");
        }
        var agent = await repo.AgentForKeyAsync(tokens.HashRefreshToken(key.Trim()), ct) ?? throw AppException.Unauthorized("Unknown edge key.");
        return agent.IsActive ? agent : throw AppException.Forbidden("This edge agent is switched off.");
    }

    public async Task<EdgeConfig> ConfigAsync(string? key, CancellationToken ct)
    {
        var agent = await AgentAsync(key, ct);
        return new EdgeConfig(agent.AgentId, agent.Name, clock.UtcNow, await repo.BindingsAsync(agent.AgentId, ct));
    }

    public async Task<EdgeUploadResult> UploadAsync(string? key, EdgeUpload upload, CancellationToken ct)
    {
        var agent = await AgentAsync(key, ct);
        var readings = upload.Readings ?? [];
        if (readings.Count > MaxReadings)
        {
            throw AppException.Validation($"Send at most {MaxReadings} readings per request.");
        }
        var tags = await repo.AgentTagsAsync(agent.AgentId, ct);
        var now = clock.UtcNow;
        var rows = new List<ReadingRow>();
        var rejected = 0;
        foreach (var r in readings)
        {
            var ts = DateTime.SpecifyKind(r.Ts.ToUniversalTime(), DateTimeKind.Utc);
            // a buffered reading can be up to 30 days old (an agent that was offline for weeks), never in the future
            if (!tags.ContainsKey(r.TagId) || ts > now.AddMinutes(5) || ts < now.AddDays(-30) || r.Quality > 3)
            {
                rejected++;
                continue;
            }
            rows.Add(new ReadingRow(agent.TenantId, r.TagId, ts, r.Value is { } v && double.IsFinite(v) ? v : null, r.Quality, r.Store));
        }
        if (rows.Count > 0)
        {
            await ingest.WriteAsync(rows, ct);
        }
        var assets = tags.Values.ToHashSet();
        var health = (upload.Health ?? []).Where(h => assets.Contains(h.AssetId) && h.Status is "Running" or "Idle" or "Stopped" or "Fault" or "Maintenance" or "Offline" or "Unchanged")
            .Select(h => h with { LastError = h.LastError is { Length: > 500 } e ? e[..500] : h.LastError }).ToList();
        await ingest.UpdateHealthAsync(health, ct);
        return new EdgeUploadResult(rows.Count, rejected);
    }

    public async Task HeartbeatAsync(string? key, EdgeHeartbeat h, CancellationToken ct)
    {
        var agent = await AgentAsync(key, ct);
        await repo.HeartbeatAsync(agent.AgentId, h with
        {
            Version = h.Version is { Length: > 40 } v ? v[..40] : h.Version, Hostname = h.Hostname is { Length: > 100 } n ? n[..100] : h.Hostname,
            LastError = h.LastError is { Length: > 500 } e ? e[..500] : h.LastError,
        }, ct);
    }

    public async Task<int> InsightsAsync(string? key, IReadOnlyList<EdgeInsight> list, CancellationToken ct)
    {
        var agent = await AgentAsync(key, ct);
        if (list.Count > 200)
        {
            throw AppException.Validation("Send at most 200 insights per request.");
        }
        var tags = await repo.AgentTagsAsync(agent.AgentId, ct);
        var assets = tags.Values.ToHashSet();
        var added = 0;
        foreach (var i in list)
        {
            if (!assets.Contains(i.AssetId) || (i.TagId is { } t && (!tags.TryGetValue(t, out var owner) || owner != i.AssetId))
                || !InsightKinds.Contains(i.Kind) || i.Severity is not ("Info" or "Warning" or "Critical") || !double.IsFinite(i.Score))
            {
                continue;
            }
            var title = Guard.Required(i.Title, "Title", 200);
            var detail = i.Detail is { Length: > 1000 } d ? d[..1000] : i.Detail;
            if (await insights.UpsertAsync(new NewInsight(agent.TenantId, i.AssetId, i.TagId, i.Kind, i.Severity, title, detail, i.Score, null,
                    $"edge:{agent.Name}"[..Math.Min(60, agent.Name.Length + 5)]), clock.UtcNow, ct))
            {
                added++;
            }
        }
        return added;
    }
}
