using System.Text.Json;
using Divakar.FactoryOS.Application.Robotics;
using Divakar.FactoryOS.Connectors.Drivers;

namespace Divakar.FactoryOS.UnitTests;

public sealed class FleetTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static VehicleState V(string name, double battery, double x, double y, bool busy = false, string status = "Running") =>
        new(Tenant, Guid.NewGuid(), name, "Hiwin", "SN-" + name, "simulator", null, null, status, battery, x, y, busy);

    private static OpenMission M() => new(Tenant, Guid.NewGuid(), "m-1", "Queued", null, 2, DateTime.UtcNow, null,
        Guid.NewGuid(), "a", 40, 20, Guid.NewGuid(), "b", 120, 20);

    [Fact]
    public void Dispatcher_picks_the_nearest_free_vehicle_with_enough_battery()
    {
        var near = V("near", 80, 45, 20);
        var far = V("far", 90, 180, 80);
        var flat = V("flat", 10, 40, 20);         // closest but nearly empty
        var busy = V("busy", 90, 41, 20, busy: true);
        var broken = V("broken", 90, 40, 21, status: "Fault");
        Assert.Equal(near, FleetDispatcher.Choose(M(), [far, flat, busy, broken, near]));
        Assert.Null(FleetDispatcher.Choose(M(), [flat, busy, broken]));
        Assert.Null(FleetDispatcher.Choose(M(), [near with { TenantId = Guid.NewGuid() }]));   // never another company's vehicle
    }

    [Theory]
    [InlineData("Running", 60.0, 50.0, 80.0, 0.0, "ok")]
    [InlineData("Running", 80.0, 50.0, 80.0, 0.0, "warn")]
    [InlineData("Running", 90.0, 50.0, 80.0, 0.0, "bad")]
    [InlineData("Running", 60.0, 97.0, 80.0, 0.0, "bad")]
    [InlineData("Running", null, null, 12.0, null, "bad")]
    [InlineData("Fault", 50.0, 40.0, null, 205.0, "bad")]
    public void Robot_health_flags_heat_torque_battery_and_faults(string status, double? temp, double? torque, double? battery, double? alarm, string expected) =>
        Assert.Equal(expected, RoboticsService.Assess(status, temp, torque, battery, alarm).Health);

    [Fact]
    public void Vda5050_order_has_two_released_nodes_one_edge_and_pick_and_drop_actions()
    {
        var order = Vda5050.BuildOrder("Hiwin", "AGV-07", "m-1-120000", 5, new DateTime(2026, 10, 16, 4, 0, 0, DateTimeKind.Utc), "factory",
            ("wh-in", 40, 20), ("smt-feed", 120, 20), "pallet");
        var json = JsonDocument.Parse(Vda5050.Serialize(order)).RootElement;

        Assert.Equal("2.0.0", json.GetProperty("version").GetString());
        Assert.Equal("2026-10-16T04:00:00.000Z", json.GetProperty("timestamp").GetString());
        Assert.Equal("m-1-120000", json.GetProperty("orderId").GetString());
        var nodes = json.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal([0, 2], nodes.Select(n => n.GetProperty("sequenceId").GetInt32()));
        Assert.All(nodes, n => Assert.True(n.GetProperty("released").GetBoolean()));
        Assert.Equal(120, nodes[1].GetProperty("nodePosition").GetProperty("x").GetDouble());
        Assert.Equal("factory", nodes[1].GetProperty("nodePosition").GetProperty("mapId").GetString());
        Assert.Equal("pick", nodes[0].GetProperty("actions")[0].GetProperty("actionType").GetString());
        Assert.Equal("drop", nodes[1].GetProperty("actions")[0].GetProperty("actionType").GetString());
        var edge = json.GetProperty("edges")[0];
        Assert.Equal(1, edge.GetProperty("sequenceId").GetInt32());
        Assert.Equal("wh-in", edge.GetProperty("startNodeId").GetString());
        Assert.Equal("uagv/v2/Hiwin/AGV-07/order", Vda5050.Topic("Hiwin", "AGV-07"));
    }
}
