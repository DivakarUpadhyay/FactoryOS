using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Divakar.FactoryOS.Application.Production;
using Microsoft.Extensions.DependencyInjection;

namespace Divakar.FactoryOS.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class ProductionTests(FactoryOsApp app)
{
    /// <summary>A CNC that reports over HTTP, so the test decides exactly when it runs and stops.</summary>
    private async Task<(HttpClient Admin, HttpClient Device, Guid Cnc)> PushCnc()
    {
        var (_, _, admin) = await app.NewTenantAsync("pro");
        Guid? parent = null;
        foreach (var level in new[] { "Site", "Plant", "Area", "Line" })
        {
            var r = await admin.PostAsJsonAsync("/api/v1/plant/nodes", new { parentId = parent, level, code = $"p-{level.ToLowerInvariant()}", name = level });
            parent = (await r.Content.ReadFromJsonAsync<IdResponse>())!.NodeId;
        }
        var res = await admin.PostAsJsonAsync("/api/v1/plant/assets",
            new { nodeId = parent, assetTypeCode = "cnc", protocolCode = "http", code = "cnc-push", name = "Push CNC", idealCycleSec = 6 });
        var cnc = (await res.Content.ReadFromJsonAsync<IdResponse>())!.AssetId;
        foreach (var (code, sensor) in new[] { ("state", "machine_state"), ("parts", "part_count"), ("scrap", "scrap_count") })
        {
            (await admin.PostAsJsonAsync($"/api/v1/plant/assets/{cnc}/tags", new { code, name = code, sensorTypeCode = sensor })).EnsureSuccessStatusCode();
        }
        var key = (await (await admin.PostAsync($"/api/v1/plant/assets/{cnc}/device-keys", null)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("key").GetString()!;
        var device = app.CreateClient();
        device.DefaultRequestHeaders.Add("X-Device-Key", key);
        return (admin, device, cnc);
    }

    private static Task<HttpResponseMessage> Push(HttpClient device, DateTime ts, params (string Tag, double Value)[] values) =>
        device.PostAsJsonAsync("/api/v1/ingest", new { readings = values.Select(v => new { tag = v.Tag, value = v.Value, ts }) });

    private async Task Detect()
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IProductionJobsStore>().DetectDowntimeAsync(default);
    }

    [Fact]
    public async Task Stops_become_downtime_get_a_reason_and_count_in_oee_and_pareto()
    {
        var (admin, device, cnc) = await PushCnc();
        var now = DateTime.UtcNow;
        // like a real device: a reading every minute (silence over 3 minutes counts as "unknown")
        for (var i = 9; i >= 5; i--)
        {
            await Push(device, now.AddMinutes(-i), ("state", 2), ("parts", 100 + (9 - i) * 10), ("scrap", 0));
        }
        await Push(device, now.AddMinutes(-4), ("state", 3), ("parts", 140));
        await Detect();

        var open = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/production/downtimes?pending=true")).EnumerateArray().ToList();
        var dt = Assert.Single(open);
        Assert.Equal("Stopped", dt.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, dt.GetProperty("endTs").ValueKind);

        await Push(device, now.AddMinutes(-3), ("state", 3), ("parts", 140));
        await Push(device, now.AddMinutes(-2), ("state", 3), ("parts", 140));
        await Push(device, now.AddSeconds(-90), ("state", 2), ("parts", 140));
        await Push(device, now.AddSeconds(-5), ("state", 2), ("parts", 160));
        await Detect();
        var closed = Assert.Single((await admin.GetFromJsonAsync<JsonElement>("/api/v1/production/downtimes")).EnumerateArray());
        Assert.InRange(closed.GetProperty("seconds").GetDouble(), 140, 160);   // 2.5 minutes

        var id = closed.GetProperty("downtimeId").GetInt64();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/production/downtimes/{id}/reason", new { reasonCode = "other" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/production/downtimes/{id}/reason", new { reasonCode = "changeover", comment = "Job 42" })).StatusCode);

        var pareto = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/production/pareto")).EnumerateArray().ToList();
        Assert.Equal("changeover", pareto[0].GetProperty("reasonCode").GetString());

        var oee = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/production/oee?from={now.AddMinutes(-15):O}&to={now.AddMinutes(1):O}");
        var m = Assert.Single(oee.GetProperty("machines").EnumerateArray());
        Assert.Equal(60, m.GetProperty("good").GetDouble());               // 100 → 160
        Assert.InRange(m.GetProperty("runSec").GetDouble(), 370, 420);     // 5 min + 1.5 min
        Assert.InRange(m.GetProperty("downSec").GetDouble(), 140, 160);
        var a = m.GetProperty("oee").GetProperty("availability").GetDouble();
        Assert.InRange(a, 68, 76);
        Assert.Equal(100, m.GetProperty("oee").GetProperty("quality").GetDouble());
        Assert.Equal("Execute", m.GetProperty("packMlState").GetString());
        Assert.Equal(JsonValueKind.Number, oee.GetProperty("plant").GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task Work_orders_follow_the_machine_counter_and_only_one_runs_per_machine()
    {
        var (admin, device, cnc) = await PushCnc();
        await Push(device, DateTime.UtcNow.AddMinutes(-1), ("state", 2), ("parts", 500), ("scrap", 3));

        var p = await admin.PostAsJsonAsync("/api/v1/production/products", new { code = "BRKT-01", name = "Bracket", idealCycleSec = 6 });
        var productId = (await p.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("productId").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/v1/production/products", new { code = "BRKT-01", name = "Again" })).StatusCode);

        async Task<Guid> Order(string code) => (await (await admin.PostAsJsonAsync("/api/v1/production/work-orders",
            new { code, productId, assetId = cnc, quantity = 50 })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workOrderId").GetGuid();
        var wo1 = await Order("WO-1");
        var wo2 = await Order("WO-2");

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/production/work-orders/{wo1}/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/v1/production/work-orders/{wo2}/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/v1/production/work-orders/{wo2}/complete", null)).StatusCode);

        await Push(device, DateTime.UtcNow, ("parts", 520), ("scrap", 4));
        var running = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/production/work-orders?status=Running")).EnumerateArray().Single();
        Assert.Equal(20, running.GetProperty("produced").GetInt32());
        Assert.Equal(1, running.GetProperty("scrap").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/production/work-orders/{wo1}/complete", null)).StatusCode);
        var done = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/production/work-orders?status=Completed")).EnumerateArray().Single();
        Assert.Equal(20, done.GetProperty("produced").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/production/work-orders/{wo2}/start", null)).StatusCode);
    }

    [Fact]
    public async Task Shift_report_covers_each_configured_shift()
    {
        var (admin, _, _) = await PushCnc();
        var shifts = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/production/shifts")).EnumerateArray().ToList();
        Assert.Equal(["Day", "Night"], shifts.Select(s => s.GetProperty("shift").GetString()));
        Assert.Equal(14, (await admin.GetFromJsonAsync<JsonElement>("/api/v1/production/reasons")).GetArrayLength());
    }

    [Fact]
    public async Task Demo_factory_gives_oee_for_every_production_machine()
    {
        var (_, _, admin) = await app.NewTenantAsync("enterprise");
        await admin.PostAsync("/api/v1/plant/demo", null);
        await app.RunEngineAsync(passes: 8, secondsApart: 75);
        var oee = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/production/oee?from={DateTime.UtcNow.AddMinutes(-5):O}&to={DateTime.UtcNow.AddMinutes(15):O}");
        var machines = oee.GetProperty("machines").EnumerateArray().ToList();
        Assert.True(machines.Count >= 8);
        Assert.Contains(machines, m => m.GetProperty("oee").GetProperty("value").ValueKind == JsonValueKind.Number);
        Assert.True(oee.GetProperty("good").GetDouble() > 0);
        Assert.True(oee.GetProperty("hourlyOutput").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Plans_without_production_and_users_without_rights_are_refused()
    {
        var (_, _, energyOnly) = await app.NewTenantAsync("energy");
        Assert.Equal(HttpStatusCode.Forbidden, (await energyOnly.GetAsync("/api/v1/production/oee")).StatusCode);

        var (_, _, other) = await app.NewTenantAsync("pro");
        Assert.Empty((await other.GetFromJsonAsync<JsonElement>("/api/v1/production/downtimes")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync("/api/v1/production/downtimes/999999/reason", new { reasonCode = "break" })).StatusCode);
    }
}
