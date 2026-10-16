using Dapper;
using Divakar.FactoryOS.Application.Robotics;
using Divakar.FactoryOS.Infrastructure.Database;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class RoboticsRepository(Db db) : IRoboticsRepository
{
    public async Task<IReadOnlyList<RobotAsset>> RobotsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<RobotAsset>(new CommandDefinition("""
            SELECT AssetId, Code, Name, AssetTypeCode, Vendor, SerialNo, ProtocolCode, Status, LastSeenAt
            FROM plant.Assets
            WHERE TenantId = @tenantId AND IsActive = 1 AND AssetTypeCode IN ('robot-arm', 'cobot', 'agv', 'amr')
            ORDER BY AssetTypeCode, Code
            """, new { tenantId }, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<RobotSignal>> SignalsAsync(Guid tenantId, DateTime since, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<RobotSignal>(new CommandDefinition("""
            SELECT t.AssetId, t.SensorTypeCode, l.Value, x.MaxV AS Max24h, l.Ts
            FROM plant.Tags t
            JOIN plant.Assets a ON a.AssetId = t.AssetId AND a.AssetTypeCode IN ('robot-arm', 'cobot', 'agv', 'amr')
            LEFT JOIN telemetry.Latest l ON l.TagId = t.TagId
            OUTER APPLY (SELECT MAX(r.Value) AS MaxV FROM telemetry.Readings r
                         WHERE r.TagId = t.TagId AND r.Ts >= @since AND r.Quality <= 1
                           AND t.SensorTypeCode IN ('robot_joint_temp', 'robot_joint_torque')) x
            WHERE t.TenantId = @tenantId AND t.IsActive = 1
              AND t.SensorTypeCode IN ('battery_soc', 'position_x', 'position_y', 'vehicle_speed', 'part_count', 'robot_joint_temp',
                                       'robot_joint_torque', 'robot_tcp_speed', 'alarm_code')
            """, new { tenantId, since }, commandTimeout: 60, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<Station>> StationsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Station>(new CommandDefinition(
            "SELECT StationId, Code, Name, X, Y FROM robotics.Stations WHERE TenantId = @tenantId ORDER BY Code", new { tenantId }, cancellationToken: ct))];
    }

    public async Task<Guid> SaveStationAsync(Guid tenantId, Guid? id, SaveStationRequest r, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        if (id is { } existing)
        {
            await con.ExecuteAsync(new CommandDefinition(
                "UPDATE robotics.Stations SET Code = @Code, Name = @Name, X = @X, Y = @Y WHERE TenantId = @tenantId AND StationId = @existing",
                new { tenantId, existing, r.Code, r.Name, r.X, r.Y }, cancellationToken: ct));
            return existing;
        }
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT robotics.Stations (TenantId, Code, Name, X, Y) OUTPUT inserted.StationId VALUES (@tenantId, @Code, @Name, @X, @Y)
            """, new { tenantId, r.Code, r.Name, r.X, r.Y }, cancellationToken: ct));
    }

    public async Task<bool> StationCodeExistsAsync(Guid tenantId, string code, Guid? exceptId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM robotics.Stations WHERE TenantId = @tenantId AND Code = @code AND (@exceptId IS NULL OR StationId <> @exceptId)",
            new { tenantId, code, exceptId }, cancellationToken: ct)) > 0;
    }

    private const string MissionSelect = """
        SELECT m.MissionId, m.Code, m.FromStationId, f.Name AS FromStation, m.ToStationId, t.Name AS ToStation, m.Priority, m.Payload,
               m.VehicleAssetId, a.Name AS VehicleName, m.Status, m.OrderId, m.Error, m.CreatedAt, m.AssignedAt, m.PickedUpAt, m.FinishedAt
        FROM robotics.Missions m
        JOIN robotics.Stations f ON f.StationId = m.FromStationId
        JOIN robotics.Stations t ON t.StationId = m.ToStationId
        LEFT JOIN plant.Assets a ON a.AssetId = m.VehicleAssetId
        """;

    public async Task<IReadOnlyList<Mission>> MissionsAsync(Guid tenantId, bool activeOnly, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Mission>(new CommandDefinition($"""
            {MissionSelect}
            WHERE m.TenantId = @tenantId
              AND (@activeOnly = 0 OR m.Status IN ('Queued', 'Assigned', 'PickedUp'))
              AND (@activeOnly = 1 OR m.CreatedAt > DATEADD(DAY, -7, SYSUTCDATETIME()))
            ORDER BY CASE WHEN m.Status IN ('Queued', 'Assigned', 'PickedUp') THEN 0 ELSE 1 END, m.Priority, m.CreatedAt DESC
            """, new { tenantId, activeOnly }, cancellationToken: ct))];
    }

    public async Task<Mission?> MissionAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.QuerySingleOrDefaultAsync<Mission>(new CommandDefinition(
            $"{MissionSelect} WHERE m.TenantId = @tenantId AND m.MissionId = @id", new { tenantId, id }, cancellationToken: ct));
    }

    public async Task<Guid> CreateMissionAsync(Guid tenantId, string code, CreateMissionRequest r, Guid userId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT robotics.Missions (TenantId, Code, FromStationId, ToStationId, Priority, Payload, VehicleAssetId, CreatedBy)
            OUTPUT inserted.MissionId
            VALUES (@tenantId, @code, @FromStationId, @ToStationId, @Priority, @Payload, @VehicleAssetId, @userId)
            """, new { tenantId, code, r.FromStationId, r.ToStationId, r.Priority, r.Payload, r.VehicleAssetId, userId }, cancellationToken: ct));
    }

    public async Task CancelMissionAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition(
            "UPDATE robotics.Missions SET Status = 'Cancelled', FinishedAt = SYSUTCDATETIME() WHERE TenantId = @tenantId AND MissionId = @id",
            new { tenantId, id }, cancellationToken: ct));
    }
}

public sealed class FleetStore(Db db) : IFleetStore
{
    public async Task<IReadOnlyList<OpenMission>> OpenMissionsAsync(CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<OpenMission>(new CommandDefinition("""
            SELECT m.TenantId, m.MissionId, m.Code, m.Status, m.VehicleAssetId, m.Priority, m.CreatedAt, m.AssignedAt,
                   f.StationId AS FromStationId, f.Code AS FromCode, f.X AS FromX, f.Y AS FromY,
                   t.StationId AS ToStationId, t.Code AS ToCode, t.X AS ToX, t.Y AS ToY
            FROM robotics.Missions m
            JOIN robotics.Stations f ON f.StationId = m.FromStationId
            JOIN robotics.Stations t ON t.StationId = m.ToStationId
            WHERE m.Status IN ('Queued', 'Assigned', 'PickedUp')
            """, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<VehicleState>> VehiclesAsync(CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<VehicleState>(new CommandDefinition("""
            SELECT a.TenantId, a.AssetId, a.Name, a.Vendor, a.SerialNo, a.ProtocolCode, a.Endpoint, a.ConnectorSettings AS Settings, a.Status,
                   (SELECT TOP 1 l.Value FROM plant.Tags g JOIN telemetry.Latest l ON l.TagId = g.TagId WHERE g.AssetId = a.AssetId AND g.SensorTypeCode = 'battery_soc') AS Battery,
                   (SELECT TOP 1 l.Value FROM plant.Tags g JOIN telemetry.Latest l ON l.TagId = g.TagId WHERE g.AssetId = a.AssetId AND g.SensorTypeCode = 'position_x') AS X,
                   (SELECT TOP 1 l.Value FROM plant.Tags g JOIN telemetry.Latest l ON l.TagId = g.TagId WHERE g.AssetId = a.AssetId AND g.SensorTypeCode = 'position_y') AS Y,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM robotics.Missions m WHERE m.VehicleAssetId = a.AssetId AND m.Status IN ('Assigned', 'PickedUp'))
                             THEN 1 ELSE 0 END AS bit) AS Busy
            FROM plant.Assets a
            WHERE a.IsActive = 1 AND a.AssetTypeCode IN ('agv', 'amr')
            """, cancellationToken: ct))];
    }

    public async Task AssignAsync(Guid missionId, Guid vehicleId, string orderId, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            UPDATE robotics.Missions SET Status = 'Assigned', VehicleAssetId = @vehicleId, OrderId = @orderId, AssignedAt = SYSUTCDATETIME()
            WHERE MissionId = @missionId AND Status = 'Queued'
            """, new { missionId, vehicleId, orderId }, cancellationToken: ct));
    }

    public async Task SetStatusAsync(Guid missionId, string status, string? error, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            UPDATE robotics.Missions
            SET Status = @status, Error = @error,
                PickedUpAt = CASE WHEN @status = 'PickedUp' THEN SYSUTCDATETIME() ELSE PickedUpAt END,
                FinishedAt = CASE WHEN @status IN ('Done', 'Failed') THEN SYSUTCDATETIME() ELSE FinishedAt END
            WHERE MissionId = @missionId AND Status IN ('Queued', 'Assigned', 'PickedUp')
            """, new { missionId, status, error }, cancellationToken: ct));
    }
}
