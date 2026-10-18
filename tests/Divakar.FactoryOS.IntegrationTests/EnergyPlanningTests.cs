using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Divakar.FactoryOS.Application.Energy;
using FluentModbus;
using Microsoft.Extensions.DependencyInjection;

namespace Divakar.FactoryOS.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class EnergyPlanningTests(FactoryOsApp app)
{
    private static async Task<JsonElement> Get(HttpClient c, string url) => await c.GetFromJsonAsync<JsonElement>(url);

    [Fact]
    public async Task Bill_forecast_and_cost_per_shift_come_from_the_meter()
    {
        var (_, _, admin) = await app.NewTenantAsync("enterprise");
        await admin.PostAsync("/api/v1/plant/demo", null);
        await app.RunEngineAsync(passes: 4, secondsApart: 60);
        var settings = JsonSerializer.Deserialize<Dictionary<string, object?>>((await Get(admin, "/api/v1/settings")).GetRawText())!;
        settings["contractKw"] = 100;
        settings["demandChargePerKw"] = 200;
        (await admin.PutAsJsonAsync("/api/v1/settings", settings)).EnsureSuccessStatusCode();

        var f = await Get(admin, "/api/v1/energy/forecast");
        Assert.Equal(20000m, f.GetProperty("basicCharge").GetDecimal());            // 100 kW × 200
        Assert.True(f.GetProperty("peakKw").GetDouble() > 100);                   // the demo plant draws far more than 100 kW
        Assert.True(f.GetProperty("excessPenalty").GetDecimal() > 0);
        Assert.True(f.GetProperty("forecastTotal").GetDecimal() >= f.GetProperty("energyCostToDate").GetDecimal() + 20000m);
        Assert.Equal(f.GetProperty("daysInMonth").GetInt32() - f.GetProperty("daysElapsed").GetInt32(),
            f.GetProperty("days").EnumerateArray().Count(d => d.GetProperty("forecast").GetBoolean()));

        var shifts = (await Get(admin, "/api/v1/energy/shifts")).EnumerateArray().ToList();
        Assert.Equal(["Day", "Night"], shifts.Select(s => s.GetProperty("shift").GetString()));
    }

    [Fact]
    public async Task Load_shedding_switches_a_real_modbus_load_off_and_back_on()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using var server = new ModbusTcpServer();
        server.AddUnit(1);
        server.Start(new IPEndPoint(IPAddress.Loopback, port));
        server.GetCoils(1)[0] = 1;   // the compressor is running

        var (_, _, admin) = await app.NewTenantAsync("pro");
        Guid? parent = null;
        Guid plant = Guid.Empty;
        foreach (var level in new[] { "Site", "Plant", "Area", "Line" })
        {
            var r = await admin.PostAsJsonAsync("/api/v1/plant/nodes", new { parentId = parent, level, code = $"ls-{level.ToLowerInvariant()}", name = level });
            parent = (await r.Content.ReadFromJsonAsync<IdResponse>())!.NodeId;
            if (level == "Plant") plant = parent.Value;
        }
        var meter = (await (await admin.PostAsJsonAsync("/api/v1/plant/assets", new { nodeId = plant, assetTypeCode = "energy-meter", protocolCode = "http", code = "main", name = "Main meter" }))
            .Content.ReadFromJsonAsync<IdResponse>())!.AssetId;
        await admin.PostAsJsonAsync($"/api/v1/plant/assets/{meter}/tags", new { code = "demand", name = "Demand", sensorTypeCode = "demand_kw" });
        var key = (await (await admin.PostAsync($"/api/v1/plant/assets/{meter}/device-keys", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;
        var device = app.CreateClient();
        device.DefaultRequestHeaders.Add("X-Device-Key", key);

        var comp = (await (await admin.PostAsJsonAsync("/api/v1/plant/assets", new { nodeId = parent, assetTypeCode = "air-compressor", protocolCode = "modbus-tcp", code = "ac-9", name = "Compressor 9", endpoint = $"127.0.0.1:{port}" }))
            .Content.ReadFromJsonAsync<IdResponse>())!.AssetId;
        var runTag = (await (await admin.PostAsJsonAsync($"/api/v1/plant/assets/{comp}/tags", new { code = "run", name = "Run command", sensorTypeCode = "door_open", address = "coil:0" }))
            .Content.ReadFromJsonAsync<IdResponse>())!.TagId;

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/energy/load-shed/rules",
            new { name = "x", assetId = comp, priority = 1, shedAboveKw = 500, restoreBelowKw = 600, action = "Notify" })).StatusCode);   // no gap
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/energy/load-shed/rules", new
        {
            name = "Stop compressor 9 at peak", assetId = comp, priority = 1, shedAboveKw = 500, restoreBelowKw = 300, action = "Write", writeTagId = runTag, shedValue = 0, restoreValue = 1,
        })).StatusCode);

        async Task Run(DateTime now)
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<LoadShedEngine>().RunOnceAsync(now, default);
        }
        await device.PostAsJsonAsync("/api/v1/ingest", new { readings = new[] { new { tag = "demand", value = 650.0 } } });
        await Run(DateTime.UtcNow);
        Assert.Equal(0, server.GetCoils(1)[0] & 1);   // switched off
        var shed = (await Get(admin, "/api/v1/energy/load-shed/rules")).EnumerateArray().Single();
        Assert.True(shed.GetProperty("shedNow").GetBoolean());
        var ev = (await Get(admin, "/api/v1/energy/load-shed/events")).EnumerateArray().Single();
        Assert.Contains("Wrote 0 to run", ev.GetProperty("result").GetString());

        var later = DateTime.UtcNow.AddMinutes(2.5);
        await device.PostAsJsonAsync("/api/v1/ingest", new { readings = new[] { new { tag = "demand", value = 250.0, ts = later.AddSeconds(-5) } } });
        await Run(DateTime.UtcNow);                       // too soon after shedding: stays off
        Assert.Equal(0, server.GetCoils(1)[0] & 1);
        await Run(later);                                 // 2.5 minutes later, with a fresh low reading
        Assert.Equal(1, server.GetCoils(1)[0] & 1);       // back on
        ev = (await Get(admin, "/api/v1/energy/load-shed/events")).EnumerateArray().Single();
        Assert.Equal(JsonValueKind.String, ev.GetProperty("restoredAt").ValueKind);
    }
}
