using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Production;
using Divakar.FactoryOS.Domain.Plant;

namespace Divakar.FactoryOS.Application.Robotics;

/// <summary>Newest value of one sensor type on one robot or vehicle, plus its 24-hour maximum.</summary>
public sealed record RobotSignal(Guid AssetId, string SensorTypeCode, double? Value, double? Max24h, DateTime? Ts);

public sealed record RobotAsset(Guid AssetId, string Code, string Name, string AssetTypeCode, string? Vendor, string? SerialNo, string? ProtocolCode,
    string Status, DateTime? LastSeenAt);

public sealed record Station(Guid StationId, string Code, string Name, decimal X, decimal Y);

public sealed record Mission(Guid MissionId, string Code, Guid FromStationId, string FromStation, Guid ToStationId, string ToStation, byte Priority,
    string? Payload, Guid? VehicleAssetId, string? VehicleName, string Status, string? OrderId, string? Error, DateTime CreatedAt,
    DateTime? AssignedAt, DateTime? PickedUpAt, DateTime? FinishedAt);

public sealed record SaveStationRequest(string Code, string Name, decimal X, decimal Y);

public sealed record CreateMissionRequest(Guid FromStationId, Guid ToStationId, byte Priority = 2, string? Payload = null, Guid? VehicleAssetId = null);

public sealed record FleetMember(Guid AssetId, string Code, string Name, string Kind, string AssetTypeCode, string? ProtocolCode, string Status,
    DateTime? LastSeenAt, double? Battery, double? X, double? Y, double? Speed, double? Missions, double? Cycles, double? JointTemp,
    double? JointTempMax24h, double? Torque, double? TorqueMax24h, double? TcpSpeed, double? AlarmCode, double? UtilizationPct,
    string Health, IReadOnlyList<string> Warnings, string? CurrentMission);

public sealed record Fleet(IReadOnlyList<FleetMember> Robots, IReadOnlyList<FleetMember> Vehicles, IReadOnlyList<Station> Stations,
    IReadOnlyList<Mission> ActiveMissions, double MapWidth, double MapHeight);

/// <summary>A vehicle as the dispatcher sees it: where it is, how charged, whether it is free.</summary>
public sealed record VehicleState(Guid TenantId, Guid AssetId, string Name, string? Vendor, string? SerialNo, string? ProtocolCode, string? Endpoint,
    string? Settings, string Status, double? Battery, double? X, double? Y, bool Busy);

public sealed record OpenMission(Guid TenantId, Guid MissionId, string Code, string Status, Guid? VehicleAssetId, byte Priority, DateTime CreatedAt,
    DateTime? AssignedAt, Guid FromStationId, string FromCode, decimal FromX, decimal FromY, Guid ToStationId, string ToCode, decimal ToX, decimal ToY);

public interface IRoboticsRepository
{
    Task<IReadOnlyList<RobotAsset>> RobotsAsync(Guid tenantId, CancellationToken ct);
    Task<IReadOnlyList<RobotSignal>> SignalsAsync(Guid tenantId, DateTime since, CancellationToken ct);
    Task<IReadOnlyList<Station>> StationsAsync(Guid tenantId, CancellationToken ct);
    Task<Guid> SaveStationAsync(Guid tenantId, Guid? id, SaveStationRequest r, CancellationToken ct);
    Task<bool> StationCodeExistsAsync(Guid tenantId, string code, Guid? exceptId, CancellationToken ct);
    Task<IReadOnlyList<Mission>> MissionsAsync(Guid tenantId, bool activeOnly, CancellationToken ct);
    Task<Mission?> MissionAsync(Guid tenantId, Guid id, CancellationToken ct);
    Task<Guid> CreateMissionAsync(Guid tenantId, string code, CreateMissionRequest r, Guid userId, CancellationToken ct);
    Task CancelMissionAsync(Guid tenantId, Guid id, CancellationToken ct);
}

/// <summary>Fleet bookkeeping for every company (system connection), used by the worker's dispatcher.</summary>
public interface IFleetStore
{
    Task<IReadOnlyList<OpenMission>> OpenMissionsAsync(CancellationToken ct);
    Task<IReadOnlyList<VehicleState>> VehiclesAsync(CancellationToken ct);
    Task AssignAsync(Guid missionId, Guid vehicleId, string orderId, CancellationToken ct);
    Task SetStatusAsync(Guid missionId, string status, string? error, CancellationToken ct);
}

/// <summary>Sends a transport order to a real vehicle (VDA 5050 over MQTT). Simulated vehicles need no order.</summary>
public interface IOrderPublisher
{
    bool Handles(string? protocolCode);
    Task PublishAsync(VehicleState vehicle, OpenMission mission, string orderId, CancellationToken ct);
}

public sealed class RoboticsService(IRoboticsRepository repo, IProductionRepository production, Settings.ModuleGate gate, ICurrentUser me, IAuditLog audit)
{
    public static readonly string[] RobotTypes = ["robot-arm", "cobot"];
    public static readonly string[] VehicleTypes = ["agv", "amr"];

    /// <summary>Health flags from joint temperature, torque, battery and alarms.</summary>
    public static (string Health, List<string> Warnings) Assess(string status, double? jointTempMax, double? torqueMax, double? battery, double? alarm)
    {
        var w = new List<string>();
        var bad = false;
        if (status == "Fault")
        {
            w.Add(alarm is > 0 ? $"In fault (alarm {alarm:0})" : "In fault");
            bad = true;
        }
        if (jointTempMax > 85)
        {
            w.Add($"Joint temperature reached {jointTempMax:0} °C — check lubrication and load before it trips");
            bad = true;
        }
        else if (jointTempMax > 75)
        {
            w.Add($"Joint temperature high ({jointTempMax:0} °C)");
        }
        if (torqueMax > 95)
        {
            w.Add($"Joint torque at {torqueMax:0} % — possible collision or gearbox wear");
            bad = true;
        }
        else if (torqueMax > 85)
        {
            w.Add($"Joint torque high ({torqueMax:0} %)");
        }
        if (battery < 15)
        {
            w.Add($"Battery {battery:0} % — send to charger");
            bad = true;
        }
        else if (battery < 25)
        {
            w.Add($"Battery low ({battery:0} %)");
        }
        if (status == "Offline")
        {
            w.Add("No data");
        }
        return (bad ? "bad" : w.Count > 0 ? "warn" : "ok", w);
    }

    public async Task<Fleet> FleetAsync(CancellationToken ct)
    {
        await gate.EnsureAsync("robotics", ct);
        var tenantId = me.RequireTenant();
        var now = DateTime.UtcNow;
        var assets = await repo.RobotsAsync(tenantId, ct);
        var signals = (await repo.SignalsAsync(tenantId, now.AddHours(-24), ct)).ToLookup(s => s.AssetId);
        var states = (await production.StateSecondsAsync(tenantId, now.AddHours(-24), now, ct)).ToLookup(s => s.AssetId);
        var missions = await repo.MissionsAsync(tenantId, activeOnly: true, ct);

        FleetMember Member(RobotAsset a, string kind)
        {
            var sig = signals[a.AssetId].ToDictionary(s => s.SensorTypeCode);
            double? V(string type) => sig.TryGetValue(type, out var s) ? s.Value : null;
            double? M(string type) => sig.TryGetValue(type, out var s) ? s.Max24h : null;
            var total = states[a.AssetId].Where(s => s.State != MachineStates.Offline).Sum(s => s.Seconds);
            var run = states[a.AssetId].Where(s => s.State == MachineStates.Running).Sum(s => s.Seconds);
            var (health, warnings) = Assess(a.Status, M("robot_joint_temp"), M("robot_joint_torque"), V("battery_soc"), V("alarm_code"));
            var mission = missions.FirstOrDefault(m => m.VehicleAssetId == a.AssetId);
            return new FleetMember(a.AssetId, a.Code, a.Name, kind, a.AssetTypeCode, a.ProtocolCode, a.Status, a.LastSeenAt,
                V("battery_soc"), V("position_x"), V("position_y"), V("vehicle_speed"),
                kind == "vehicle" ? V("part_count") : null, kind == "robot" ? V("part_count") : null,
                V("robot_joint_temp"), M("robot_joint_temp"), V("robot_joint_torque"), M("robot_joint_torque"), V("robot_tcp_speed"), V("alarm_code"),
                total > 0 ? Math.Round(run / total * 100, 1) : null, health, warnings,
                mission is null ? null : $"{mission.Code}: {mission.FromStation} → {mission.ToStation} ({mission.Status})");
        }

        return new Fleet(
            [.. assets.Where(a => RobotTypes.Contains(a.AssetTypeCode)).Select(a => Member(a, "robot"))],
            [.. assets.Where(a => VehicleTypes.Contains(a.AssetTypeCode)).Select(a => Member(a, "vehicle"))],
            await repo.StationsAsync(tenantId, ct), missions, 200, 100);
    }

    public async Task<IReadOnlyList<Station>> StationsAsync(CancellationToken ct)
    {
        await gate.EnsureAsync("robotics", ct);
        return await repo.StationsAsync(me.RequireTenant(), ct);
    }

    public async Task<Guid> SaveStationAsync(Guid? id, SaveStationRequest r, CancellationToken ct)
    {
        await gate.EnsureAsync("robotics", ct);
        var tenantId = me.RequireTenant();
        var code = Guard.Code(r.Code, "Code");
        if (r.X is < 0 or > 10_000 || r.Y is < 0 or > 10_000)
        {
            throw AppException.Validation("Station coordinates must be between 0 and 10 000 m.");
        }
        if (await repo.StationCodeExistsAsync(tenantId, code, id, ct))
        {
            throw AppException.Conflict($"Station '{code}' already exists.");
        }
        if (id is { } existing && (await repo.StationsAsync(tenantId, ct)).All(s => s.StationId != existing))
        {
            throw AppException.NotFound("Station");
        }
        var saved = await repo.SaveStationAsync(tenantId, id, r with { Code = code, Name = Guard.Required(r.Name, "Name", 100) }, ct);
        await audit.WriteAsync(id is null ? "station.create" : "station.update", "station", saved.ToString(), null, ct);
        return saved;
    }

    public async Task<IReadOnlyList<Mission>> MissionsAsync(bool activeOnly, CancellationToken ct)
    {
        await gate.EnsureAsync("robotics", ct);
        return await repo.MissionsAsync(me.RequireTenant(), activeOnly, ct);
    }

    public async Task<Guid> CreateMissionAsync(CreateMissionRequest r, CancellationToken ct)
    {
        await gate.EnsureAsync("robotics", ct);
        var tenantId = me.RequireTenant();
        var stations = await repo.StationsAsync(tenantId, ct);
        if (stations.All(s => s.StationId != r.FromStationId) || stations.All(s => s.StationId != r.ToStationId))
        {
            throw AppException.NotFound("Station");
        }
        if (r.FromStationId == r.ToStationId)
        {
            throw AppException.Validation("Pick-up and drop-off must be different stations.");
        }
        if (r.Priority is < 1 or > 3)
        {
            throw AppException.Validation("Priority is 1 (urgent), 2 (normal) or 3 (low).");
        }
        if (r.VehicleAssetId is { } v && (await repo.RobotsAsync(tenantId, ct)).All(a => a.AssetId != v || !VehicleTypes.Contains(a.AssetTypeCode)))
        {
            throw AppException.NotFound("Vehicle");
        }
        var code = $"m-{DateTime.UtcNow:yyMMdd-HHmmss}-{Random.Shared.Next(100, 999)}";
        var id = await repo.CreateMissionAsync(tenantId, code, r with { Payload = Guard.Optional(r.Payload, "Payload", 100) }, me.UserId, ct);
        await audit.WriteAsync("mission.create", "mission", id.ToString(), new { code }, ct);
        return id;
    }

    public async Task CancelMissionAsync(Guid id, CancellationToken ct)
    {
        await gate.EnsureAsync("robotics", ct);
        var tenantId = me.RequireTenant();
        var m = await repo.MissionAsync(tenantId, id, ct) ?? throw AppException.NotFound("Mission");
        if (m.Status is "Done" or "Failed" or "Cancelled")
        {
            throw AppException.Validation($"The mission is already {m.Status.ToLowerInvariant()}.");
        }
        await repo.CancelMissionAsync(tenantId, id, ct);
        await audit.WriteAsync("mission.cancel", "mission", id.ToString(), null, ct);
    }
}

/// <summary>
/// Runs in the worker: gives queued missions to the best free vehicle (enough battery, nearest to the pick-up), sends real
/// vehicles a VDA 5050 order, and follows progress from the vehicle's position: near the pick-up = picked up, near the
/// drop-off afterwards = done. A mission not finished within 30 minutes of assignment fails.
/// </summary>
public sealed class FleetDispatcher(IFleetStore store, IEnumerable<IOrderPublisher> publishers)
{
    public const double ArrivalRadiusM = 6;
    public const double MinBattery = 25;
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    public static double Distance(double? x, double? y, decimal tx, decimal ty) =>
        x is null || y is null ? double.MaxValue : Math.Sqrt(Math.Pow(x.Value - (double)tx, 2) + Math.Pow(y.Value - (double)ty, 2));

    /// <summary>The free vehicle closest to the pick-up with enough battery, or null.</summary>
    public static VehicleState? Choose(OpenMission m, IEnumerable<VehicleState> vehicles) =>
        vehicles.Where(v => v.TenantId == m.TenantId && !v.Busy && v.Status is not ("Offline" or "Fault" or "Maintenance") && (v.Battery ?? 0) >= MinBattery)
            .OrderBy(v => Distance(v.X, v.Y, m.FromX, m.FromY)).FirstOrDefault();

    public async Task<int> RunOnceAsync(DateTime now, CancellationToken ct)
    {
        var changes = 0;
        var vehicles = (await store.VehiclesAsync(ct)).ToList();
        foreach (var m in (await store.OpenMissionsAsync(ct)).OrderBy(m => m.Priority).ThenBy(m => m.CreatedAt))
        {
            var vehicle = m.VehicleAssetId is { } vid ? vehicles.FirstOrDefault(v => v.AssetId == vid) : null;
            switch (m.Status)
            {
                case "Queued":
                    // a mission created for a named vehicle waits for that vehicle
                    var pick = vehicle is not null ? (vehicle.Busy || (vehicle.Battery ?? 0) < MinBattery ? null : vehicle) : Choose(m, vehicles);
                    if (pick is null)
                    {
                        continue;
                    }
                    var orderId = $"{m.Code}-{now:HHmmss}";
                    var publisher = publishers.FirstOrDefault(p => p.Handles(pick.ProtocolCode));
                    try
                    {
                        if (publisher is not null)
                        {
                            await publisher.PublishAsync(pick, m, orderId, ct);
                        }
                        await store.AssignAsync(m.MissionId, pick.AssetId, orderId, ct);
                        vehicles[vehicles.IndexOf(pick)] = pick with { Busy = true };
                    }
                    catch (Exception ex)
                    {
                        await store.SetStatusAsync(m.MissionId, "Failed", $"Could not send the order to {pick.Name}: {ex.Message}", ct);
                    }
                    changes++;
                    break;
                case "Assigned" when vehicle is not null && Distance(vehicle.X, vehicle.Y, m.FromX, m.FromY) <= ArrivalRadiusM:
                    await store.SetStatusAsync(m.MissionId, "PickedUp", null, ct);
                    changes++;
                    break;
                case "PickedUp" when vehicle is not null && Distance(vehicle.X, vehicle.Y, m.ToX, m.ToY) <= ArrivalRadiusM:
                    await store.SetStatusAsync(m.MissionId, "Done", null, ct);
                    changes++;
                    break;
                case "Assigned" or "PickedUp" when m.AssignedAt is { } at && now - at > Timeout:
                    await store.SetStatusAsync(m.MissionId, "Failed", "Not completed within 30 minutes", ct);
                    changes++;
                    break;
                case "Assigned" or "PickedUp" when vehicle is { Status: "Fault" or "Offline" }:
                    await store.SetStatusAsync(m.MissionId, "Failed", $"{vehicle.Name} went {vehicle.Status.ToLowerInvariant()}", ct);
                    changes++;
                    break;
            }
        }
        return changes;
    }
}
