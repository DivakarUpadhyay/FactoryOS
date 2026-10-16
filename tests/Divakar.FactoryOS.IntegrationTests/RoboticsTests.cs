using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Divakar.FactoryOS.Application.Robotics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Server;

namespace Divakar.FactoryOS.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class RoboticsTests(FactoryOsApp app)
{
    private async Task Dispatch()
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FleetDispatcher>().RunOnceAsync(DateTime.UtcNow, default);
    }

    private static async Task<Guid> Line(HttpClient admin, string prefix)
    {
        Guid? parent = null;
        foreach (var level in new[] { "Site", "Plant", "Area", "Line" })
        {
            var r = await admin.PostAsJsonAsync("/api/v1/plant/nodes", new { parentId = parent, level, code = $"{prefix}-{level.ToLowerInvariant()}", name = level });
            parent = (await r.Content.ReadFromJsonAsync<IdResponse>())!.NodeId;
        }
        return parent!.Value;
    }

    private static async Task<Guid> Station(HttpClient admin, string code, decimal x, decimal y) =>
        (await (await admin.PostAsJsonAsync("/api/v1/robotics/stations", new { code, name = code, x, y })).Content.ReadFromJsonAsync<JsonElement>())
        .GetProperty("stationId").GetGuid();

    [Fact]
    public async Task Demo_fleet_shows_robots_vehicles_and_stations_with_live_values()
    {
        var (_, _, admin) = await app.NewTenantAsync("enterprise");
        await admin.PostAsync("/api/v1/plant/demo", null);
        await app.RunEngineAsync(passes: 3);
        var fleet = await admin.GetFromJsonAsync<JsonElement>("/api/v1/robotics/fleet");
        var robots = fleet.GetProperty("robots").EnumerateArray().ToList();
        var vehicles = fleet.GetProperty("vehicles").EnumerateArray().ToList();
        Assert.True(robots.Count >= 2);
        Assert.True(vehicles.Count >= 2);
        Assert.All(vehicles, v =>
        {
            Assert.Equal(JsonValueKind.Number, v.GetProperty("battery").ValueKind);
            Assert.InRange(v.GetProperty("x").GetDouble(), 0, 200);
            Assert.InRange(v.GetProperty("y").GetDouble(), 0, 100);
        });
        Assert.Contains(robots, r => r.GetProperty("jointTemp").ValueKind == JsonValueKind.Number);
        Assert.Equal(5, fleet.GetProperty("stations").GetArrayLength());
    }

    [Fact]
    public async Task Mission_is_dispatched_to_the_nearest_vehicle_and_follows_its_position()
    {
        var (_, _, admin) = await app.NewTenantAsync("pro");
        var line = await Line(admin, "fl");
        var keys = new Dictionary<string, HttpClient>();
        foreach (var (code, x) in new[] { ("agv-near", 45.0), ("agv-far", 170.0) })
        {
            var id = (await (await admin.PostAsJsonAsync("/api/v1/plant/assets", new { nodeId = line, assetTypeCode = "agv", protocolCode = "http", code, name = code }))
                .Content.ReadFromJsonAsync<IdResponse>())!.AssetId;
            await admin.PostAsync($"/api/v1/plant/assets/{id}/tags/template", null);
            var key = (await (await admin.PostAsync($"/api/v1/plant/assets/{id}/device-keys", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;
            var device = app.CreateClient();
            device.DefaultRequestHeaders.Add("X-Device-Key", key);
            keys[code] = device;
            await Move(device, x, 20);
        }

        var from = await Station(admin, "pick-a", 40, 20);
        var to = await Station(admin, "drop-b", 120, 20);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/robotics/missions", new { fromStationId = from, toStationId = from })).StatusCode);
        var res = await admin.PostAsJsonAsync("/api/v1/robotics/missions", new { fromStationId = from, toStationId = to, payload = "pallet" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var missionId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("missionId").GetGuid();

        async Task<JsonElement> Mission() => (await admin.GetFromJsonAsync<JsonElement>("/api/v1/robotics/missions"))
            .EnumerateArray().Single(m => m.GetProperty("missionId").GetGuid() == missionId);

        await Dispatch();
        var m = await Mission();
        Assert.Equal("Assigned", m.GetProperty("status").GetString());
        Assert.Equal("agv-near", m.GetProperty("vehicleName").GetString());

        await Move(keys["agv-near"], 41, 20);    // at the pick-up
        await Dispatch();
        Assert.Equal("PickedUp", (await Mission()).GetProperty("status").GetString());

        await Move(keys["agv-near"], 118, 21);   // at the drop-off
        await Dispatch();
        m = await Mission();
        Assert.Equal("Done", m.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, m.GetProperty("finishedAt").ValueKind);

        var fleet = await admin.GetFromJsonAsync<JsonElement>("/api/v1/robotics/fleet");
        Assert.Empty(fleet.GetProperty("activeMissions").EnumerateArray());
    }

    private static Task Move(HttpClient device, double x, double y) =>
        device.PostAsJsonAsync("/api/v1/ingest", new { readings = new object[]
        {
            new { tag = "state", value = 2 }, new { tag = "battery", value = 80 }, new { tag = "x", value = x }, new { tag = "y", value = y },
        } });

    [Fact]
    public async Task Real_vehicles_receive_a_vda5050_order_over_mqtt()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using var broker = new MqttServerFactory().CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(port).Build());
        var received = new TaskCompletionSource<(string Topic, string Payload)>(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.InterceptingPublishAsync += e =>
        {
            if (e.ApplicationMessage.Topic.EndsWith("/order"))
            {
                received.TrySetResult((e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString()));
            }
            return Task.CompletedTask;
        };
        await broker.StartAsync();
        try
        {
            var (tenantId, _, admin) = await app.NewTenantAsync("pro");
            var line = await Line(admin, "vda");
            var res = await admin.PostAsJsonAsync("/api/v1/plant/assets", new
            {
                nodeId = line, assetTypeCode = "amr", protocolCode = "vda5050", code = "amr-7", name = "AMR 7", vendor = "Hiwin", serialNo = "AMR-007",
                endpoint = $"127.0.0.1:{port}",
            });
            var amr = (await res.Content.ReadFromJsonAsync<IdResponse>())!.AssetId;
            await admin.PostAsync($"/api/v1/plant/assets/{amr}/tags/template", null);
            await SetLatest(tenantId, amr, "battery_soc", 90);

            var from = await Station(admin, "dock-1", 10, 10);
            var to = await Station(admin, "dock-2", 60, 10);
            await admin.PostAsJsonAsync("/api/v1/robotics/missions", new { fromStationId = from, toStationId = to, vehicleAssetId = amr });
            await Dispatch();

            var (topic, payload) = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("uagv/v2/Hiwin/AMR-007/order", topic);
            var order = JsonDocument.Parse(payload).RootElement;
            Assert.Equal("AMR-007", order.GetProperty("serialNumber").GetString());
            Assert.Equal(["dock-1", "dock-2"], order.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("nodeId").GetString()));
            var mission = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/robotics/missions?active=true")).EnumerateArray().Single();
            Assert.Equal("Assigned", mission.GetProperty("status").GetString());
            Assert.Equal(order.GetProperty("orderId").GetString(), mission.GetProperty("orderId").GetString());
        }
        finally
        {
            await broker.StopAsync();
        }
    }

    /// <summary>Stands in for the vehicle's state topic: writes a latest value directly.</summary>
    private static async Task SetLatest(Guid tenantId, Guid assetId, string sensor, double value)
    {
        await using var con = new SqlConnection(FactoryOsApp.ConnectionString);
        await con.OpenAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = """
            EXEC sp_set_session_context N'System', 1;
            INSERT telemetry.Latest (TagId, TenantId, Ts, Value, Quality)
            SELECT TagId, @tenantId, SYSUTCDATETIME(), @value, 0 FROM plant.Tags WHERE AssetId = @assetId AND SensorTypeCode = @sensor;
            """;
        cmd.Parameters.AddWithValue("@tenantId", tenantId);
        cmd.Parameters.AddWithValue("@assetId", assetId);
        cmd.Parameters.AddWithValue("@sensor", sensor);
        cmd.Parameters.AddWithValue("@value", value);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Robotics_needs_the_module_and_missions_stay_inside_the_company()
    {
        var (_, _, energy) = await app.NewTenantAsync("energy");
        Assert.Equal(HttpStatusCode.Forbidden, (await energy.GetAsync("/api/v1/robotics/fleet")).StatusCode);
        var (_, _, a) = await app.NewTenantAsync("pro");
        var (_, _, b) = await app.NewTenantAsync("pro");
        var s1 = await Station(a, "a1", 1, 1);
        var s2 = await Station(a, "a2", 2, 2);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsJsonAsync("/api/v1/robotics/missions", new { fromStationId = s1, toStationId = s2 })).StatusCode);
    }
}
