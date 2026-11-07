using Dapper;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Infrastructure.Database;
using Divakar.FactoryOS.Infrastructure.Security;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class EdgeRepository(Db db, SecretProtector secrets) : IEdgeRepository
{
    public async Task<IReadOnlyList<EdgeAgentInfo>> AgentsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<EdgeAgentInfo>(new CommandDefinition("""
            SELECT g.AgentId, g.Name, g.KeyPrefix, g.IsActive, g.Version, g.Hostname, g.Buffered, g.Dropped, g.LastError, g.LastSeenAt, g.CreatedAt,
                   ISNULL((SELECT '[' + STRING_AGG('"' + CAST(x.AssetId AS varchar(36)) + '"', ',') + ']' FROM plant.EdgeAgentAssets x WHERE x.AgentId = g.AgentId), '[]') AS AssetIds
            FROM plant.EdgeAgents g
            WHERE g.TenantId = @tenantId
            ORDER BY g.Name
            """, new { tenantId }, cancellationToken: ct))];
    }

    public async Task<Guid> CreateAsync(Guid tenantId, string name, string keyHash, string prefix, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT plant.EdgeAgents (TenantId, Name, KeyHash, KeyPrefix) OUTPUT inserted.AgentId VALUES (@tenantId, @name, @keyHash, @prefix)
            """, new { tenantId, name, keyHash, prefix }, cancellationToken: ct));
    }

    public async Task RotateKeyAsync(Guid tenantId, Guid agentId, string keyHash, string prefix, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var n = await con.ExecuteAsync(new CommandDefinition(
            "UPDATE plant.EdgeAgents SET KeyHash = @keyHash, KeyPrefix = @prefix WHERE TenantId = @tenantId AND AgentId = @agentId",
            new { tenantId, agentId, keyHash, prefix }, cancellationToken: ct));
        if (n == 0)
        {
            throw AppException.NotFound("Edge agent");
        }
    }

    public async Task SetActiveAsync(Guid tenantId, Guid agentId, bool active, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var n = await con.ExecuteAsync(new CommandDefinition("UPDATE plant.EdgeAgents SET IsActive = @active WHERE TenantId = @tenantId AND AgentId = @agentId",
            new { tenantId, agentId, active }, cancellationToken: ct));
        if (n == 0)
        {
            throw AppException.NotFound("Edge agent");
        }
    }

    public async Task DeleteAsync(Guid tenantId, Guid agentId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var n = await con.ExecuteAsync(new CommandDefinition("DELETE FROM plant.EdgeAgents WHERE TenantId = @tenantId AND AgentId = @agentId",
            new { tenantId, agentId }, cancellationToken: ct));
        if (n == 0)
        {
            throw AppException.NotFound("Edge agent");
        }
    }

    public async Task SetAssetsAsync(Guid tenantId, Guid agentId, IReadOnlyList<Guid> assetIds, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        if (await con.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM plant.EdgeAgents WHERE TenantId = @tenantId AND AgentId = @agentId",
                new { tenantId, agentId }, cancellationToken: ct)) == 0)
        {
            throw AppException.NotFound("Edge agent");
        }
        var known = (await con.QueryAsync<Guid>(new CommandDefinition("SELECT AssetId FROM plant.Assets WHERE TenantId = @tenantId AND AssetId IN @assetIds",
            new { tenantId, assetIds }, cancellationToken: ct))).ToHashSet();
        if (assetIds.Any(a => !known.Contains(a)))
        {
            throw AppException.NotFound("Asset");
        }
        var taken = (await con.QueryAsync<string>(new CommandDefinition("""
            SELECT a.Name + N' (' + g.Name + N')' FROM plant.EdgeAgentAssets x
            JOIN plant.Assets a ON a.AssetId = x.AssetId JOIN plant.EdgeAgents g ON g.AgentId = x.AgentId
            WHERE x.TenantId = @tenantId AND x.AgentId <> @agentId AND x.AssetId IN @assetIds
            """, new { tenantId, agentId, assetIds }, cancellationToken: ct))).ToList();
        if (taken.Count > 0)
        {
            throw AppException.Conflict($"Already read by another agent: {string.Join(", ", taken)}.");
        }
        await using var tx = await con.BeginTransactionAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("DELETE FROM plant.EdgeAgentAssets WHERE TenantId = @tenantId AND AgentId = @agentId",
            new { tenantId, agentId }, tx, cancellationToken: ct));
        foreach (var assetId in assetIds)
        {
            await con.ExecuteAsync(new CommandDefinition("INSERT plant.EdgeAgentAssets (TenantId, AgentId, AssetId) VALUES (@tenantId, @agentId, @assetId)",
                new { tenantId, agentId, assetId }, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
    }

    public async Task<EdgeAgentKey?> AgentForKeyAsync(string keyHash, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return await con.QuerySingleOrDefaultAsync<EdgeAgentKey>(new CommandDefinition("""
            SELECT g.AgentId, g.TenantId, g.Name, CAST(CASE WHEN g.IsActive = 1 AND tn.Status IN ('Trial', 'Active') THEN 1 ELSE 0 END AS bit) AS IsActive
            FROM plant.EdgeAgents g JOIN core.Tenants tn ON tn.TenantId = g.TenantId
            WHERE g.KeyHash = @keyHash
            """, new { keyHash }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AssetBinding>> BindingsAsync(Guid agentId, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return await BindingSql.ReadAsync(con, BindingSql.Select + """
             JOIN plant.EdgeAgentAssets x ON x.AssetId = a.AssetId AND x.AgentId = @agentId
            WHERE a.IsActive = 1
            """, new { agentId }, secrets, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, Guid>> AgentTagsAsync(Guid agentId, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return (await con.QueryAsync<(Guid TagId, Guid AssetId)>(new CommandDefinition("""
            SELECT t.TagId, t.AssetId FROM plant.Tags t JOIN plant.EdgeAgentAssets x ON x.AssetId = t.AssetId AND x.AgentId = @agentId
            WHERE t.IsActive = 1
            """, new { agentId }, cancellationToken: ct))).ToDictionary(x => x.TagId, x => x.AssetId);
    }

    public async Task HeartbeatAsync(Guid agentId, EdgeHeartbeat h, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            UPDATE plant.EdgeAgents SET Version = @Version, Hostname = @Hostname, Buffered = @Buffered, Dropped = @Dropped, LastError = @LastError,
                   LastSeenAt = SYSUTCDATETIME()
            WHERE AgentId = @agentId
            """, new { agentId, h.Version, h.Hostname, h.Buffered, h.Dropped, h.LastError }, cancellationToken: ct));
    }
}
