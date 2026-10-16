using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Tenancy;
using Divakar.FactoryOS.Domain.Plant;

namespace Divakar.FactoryOS.Application.Plant;

public sealed record SaveNodeRequest(Guid? ParentId, string Level, string Code, string Name, string? Description,
    decimal? Latitude, decimal? Longitude, int? LayoutX, int? LayoutY);

public sealed record SaveAssetRequest(Guid NodeId, string AssetTypeCode, string? ProtocolCode, string Code, string Name,
    string? Vendor, string? Model, string? SerialNo, string? Endpoint, decimal? RatedPowerKw, decimal? IdealCycleSec,
    DateOnly? InstalledOn, int? LayoutX, int? LayoutY);

public sealed record SetLayoutRequest(int X, int Y);

public sealed record TreeNode(PlantNode Node, IReadOnlyList<TreeNode> Children, IReadOnlyList<Asset> Assets);

public sealed record Catalogue(IReadOnlyList<AssetType> AssetTypes, IReadOnlyList<Protocol> Protocols);

public interface IPlantRepository
{
    Task<IReadOnlyList<PlantNode>> NodesAsync(Guid tenantId, CancellationToken ct);
    Task<PlantNode?> NodeAsync(Guid tenantId, Guid nodeId, CancellationToken ct);
    Task<Guid> SaveNodeAsync(Guid tenantId, Guid? nodeId, SaveNodeRequest request, CancellationToken ct);
    Task<(int Children, int Assets)> NodeContentsAsync(Guid tenantId, Guid nodeId, CancellationToken ct);
    Task DeleteNodeAsync(Guid tenantId, Guid nodeId, CancellationToken ct);
    Task<bool> NodeCodeExistsAsync(Guid tenantId, string code, Guid? exceptId, CancellationToken ct);

    Task<IReadOnlyList<Asset>> AssetsAsync(Guid tenantId, Guid? nodeId, CancellationToken ct);
    Task<Asset?> AssetAsync(Guid tenantId, Guid assetId, CancellationToken ct);
    Task<Guid> SaveAssetAsync(Guid tenantId, Guid? assetId, SaveAssetRequest request, CancellationToken ct);
    Task DeleteAssetAsync(Guid tenantId, Guid assetId, CancellationToken ct);
    Task<bool> AssetCodeExistsAsync(Guid tenantId, string code, Guid? exceptId, CancellationToken ct);
    Task SetLayoutAsync(Guid tenantId, Guid assetId, int x, int y, CancellationToken ct);

    Task<Catalogue> CatalogueAsync(CancellationToken ct);
}

public sealed class PlantService(IPlantRepository repo, ITenantRepository tenants, ICurrentUser me, IAuditLog audit)
{
    public Task<Catalogue> CatalogueAsync(CancellationToken ct) => repo.CatalogueAsync(ct);

    /// <summary>The whole hierarchy with assets under their nodes, roots (sites) first.</summary>
    public async Task<IReadOnlyList<TreeNode>> TreeAsync(CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        var nodes = await repo.NodesAsync(tenantId, ct);
        var assets = (await repo.AssetsAsync(tenantId, null, ct)).ToLookup(a => a.NodeId);
        var byParent = nodes.ToLookup(n => n.ParentId);
        TreeNode Build(PlantNode n) => new(n, [.. byParent[n.NodeId].OrderBy(c => c.Code).Select(Build)], [.. assets[n.NodeId].OrderBy(a => a.Code)]);
        return [.. byParent[null].OrderBy(n => n.Code).Select(Build)];
    }

    public async Task<Guid> SaveNodeAsync(Guid? nodeId, SaveNodeRequest request, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        var code = Guard.Code(request.Code, "Code");
        Guard.Required(request.Name, "Name", 150);
        PlantNode? parent = null;
        if (request.ParentId is { } pid)
        {
            parent = await repo.NodeAsync(tenantId, pid, ct) ?? throw AppException.Validation("Parent not found.");
        }
        if (!NodeLevels.IsValidChild(parent?.Level, request.Level))
        {
            throw AppException.Validation(parent is null
                ? "Only a Site can be at the top of the hierarchy."
                : $"A {parent.Level} can only contain {NodeLevels.Ordered.SkipWhile(l => l != parent.Level).Skip(1).FirstOrDefault() ?? "nothing"}.");
        }
        if (nodeId is { } id)
        {
            var existing = await repo.NodeAsync(tenantId, id, ct) ?? throw AppException.NotFound("Node");
            if (existing.Level != request.Level)
            {
                throw AppException.Validation("The level of an existing node cannot change.");
            }
            if (request.ParentId == id)
            {
                throw AppException.Validation("A node cannot be its own parent.");
            }
        }
        else if (request.Level == NodeLevels.Plant)
        {
            await EnsureLimit(tenantId, u => u.Plants, p => p.MaxPlants, "plants", ct);
        }
        if (await repo.NodeCodeExistsAsync(tenantId, code, nodeId, ct))
        {
            throw AppException.Conflict($"Code '{code}' is already used.");
        }
        var saved = await repo.SaveNodeAsync(tenantId, nodeId, request with { Code = code, Name = request.Name.Trim() }, ct);
        await audit.WriteAsync(nodeId is null ? "node.create" : "node.update", "node", saved.ToString(), request, ct);
        return saved;
    }

    public async Task DeleteNodeAsync(Guid nodeId, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        _ = await repo.NodeAsync(tenantId, nodeId, ct) ?? throw AppException.NotFound("Node");
        var (children, assets) = await repo.NodeContentsAsync(tenantId, nodeId, ct);
        if (children > 0 || assets > 0)
        {
            throw AppException.Conflict($"Remove its {children} child nodes and {assets} assets first.");
        }
        await repo.DeleteNodeAsync(tenantId, nodeId, ct);
        await audit.WriteAsync("node.delete", "node", nodeId.ToString(), null, ct);
    }

    public Task<IReadOnlyList<Asset>> AssetsAsync(Guid? nodeId, CancellationToken ct) => repo.AssetsAsync(me.RequireTenant(), nodeId, ct);

    public async Task<Asset> AssetAsync(Guid assetId, CancellationToken ct) =>
        await repo.AssetAsync(me.RequireTenant(), assetId, ct) ?? throw AppException.NotFound("Asset");

    public async Task<Guid> SaveAssetAsync(Guid? assetId, SaveAssetRequest request, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        var code = Guard.Code(request.Code, "Code");
        Guard.Required(request.Name, "Name", 150);
        Guard.Optional(request.Endpoint, "Endpoint", 300);
        _ = await repo.NodeAsync(tenantId, request.NodeId, ct) ?? throw AppException.Validation("Node not found.");
        var catalogue = await repo.CatalogueAsync(ct);
        if (catalogue.AssetTypes.All(t => t.AssetTypeCode != request.AssetTypeCode))
        {
            throw AppException.Validation("Unknown asset type.");
        }
        if (request.ProtocolCode is not null && catalogue.Protocols.All(p => p.ProtocolCode != request.ProtocolCode))
        {
            throw AppException.Validation("Unknown protocol.");
        }
        if (request.RatedPowerKw < 0 || request.IdealCycleSec <= 0)
        {
            throw AppException.Validation("Rated power cannot be negative and the ideal cycle time must be above zero.");
        }
        if (assetId is { } id)
        {
            _ = await repo.AssetAsync(tenantId, id, ct) ?? throw AppException.NotFound("Asset");
        }
        else
        {
            await EnsureLimit(tenantId, u => u.Assets, p => p.MaxAssets, "assets", ct);
        }
        if (await repo.AssetCodeExistsAsync(tenantId, code, assetId, ct))
        {
            throw AppException.Conflict($"Code '{code}' is already used.");
        }
        var saved = await repo.SaveAssetAsync(tenantId, assetId, request with { Code = code, Name = request.Name.Trim() }, ct);
        await audit.WriteAsync(assetId is null ? "asset.create" : "asset.update", "asset", saved.ToString(), request, ct);
        return saved;
    }

    public async Task DeleteAssetAsync(Guid assetId, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        _ = await repo.AssetAsync(tenantId, assetId, ct) ?? throw AppException.NotFound("Asset");
        await repo.DeleteAssetAsync(tenantId, assetId, ct);
        await audit.WriteAsync("asset.delete", "asset", assetId.ToString(), null, ct);
    }

    /// <summary>Places an asset on the floor plan (0..1000 by 0..1000 plan units).</summary>
    public async Task SetLayoutAsync(Guid assetId, SetLayoutRequest request, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        if (request.X is < 0 or > 1000 || request.Y is < 0 or > 1000)
        {
            throw AppException.Validation("The position must be between 0 and 1000.");
        }
        _ = await repo.AssetAsync(tenantId, assetId, ct) ?? throw AppException.NotFound("Asset");
        await repo.SetLayoutAsync(tenantId, assetId, request.X, request.Y, ct);
    }

    private async Task EnsureLimit(Guid tenantId, Func<(int Users, int Plants, int Assets), int> used,
        Func<Domain.Tenancy.Plan, int> max, string what, CancellationToken ct)
    {
        var tenant = await tenants.TenantAsync(tenantId, ct) ?? throw AppException.NotFound("Tenant");
        var plan = await tenants.PlanAsync(tenant.PlanCode, ct) ?? throw AppException.NotFound("Plan");
        if (used(await tenants.UsageAsync(tenantId, ct)) >= max(plan))
        {
            throw AppException.Limit($"Your plan allows {max(plan)} {what}. Upgrade to add more.");
        }
    }
}
