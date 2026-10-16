using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Divakar.FactoryOS.Application.QualityControl;
using Microsoft.Extensions.DependencyInjection;

namespace Divakar.FactoryOS.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class QualityInventoryTests(FactoryOsApp app)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? demoEmail;

    /// <summary>One demo factory (with products, characteristics and stock) shared by these tests.</summary>
    private async Task<HttpClient> Demo()
    {
        await Gate.WaitAsync();
        try
        {
            if (demoEmail is null)
            {
                var (_, email, admin) = await app.NewTenantAsync("enterprise");
                (await admin.PostAsync("/api/v1/plant/demo", null)).EnsureSuccessStatusCode();
                demoEmail = email;
            }
        }
        finally
        {
            Gate.Release();
        }
        return await app.SignInAsync(demoEmail);
    }

    private static async Task<JsonElement> Get(HttpClient c, string url) => await c.GetFromJsonAsync<JsonElement>(url);

    [Fact]
    public async Task Demo_has_spc_charts_with_capability_and_stock_with_a_low_spare_part()
    {
        var admin = await Demo();
        var chars = (await Get(admin, "/api/v1/quality/characteristics")).EnumerateArray().ToList();
        Assert.Equal(3, chars.Count);
        var hole = chars.Single(c => c.GetProperty("code").GetString() == "brkt-hole");
        Assert.True(hole.GetProperty("measurements").GetInt32() >= 125);   // other tests in this class add a few

        var chart = await Get(admin, $"/api/v1/quality/characteristics/{hole.GetProperty("charId").GetGuid()}/spc");
        Assert.Equal("Xbar-R", chart.GetProperty("chartType").GetString());
        Assert.True(chart.GetProperty("points").GetArrayLength() >= 25);
        Assert.InRange(chart.GetProperty("limits").GetProperty("centre").GetDouble(), 9.99, 10.01);
        Assert.True(chart.GetProperty("capability").GetProperty("cpk").GetDouble() > 1);

        var items = (await Get(admin, "/api/v1/inventory/items")).EnumerateArray().ToList();
        Assert.Equal(7, items.Count);
        Assert.True(items.Single(i => i.GetProperty("code").GetString() == "brg-6205").GetProperty("low").GetBoolean());
        Assert.Equal(500, items.Single(i => i.GetProperty("code").GetString() == "abs-resin").GetProperty("onHand").GetDecimal());

        var scan = await Get(admin, "/api/v1/inventory/lookup?code=BRG-6205");
        Assert.Equal("item", scan.GetProperty("kind").GetString());
        var scanLot = await Get(admin, "/api/v1/inventory/lookup?code=DEMO-ABS-RESIN-01");
        Assert.Equal("lot", scanLot.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Measurements_must_match_the_subgroup_and_ncrs_need_a_capa_to_close()
    {
        var admin = await Demo();
        var hole = (await Get(admin, "/api/v1/quality/characteristics")).EnumerateArray().Single(c => c.GetProperty("code").GetString() == "brkt-hole")
            .GetProperty("charId").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/quality/measurements", new { charId = hole, values = new[] { 10.0, 10.01 } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync("/api/v1/quality/measurements",
            new { charId = hole, values = new[] { 10.0, 10.01, 9.99, 10.0, 10.02 }, lotNo = "LOT-X" })).StatusCode);

        var res = await admin.PostAsJsonAsync("/api/v1/quality/ncr", new { title = "Burr on hole edge", severity = "Major", lotNo = "LOT-X", quantity = 40 });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ncId").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/quality/ncr/{id}",
            new { title = "Burr on hole edge", severity = "Major", status = "Closed" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/quality/ncr/{id}", new
        {
            title = "Burr on hole edge", severity = "Major", status = "Closed", disposition = "Rework", rootCause = "Worn drill", correctiveAction = "Drill life limit set to 2,000 holes",
        })).StatusCode);
        var ncr = (await Get(admin, "/api/v1/quality/ncr")).EnumerateArray().Single(n => n.GetProperty("ncId").GetGuid() == id);
        Assert.Equal("Closed", ncr.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, ncr.GetProperty("closedAt").ValueKind);
    }

    [Fact]
    public async Task Lot_genealogy_links_raw_material_work_order_finished_lot_and_quality()
    {
        var admin = await Demo();
        var products = (await Get(admin, "/api/v1/production/products")).EnumerateArray().ToList();
        var bracket = products.Single(p => p.GetProperty("code").GetString() == "brkt-01").GetProperty("productId").GetGuid();
        var cnc = (await Get(admin, "/api/v1/plant/floor")).EnumerateArray().Single(a => a.GetProperty("code").GetString() == "demo-cnc-02").GetProperty("assetId").GetGuid();
        var wo = (await (await admin.PostAsJsonAsync("/api/v1/production/work-orders", new { code = "wo-trace-1", productId = bracket, assetId = cnc, quantity = 100 }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workOrderId").GetGuid();
        (await admin.PostAsync($"/api/v1/production/work-orders/{wo}/start", null)).EnsureSuccessStatusCode();

        var items = (await Get(admin, "/api/v1/inventory/items")).EnumerateArray().ToList();
        var steel = items.Single(i => i.GetProperty("code").GetString() == "steel-bar-40").GetProperty("itemId").GetGuid();
        var steelLot = (await Get(admin, $"/api/v1/inventory/lots?itemId={steel}")).EnumerateArray().Single().GetProperty("lotId").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/inventory/issue", new { itemId = steel, quantity = 999, lotId = steelLot, workOrderId = wo })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync("/api/v1/inventory/issue", new { itemId = steel, quantity = 12.5, lotId = steelLot, workOrderId = wo })).StatusCode);

        (await admin.PostAsync($"/api/v1/production/work-orders/{wo}/complete", null)).EnsureSuccessStatusCode();
        var produced = await admin.PostAsJsonAsync("/api/v1/inventory/produce", new { workOrderId = wo, quantity = 95, lotNo = "FG-TRACE-01" });
        Assert.Equal(HttpStatusCode.Created, produced.StatusCode);
        var hole = (await Get(admin, "/api/v1/quality/characteristics")).EnumerateArray().Single(c => c.GetProperty("code").GetString() == "brkt-hole").GetProperty("charId").GetGuid();
        await admin.PostAsJsonAsync("/api/v1/quality/measurements", new { charId = hole, values = new[] { 10.0, 10.01, 9.99, 10.0, 10.08 }, lotNo = "FG-TRACE-01" });

        var trace = await Get(admin, "/api/v1/trace?lot=FG-TRACE-01");
        Assert.Equal("Steel bracket (finished)", trace.GetProperty("lot").GetProperty("itemName").GetString());
        Assert.Equal(95, trace.GetProperty("lot").GetProperty("onHand").GetDecimal());
        Assert.Contains(trace.GetProperty("produced").EnumerateArray(), s => s.GetProperty("title").GetString() == "Work order wo-trace-1");
        Assert.Equal("DEMO-STEEL-BAR-40-01", trace.GetProperty("inputs")[0].GetProperty("lotNo").GetString());
        var q = trace.GetProperty("quality").EnumerateArray().Single();
        Assert.Contains("1 out of spec", q.GetProperty("detail").GetString());

        var back = await Get(admin, "/api/v1/trace?lot=DEMO-STEEL-BAR-40-01");
        var use = back.GetProperty("usedIn").EnumerateArray().Single();
        Assert.Contains("FG-TRACE-01", use.GetProperty("detail").GetString());
        Assert.Equal(107.5m, back.GetProperty("lot").GetProperty("onHand").GetDecimal());

        var (_, _, other) = await app.NewTenantAsync("enterprise");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/api/v1/trace?lot=FG-TRACE-01")).StatusCode);
    }

    [Fact]
    public async Task Sensor_fed_characteristic_raises_and_clears_a_quality_alarm()
    {
        var (_, _, admin) = await app.NewTenantAsync("enterprise");
        Guid? parent = null;
        foreach (var level in new[] { "Site", "Plant", "Area", "Line" })
        {
            var r = await admin.PostAsJsonAsync("/api/v1/plant/nodes", new { parentId = parent, level, code = $"q-{level.ToLowerInvariant()}", name = level });
            parent = (await r.Content.ReadFromJsonAsync<IdResponse>())!.NodeId;
        }
        var oven = (await (await admin.PostAsJsonAsync("/api/v1/plant/assets", new { nodeId = parent, assetTypeCode = "reflow", protocolCode = "http", code = "oven-q", name = "Oven Q" }))
            .Content.ReadFromJsonAsync<IdResponse>())!.AssetId;
        var tag = (await (await admin.PostAsJsonAsync($"/api/v1/plant/assets/{oven}/tags", new { code = "peak", name = "Peak", sensorTypeCode = "temperature" }))
            .Content.ReadFromJsonAsync<IdResponse>())!.TagId;
        var key = (await (await admin.PostAsync($"/api/v1/plant/assets/{oven}/device-keys", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;
        var device = app.CreateClient();
        device.DefaultRequestHeaders.Add("X-Device-Key", key);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/quality/characteristics",
            new { code = "peak", name = "Peak", assetId = oven, tagId = tag, lsl = 235, usl = 250, subgroupSize = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/quality/characteristics",
            new { code = "peak", name = "Reflow peak", unit = "°C", assetId = oven, tagId = tag, lsl = 235, usl = 250, subgroupSize = 1, sampleSeconds = 10 })).StatusCode);

        async Task Sample(double v, DateTime now)
        {
            await device.PostAsJsonAsync("/api/v1/ingest", new { readings = new[] { new { tag = "peak", value = v, ts = now } } });
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SpcSampler>().RunOnceAsync(now.AddSeconds(1), default);
        }
        var t = DateTime.UtcNow.AddMinutes(-2);
        await Sample(244, t);
        await Sample(256, t.AddSeconds(20));   // above the specification
        var alarm = Assert.Single((await Get(admin, "/api/v1/alarms?state=open")).EnumerateArray());
        Assert.Equal("Quality", alarm.GetProperty("kind").GetString());
        Assert.Equal("Critical", alarm.GetProperty("severity").GetString());
        await Sample(243, t.AddSeconds(40));
        Assert.Empty((await Get(admin, "/api/v1/alarms?state=open")).EnumerateArray());
        Assert.Equal(3, (await Get(admin, "/api/v1/quality/characteristics")).EnumerateArray().Single().GetProperty("measurements").GetInt32());
    }

    [Fact]
    public async Task Quality_and_inventory_need_their_modules()
    {
        var (_, _, energy) = await app.NewTenantAsync("energy");
        Assert.Equal(HttpStatusCode.Forbidden, (await energy.GetAsync("/api/v1/quality/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await energy.GetAsync("/api/v1/inventory/items")).StatusCode);
    }
}
