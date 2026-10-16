using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Divakar.FactoryOS.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class PlanningTests(FactoryOsApp app)
{
    private static async Task<JsonElement> Get(HttpClient c, string url) => await c.GetFromJsonAsync<JsonElement>(url);

    [Fact]
    public async Task Demo_products_have_a_bom_and_routing_that_new_orders_copy_and_follow()
    {
        var (_, _, admin) = await app.NewTenantAsync("enterprise");
        (await admin.PostAsync("/api/v1/plant/demo", null)).EnsureSuccessStatusCode();
        var products = (await Get(admin, "/api/v1/production/products")).EnumerateArray().ToList();
        var bracket = products.Single(p => p.GetProperty("code").GetString() == "brkt-01").GetProperty("productId").GetGuid();

        var bom = (await Get(admin, $"/api/v1/production/products/{bracket}/bom")).EnumerateArray().Single();
        Assert.Equal("steel-bar-40", bom.GetProperty("itemCode").GetString());
        var routing = (await Get(admin, $"/api/v1/production/products/{bracket}/routing")).EnumerateArray().ToList();
        Assert.Equal(["Saw bar to length", "CNC machining", "Deburr and inspect"], routing.Select(r => r.GetProperty("name").GetString()));

        var cnc = (await Get(admin, "/api/v1/plant/floor")).EnumerateArray().Single(a => a.GetProperty("code").GetString() == "demo-cnc-03").GetProperty("assetId").GetGuid();
        var wo = (await (await admin.PostAsJsonAsync("/api/v1/production/work-orders", new { code = "wo-bom-1", productId = bracket, assetId = cnc, quantity = 400 }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workOrderId").GetGuid();

        // 400 × 0.25 m × 1.02 = 102 m needed; 120 m in stock
        var mat = await Get(admin, $"/api/v1/production/work-orders/{wo}/materials");
        var line = mat.GetProperty("lines").EnumerateArray().Single();
        Assert.Equal(102m, line.GetProperty("required").GetDecimal());
        Assert.True(mat.GetProperty("ready").GetBoolean());

        var issued = await (await admin.PostAsync($"/api/v1/production/work-orders/{wo}/issue-materials", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(102m, issued.GetProperty("issued").EnumerateArray().Sum(i => i.GetProperty("quantity").GetDecimal()));
        Assert.Empty(issued.GetProperty("stillShort").EnumerateArray());
        var after = (await Get(admin, $"/api/v1/production/work-orders/{wo}/materials")).GetProperty("lines")[0];
        Assert.Equal(0m, after.GetProperty("remaining").GetDecimal());
        Assert.Equal(18m, after.GetProperty("onHand").GetDecimal());

        // operations follow the routing, in order, on a running order
        var steps = (await Get(admin, $"/api/v1/production/work-orders/{wo}/steps")).EnumerateArray().ToList();
        Assert.Equal([10, 20, 30], steps.Select(s => s.GetProperty("seq").GetInt32()));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/v1/production/work-orders/{wo}/steps/10/start", null)).StatusCode);   // order not started
        (await admin.PostAsync($"/api/v1/production/work-orders/{wo}/start", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/v1/production/work-orders/{wo}/steps/20/start", null)).StatusCode);   // out of order
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/production/work-orders/{wo}/steps/10/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/v1/production/work-orders/{wo}/steps/10/complete", new { goodQty = 398, scrapQty = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/production/work-orders/{wo}/steps/20/start", null)).StatusCode);
        var now = (await Get(admin, $"/api/v1/production/work-orders/{wo}/steps")).EnumerateArray().ToList();
        Assert.Equal(["Done", "Running", "Pending"], now.Select(s => s.GetProperty("status").GetString()));
        Assert.Equal(398, now[0].GetProperty("goodQty").GetInt32());
    }

    [Fact]
    public async Task Shortages_are_reported_and_bom_input_is_checked()
    {
        var (_, _, admin) = await app.NewTenantAsync("enterprise");
        await admin.PostAsync("/api/v1/plant/demo", null);
        var products = (await Get(admin, "/api/v1/production/products")).EnumerateArray().ToList();
        var cap = products.Single(p => p.GetProperty("code").GetString() == "cap-01").GetProperty("productId").GetGuid();
        var items = (await Get(admin, "/api/v1/inventory/items")).EnumerateArray().ToList();
        var resin = items.Single(i => i.GetProperty("code").GetString() == "abs-resin").GetProperty("itemId").GetGuid();

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/production/products/{cap}/bom", new[] { new { itemId = resin, qtyPer = 0 } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/production/products/{cap}/bom",
            new[] { new { itemId = resin, qtyPer = 1 }, new { itemId = resin, qtyPer = 2 } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"/api/v1/production/products/{cap}/bom", new[] { new { itemId = Guid.NewGuid(), qtyPer = 1 } })).StatusCode);

        // 40 000 caps × 0.018 kg × 1.03 = 741.6 kg needed, 500 kg in stock
        var inj = (await Get(admin, "/api/v1/plant/floor")).EnumerateArray().Single(a => a.GetProperty("code").GetString() == "demo-inj-02").GetProperty("assetId").GetGuid();
        var wo = (await (await admin.PostAsJsonAsync("/api/v1/production/work-orders", new { code = "wo-cap-1", productId = cap, assetId = inj, quantity = 40000 }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workOrderId").GetGuid();
        var mat = await Get(admin, $"/api/v1/production/work-orders/{wo}/materials");
        Assert.False(mat.GetProperty("ready").GetBoolean());
        Assert.Equal(241.6m, mat.GetProperty("lines")[0].GetProperty("short").GetDecimal());
        var issued = await (await admin.PostAsync($"/api/v1/production/work-orders/{wo}/issue-materials", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(500m, issued.GetProperty("issued")[0].GetProperty("quantity").GetDecimal());
        Assert.Equal(241.6m, issued.GetProperty("stillShort")[0].GetProperty("remaining").GetDecimal());
    }
}
