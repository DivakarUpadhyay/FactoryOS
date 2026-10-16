using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Divakar.FactoryOS.Application.Connectivity;
using FluentModbus;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Server;

namespace Divakar.FactoryOS.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class ConnectivityTests(FactoryOsApp app)
{
    private static async Task<Guid> Line(HttpClient c, string prefix)
    {
        Guid? parent = null;
        foreach (var level in new[] { "Site", "Plant", "Area", "Line" })
        {
            var res = await c.PostAsJsonAsync("/api/v1/plant/nodes", new { parentId = parent, level, code = $"{prefix}-{level.ToLowerInvariant()}", name = level });
            parent = (await res.Content.ReadFromJsonAsync<IdResponse>())!.NodeId;
        }
        return parent!.Value;
    }

    private static async Task<Guid> NewAsset(HttpClient c, Guid line, string code, string type, string? protocol, string? endpoint = null)
    {
        var res = await c.PostAsJsonAsync("/api/v1/plant/assets", new { nodeId = line, assetTypeCode = type, protocolCode = protocol, code, name = code, endpoint });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<IdResponse>())!.AssetId;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Runs the real ingestion engine against the test database for a few passes.</summary>
    private async Task RunEngine(int passes)
    {
        using var scope = app.Services.CreateScope();
        var engine = new IngestionEngine(scope.ServiceProvider.GetRequiredService<IIngestionStore>(),
            scope.ServiceProvider.GetRequiredService<ConnectorRegistry>(), new IngestionOptions(), NullLogger<IngestionEngine>.Instance);
        var t = DateTime.UtcNow;
        for (var i = 0; i < passes; i++)
        {
            await engine.RunOnceAsync(t.AddSeconds(i * 6), default);
        }
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Template_gives_a_robot_arm_its_standard_tags()
    {
        var (_, _, admin) = await app.NewTenantAsync();
        var asset = await NewAsset(admin, await Line(admin, "tpl"), "robot-1", "robot-arm", "simulator");
        var res = await admin.PostAsync($"/api/v1/plant/assets/{asset}/tags/template", null);
        Assert.Equal(9, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("added").GetInt32());
        // applying again adds nothing
        res = await admin.PostAsync($"/api/v1/plant/assets/{asset}/tags/template", null);
        Assert.Equal(0, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("added").GetInt32());
        var tags = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plant/assets/{asset}/tags");
        Assert.Contains(tags.EnumerateArray(), t => t.GetProperty("sensorTypeCode").GetString() == "robot_joint_temp");
    }

    [Fact]
    public async Task Tag_rules_are_enforced()
    {
        var (_, _, admin) = await app.NewTenantAsync();
        var asset = await NewAsset(admin, await Line(admin, "tr"), "m-1", "cnc", "opcua", "opc.tcp://localhost:4840");
        var ok = await admin.PostAsJsonAsync($"/api/v1/plant/assets/{asset}/tags", new { code = "temp", name = "Temp", sensorTypeCode = "temperature", address = "ns=2;s=T" });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var dup = await admin.PostAsJsonAsync($"/api/v1/plant/assets/{asset}/tags", new { code = "temp", name = "Again", sensorTypeCode = "temperature" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        var bad = await admin.PostAsJsonAsync($"/api/v1/plant/assets/{asset}/tags", new { code = "x", name = "X", sensorTypeCode = "nonsense" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var tags = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plant/assets/{asset}/tags");
        Assert.Equal("°C", tags[0].GetProperty("unit").GetString());   // unit defaults from the sensor type

        // an operator may look but not configure
        var email = $"o{Guid.NewGuid():N}"[..10] + "@x.test";
        await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = "Op", password = FactoryOsApp.Password, roles = new[] { "operator" } });
        var op = await app.SignInAsync(email);
        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync($"/api/v1/plant/assets/{asset}/tags")).StatusCode);
        var denied = await op.PostAsJsonAsync($"/api/v1/plant/assets/{asset}/tags", new { code = "y", name = "Y", sensorTypeCode = "temperature" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Demo_factory_is_built_once_and_respects_the_plan()
    {
        var (_, _, admin) = await app.NewTenantAsync();
        var res = await admin.PostAsync("/api/v1/plant/demo", null);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var demo = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(demo.GetProperty("assets").GetInt32() >= 40);
        Assert.True(demo.GetProperty("tags").GetInt32() >= 200);
        Assert.Equal(0, demo.GetProperty("skippedForPlanLimit").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/v1/plant/demo", null)).StatusCode);

        var (_, _, trial) = await app.NewTenantAsync(plan: "trial");   // trial allows 25 assets
        var small = await (await trial.PostAsync("/api/v1/plant/demo", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(25, small.GetProperty("assets").GetInt32());
        Assert.True(small.GetProperty("skippedForPlanLimit").GetInt32() > 0);
    }

    [Fact]
    public async Task Simulated_factory_flows_through_ingestion_to_live_values_and_history()
    {
        var (_, _, admin) = await app.NewTenantAsync();
        await admin.PostAsync("/api/v1/plant/demo", null);
        await RunEngine(passes: 3);

        var connections = await admin.GetFromJsonAsync<JsonElement>("/api/v1/connectivity");
        var rows = connections.EnumerateArray().ToList();
        Assert.All(rows, r => Assert.NotEqual("Offline", r.GetProperty("status").GetString()));
        Assert.All(rows, r => Assert.Equal("Native", r.GetProperty("supportLevel").GetString()));

        var cnc = rows.First(r => r.GetProperty("code").GetString() == "demo-cnc-01").GetProperty("assetId").GetGuid();
        var live = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plant/assets/{cnc}/live");
        var values = live.GetProperty("values").EnumerateArray().ToList();
        Assert.Equal(9, values.Count);
        Assert.All(values, v => Assert.Equal(JsonValueKind.Number, v.GetProperty("value").ValueKind));
        var energy = values.First(v => v.GetProperty("code").GetString() == "energy");
        Assert.True(energy.GetProperty("value").GetDouble() > 1000);

        var history = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/telemetry/history?tagId={energy.GetProperty("tagId").GetGuid()}&from={DateTime.UtcNow.AddHours(-1):O}&to={DateTime.UtcNow.AddMinutes(5):O}");
        Assert.True(history.GetArrayLength() >= 1);

        // another tenant sees none of it
        var (_, _, other) = await app.NewTenantAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/plant/assets/{cnc}/live")).StatusCode);
        var leak = await other.GetFromJsonAsync<JsonElement>($"/api/v1/telemetry/history?tagId={energy.GetProperty("tagId").GetGuid()}");
        Assert.Equal(0, leak.GetArrayLength());
    }

    [Fact]
    public async Task Devices_push_readings_over_http_with_their_key()
    {
        var (_, _, admin) = await app.NewTenantAsync();
        var asset = await NewAsset(admin, await Line(admin, "push"), "lora-1", "sensor", "http");
        await admin.PostAsJsonAsync($"/api/v1/plant/assets/{asset}/tags", new { code = "temp", name = "Temperature", sensorTypeCode = "temperature", scale = 0.1 });
        var key = (await (await admin.PostAsync($"/api/v1/plant/assets/{asset}/device-keys", null)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("key").GetString()!;

        var device = app.CreateClient();
        device.DefaultRequestHeaders.Add("X-Device-Key", key);
        var res = await device.PostAsJsonAsync("/api/v1/ingest", new { readings = new object[] { new { tag = "temp", value = 235 }, new { tag = "nope", value = 1 } } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("accepted").GetInt32());
        Assert.Equal("nope", body.GetProperty("unknownTags")[0].GetString());

        var live = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plant/assets/{asset}/live");
        Assert.Equal(23.5, live.GetProperty("values")[0].GetProperty("value").GetDouble(), 3);   // scaled by 0.1
        Assert.Equal("Running", live.GetProperty("status").GetString());

        var stranger = app.CreateClient();
        stranger.DefaultRequestHeaders.Add("X-Device-Key", "fos_wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.PostAsJsonAsync("/api/v1/ingest", new { readings = new[] { new { tag = "temp", value = 1 } } })).StatusCode);

        // a new key revokes the old one
        await admin.PostAsync($"/api/v1/plant/assets/{asset}/device-keys", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.PostAsJsonAsync("/api/v1/ingest", new { readings = new[] { new { tag = "temp", value = 1 } } })).StatusCode);
    }

    [Fact]
    public async Task Modbus_driver_reads_a_real_modbus_tcp_server()
    {
        var port = FreePort();
        using var server = new ModbusTcpServer();
        server.AddUnit(1);
        server.Start(new IPEndPoint(IPAddress.Loopback, port));
        var regs = server.GetHoldingRegisters(1);
        regs.SetBigEndian<float>(0, 231.4f);   // voltage
        regs.SetBigEndian<short>(2, 1234);     // raw current, tenths
        server.GetCoils(1)[0] = 1;              // first coil on

        var (_, _, admin) = await app.NewTenantAsync();
        var asset = await NewAsset(admin, await Line(admin, "mb"), "meter-1", "energy-meter", "modbus-tcp", $"127.0.0.1:{port}");
        await admin.PostAsJsonAsync($"/api/v1/plant/assets/{asset}/tags", new { code = "v", name = "Voltage", sensorTypeCode = "voltage_ln", address = "hr:40001:float32" });
        await admin.PostAsJsonAsync($"/api/v1/plant/assets/{asset}/tags", new { code = "i", name = "Current", sensorTypeCode = "current", address = "hr:2:int16", scale = 0.1 });
        await admin.PostAsJsonAsync($"/api/v1/plant/assets/{asset}/tags", new { code = "run", name = "Running", sensorTypeCode = "door_open", address = "coil:0" });

        var test = await (await admin.PostAsync($"/api/v1/plant/assets/{asset}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(test.GetProperty("connected").GetBoolean(), test.ToString());
        var byCode = test.GetProperty("values").EnumerateArray().ToDictionary(v => v.GetProperty("code").GetString()!, v => v.GetProperty("value").GetDouble());
        Assert.Equal(231.4, byCode["v"], 2);
        Assert.Equal(123.4, byCode["i"], 2);
        Assert.Equal(1, byCode["run"]);
    }

    [Fact]
    public async Task Mqtt_driver_reads_json_and_plain_payloads_from_a_real_broker()
    {
        var port = FreePort();
        var options = new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(port).Build();
        using var broker = new MqttServerFactory().CreateMqttServer(options);
        await broker.StartAsync();
        try
        {
            var (_, _, admin) = await app.NewTenantAsync();
            var asset = await NewAsset(admin, await Line(admin, "mq"), "env-1", "sensor", "mqtt", $"127.0.0.1:{port}");
            await admin.PostAsJsonAsync($"/api/v1/plant/assets/{asset}/tags", new { code = "t", name = "Temp", sensorTypeCode = "temperature", address = "plant/+/env|$.temp.c" });
            await admin.PostAsJsonAsync($"/api/v1/plant/assets/{asset}/tags", new { code = "h", name = "Humidity", sensorTypeCode = "humidity", address = "plant/hall1/humidity" });

            async Task Publish(string topic, string payload) => await broker.InjectApplicationMessage(
                new InjectedMqttApplicationMessage(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).WithRetainFlag().Build())
                { SenderClientId = "test" });
            await Publish("plant/hall1/env", """{"temp":{"c":27.25}}""");
            await Publish("plant/hall1/humidity", "61.5");

            var test = await (await admin.PostAsync($"/api/v1/plant/assets/{asset}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(test.GetProperty("connected").GetBoolean(), test.ToString());
            var byCode = test.GetProperty("values").EnumerateArray().ToDictionary(v => v.GetProperty("code").GetString()!, v => v.GetProperty("value").GetDouble());
            Assert.Equal(27.25, byCode["t"], 3);
            Assert.Equal(61.5, byCode["h"], 3);
        }
        finally
        {
            await broker.StopAsync();
        }
    }

    [Fact]
    public async Task Unreachable_devices_are_reported_not_hidden()
    {
        var (_, _, admin) = await app.NewTenantAsync();
        var asset = await NewAsset(admin, await Line(admin, "un"), "plc-1", "plc", "modbus-tcp", $"127.0.0.1:{FreePort()}");
        await admin.PostAsJsonAsync($"/api/v1/plant/assets/{asset}/tags", new { code = "s", name = "State", sensorTypeCode = "machine_state", address = "hr:0" });
        var test = await (await admin.PostAsync($"/api/v1/plant/assets/{asset}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(test.GetProperty("connected").GetBoolean());
        Assert.False(string.IsNullOrEmpty(test.GetProperty("error").GetString()));

        var gateway = await NewAsset(admin, await Line(admin, "gw"), "bac-1", "hvac", "bacnet");
        await admin.PostAsync($"/api/v1/plant/assets/{gateway}/tags/template", null);
        var gw = await (await admin.PostAsync($"/api/v1/plant/assets/{gateway}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("gateway", gw.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Connection_secrets_are_masked_and_kept()
    {
        var (tenantId, _, admin) = await app.NewTenantAsync();
        var asset = await NewAsset(admin, await Line(admin, "sec"), "opc-1", "cnc", "opcua", "opc.tcp://10.0.0.1:4840");
        var put = await admin.PutAsJsonAsync($"/api/v1/plant/assets/{asset}/connection",
            new { pollMs = 2000, connectorSettings = """{"username":"svc","password":"Top-Secret-1"}""" });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var got = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plant/assets/{asset}/connection");
        var shown = got.GetProperty("connectorSettings").GetString()!;
        Assert.DoesNotContain("Top-Secret-1", shown);
        Assert.Equal(2000, got.GetProperty("pollMs").GetInt32());

        // saving the masked form back keeps the real password
        await admin.PutAsJsonAsync($"/api/v1/plant/assets/{asset}/connection", new { pollMs = 1000, connectorSettings = shown });
        await using var con = new SqlConnection(FactoryOsApp.ConnectionString);
        await con.OpenAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "EXEC sp_set_session_context N'TenantId', @t; SELECT ConnectorSettings FROM plant.Assets WHERE AssetId = @a";
        cmd.Parameters.AddWithValue("@t", tenantId);
        cmd.Parameters.AddWithValue("@a", asset);
        Assert.Contains("Top-Secret-1", (string)(await cmd.ExecuteScalarAsync())!);

        var bad = await admin.PutAsJsonAsync($"/api/v1/plant/assets/{asset}/connection", new { pollMs = 10 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
