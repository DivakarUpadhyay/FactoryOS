using Dapper;
using Divakar.FactoryOS.Application.Plant;
using Divakar.FactoryOS.Domain.Plant;
using Divakar.FactoryOS.Infrastructure.Database;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class PlantRepository(Db db) : IPlantRepository
{
    private const string NodeSelect = """
        SELECT NodeId, ParentId, Level, Code, Name, Description, Latitude, Longitude, LayoutX, LayoutY, IsActive
        FROM plant.Nodes WHERE TenantId = @tenantId
        """;

    private const string AssetSelect = """
        SELECT AssetId, NodeId, AssetTypeCode, ProtocolCode, Code, Name, Vendor, Model, SerialNo, Endpoint, RatedPowerKw,
               IdealCycleSec, Status, InstalledOn, LayoutX, LayoutY, IsActive
        FROM plant.Assets WHERE TenantId = @tenantId
        """;

    public async Task<IReadOnlyList<PlantNode>> NodesAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<PlantNode>(new CommandDefinition(NodeSelect, new { tenantId }, cancellationToken: ct))];
    }

    public async Task<PlantNode?> NodeAsync(Guid tenantId, Guid nodeId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.QuerySingleOrDefaultAsync<PlantNode>(new CommandDefinition(
            NodeSelect + " AND NodeId = @nodeId", new { tenantId, nodeId }, cancellationToken: ct));
    }

    public async Task<Guid> SaveNodeAsync(Guid tenantId, Guid? nodeId, SaveNodeRequest r, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var id = nodeId ?? Guid.NewGuid();
        await con.ExecuteAsync(new CommandDefinition("""
            IF EXISTS (SELECT 1 FROM plant.Nodes WHERE NodeId = @id AND TenantId = @tenantId)
                UPDATE plant.Nodes SET ParentId = @ParentId, Code = @Code, Name = @Name, Description = @Description,
                       Latitude = @Latitude, Longitude = @Longitude, LayoutX = @LayoutX, LayoutY = @LayoutY
                WHERE NodeId = @id AND TenantId = @tenantId;
            ELSE
                INSERT plant.Nodes (NodeId, TenantId, ParentId, Level, Code, Name, Description, Latitude, Longitude, LayoutX, LayoutY)
                VALUES (@id, @tenantId, @ParentId, @Level, @Code, @Name, @Description, @Latitude, @Longitude, @LayoutX, @LayoutY);
            """, new { id, tenantId, r.ParentId, r.Level, r.Code, r.Name, r.Description, r.Latitude, r.Longitude, r.LayoutX, r.LayoutY },
            cancellationToken: ct));
        return id;
    }

    public async Task<(int Children, int Assets)> NodeContentsAsync(Guid tenantId, Guid nodeId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.QuerySingleAsync<(int, int)>(new CommandDefinition("""
            SELECT (SELECT COUNT(*) FROM plant.Nodes WHERE TenantId = @tenantId AND ParentId = @nodeId),
                   (SELECT COUNT(*) FROM plant.Assets WHERE TenantId = @tenantId AND NodeId = @nodeId)
            """, new { tenantId, nodeId }, cancellationToken: ct));
    }

    public async Task DeleteNodeAsync(Guid tenantId, Guid nodeId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("DELETE plant.Nodes WHERE NodeId = @nodeId AND TenantId = @tenantId",
            new { tenantId, nodeId }, cancellationToken: ct));
    }

    public async Task<bool> NodeCodeExistsAsync(Guid tenantId, string code, Guid? exceptId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM plant.Nodes WHERE TenantId = @tenantId AND Code = @code AND (@exceptId IS NULL OR NodeId <> @exceptId)
            """, new { tenantId, code, exceptId }, cancellationToken: ct)) > 0;
    }

    public async Task<IReadOnlyList<Asset>> AssetsAsync(Guid tenantId, Guid? nodeId, CancellationToken ct)
    {
        DapperSetup.Once();
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Asset>(new CommandDefinition(
            AssetSelect + " AND (@nodeId IS NULL OR NodeId = @nodeId) ORDER BY Code", new { tenantId, nodeId }, cancellationToken: ct))];
    }

    public async Task<Asset?> AssetAsync(Guid tenantId, Guid assetId, CancellationToken ct)
    {
        DapperSetup.Once();
        await using var con = await db.OpenAsync(ct);
        return await con.QuerySingleOrDefaultAsync<Asset>(new CommandDefinition(
            AssetSelect + " AND AssetId = @assetId", new { tenantId, assetId }, cancellationToken: ct));
    }

    public async Task<Guid> SaveAssetAsync(Guid tenantId, Guid? assetId, SaveAssetRequest r, CancellationToken ct)
    {
        DapperSetup.Once();
        await using var con = await db.OpenAsync(ct);
        var id = assetId ?? Guid.NewGuid();
        await con.ExecuteAsync(new CommandDefinition("""
            IF EXISTS (SELECT 1 FROM plant.Assets WHERE AssetId = @id AND TenantId = @tenantId)
                UPDATE plant.Assets SET NodeId = @NodeId, AssetTypeCode = @AssetTypeCode, ProtocolCode = @ProtocolCode, Code = @Code,
                       Name = @Name, Vendor = @Vendor, Model = @Model, SerialNo = @SerialNo, Endpoint = @Endpoint,
                       RatedPowerKw = @RatedPowerKw, IdealCycleSec = @IdealCycleSec, InstalledOn = @InstalledOn,
                       LayoutX = @LayoutX, LayoutY = @LayoutY
                WHERE AssetId = @id AND TenantId = @tenantId;
            ELSE
                INSERT plant.Assets (AssetId, TenantId, NodeId, AssetTypeCode, ProtocolCode, Code, Name, Vendor, Model, SerialNo,
                                     Endpoint, RatedPowerKw, IdealCycleSec, InstalledOn, LayoutX, LayoutY)
                VALUES (@id, @tenantId, @NodeId, @AssetTypeCode, @ProtocolCode, @Code, @Name, @Vendor, @Model, @SerialNo,
                        @Endpoint, @RatedPowerKw, @IdealCycleSec, @InstalledOn, @LayoutX, @LayoutY);
            """, new
        {
            id, tenantId, r.NodeId, r.AssetTypeCode, r.ProtocolCode, r.Code, r.Name, r.Vendor, r.Model, r.SerialNo, r.Endpoint,
            r.RatedPowerKw, r.IdealCycleSec, r.InstalledOn, r.LayoutX, r.LayoutY,
        }, cancellationToken: ct));
        return id;
    }

    public async Task DeleteAssetAsync(Guid tenantId, Guid assetId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("DELETE plant.Assets WHERE AssetId = @assetId AND TenantId = @tenantId",
            new { tenantId, assetId }, cancellationToken: ct));
    }

    public async Task SetLayoutAsync(Guid tenantId, Guid assetId, int x, int y, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("UPDATE plant.Assets SET LayoutX = @x, LayoutY = @y WHERE AssetId = @assetId AND TenantId = @tenantId",
            new { tenantId, assetId, x, y }, cancellationToken: ct));
    }

    public async Task<bool> AssetCodeExistsAsync(Guid tenantId, string code, Guid? exceptId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM plant.Assets WHERE TenantId = @tenantId AND Code = @code AND (@exceptId IS NULL OR AssetId <> @exceptId)
            """, new { tenantId, code, exceptId }, cancellationToken: ct)) > 0;
    }

    public async Task<Catalogue> CatalogueAsync(CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        using var grid = await con.QueryMultipleAsync(new CommandDefinition("""
            SELECT AssetTypeCode, Category, Name, Icon FROM plant.AssetTypes ORDER BY Category, Name;
            SELECT ProtocolCode, Family, Name, Transport, DefaultPort, Notes, SupportLevel FROM plant.Protocols ORDER BY Family, Name;
            """, cancellationToken: ct));
        return new Catalogue([.. await grid.ReadAsync<AssetType>()], [.. await grid.ReadAsync<Protocol>()]);
    }
}
