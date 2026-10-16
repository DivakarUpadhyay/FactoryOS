using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Divakar.FactoryOS.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class InspectionPlanTests(FactoryOsApp app)
{
    private static async Task<JsonElement> Get(HttpClient c, string url) => await c.GetFromJsonAsync<JsonElement>(url);

    [Fact]
    public async Task First_piece_inspection_is_due_records_results_and_opens_an_ncr_when_it_fails()
    {
        var (_, _, admin) = await app.NewTenantAsync("enterprise");
        (await admin.PostAsync("/api/v1/plant/demo", null)).EnsureSuccessStatusCode();
        var plans = (await Get(admin, "/api/v1/quality/inspection-plans")).EnumerateArray().ToList();
        Assert.Equal(3, plans.Count);
        var first = plans.Single(p => p.GetProperty("name").GetString() == "First-piece check");
        Assert.Equal(3, first.GetProperty("items").GetArrayLength());

        var bracket = first.GetProperty("productId").GetGuid();
        var cnc = (await Get(admin, "/api/v1/plant/floor")).EnumerateArray().Single(a => a.GetProperty("code").GetString() == "demo-cnc-04").GetProperty("assetId").GetGuid();
        var wo = (await (await admin.PostAsJsonAsync("/api/v1/production/work-orders", new { code = "wo-insp-1", productId = bracket, assetId = cnc, quantity = 100, lotNo = "LOT-INSP" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workOrderId").GetGuid();
        Assert.DoesNotContain((await Get(admin, "/api/v1/quality/inspections/due")).EnumerateArray(), d => d.GetProperty("workOrderId").GetGuid() == wo);
        (await admin.PostAsync($"/api/v1/production/work-orders/{wo}/start", null)).EnsureSuccessStatusCode();
        var due = (await Get(admin, "/api/v1/quality/inspections/due")).EnumerateArray().Where(d => d.GetProperty("workOrderId").GetGuid() == wo).ToList();
        var d1 = Assert.Single(due);
        Assert.Equal("FirstPiece", d1.GetProperty("trigger").GetString());

        var planId = first.GetProperty("planId").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/quality/inspections",
            new { planId, workOrderId = wo, results = new object[] { new { seq = 1, passed = true } } })).StatusCode);   // the hole must be measured

        var res = await (await admin.PostAsJsonAsync("/api/v1/quality/inspections", new
        {
            planId, workOrderId = wo, lotNo = "LOT-INSP",
            results = new object[] { new { seq = 1, value = 10.08 }, new { seq = 2, passed = true }, new { seq = 3, passed = true } },
        })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Fail", res.GetProperty("result").GetString());
        Assert.Equal("Hole diameter 10.00 ± 0.05 mm", res.GetProperty("failed")[0].GetString());
        var ncId = res.GetProperty("ncId").GetGuid();
        var ncr = (await Get(admin, "/api/v1/quality/ncr")).EnumerateArray().Single(n => n.GetProperty("ncId").GetGuid() == ncId);
        Assert.Contains("wo-insp-1", ncr.GetProperty("title").GetString());
        Assert.Equal("LOT-INSP", ncr.GetProperty("lotNo").GetString());

        Assert.DoesNotContain((await Get(admin, "/api/v1/quality/inspections/due")).EnumerateArray(), d => d.GetProperty("workOrderId").GetGuid() == wo);
        var history = (await Get(admin, "/api/v1/quality/inspections")).EnumerateArray().Single();
        Assert.Equal("Fail", history.GetProperty("result").GetString());
        Assert.Equal("wo-insp-1", history.GetProperty("workOrderCode").GetString());
    }

    [Fact]
    public async Task Plans_are_validated()
    {
        var (_, _, admin) = await app.NewTenantAsync("enterprise");
        await admin.PostAsync("/api/v1/plant/demo", null);
        var bracket = (await Get(admin, "/api/v1/production/products")).EnumerateArray().First().GetProperty("productId").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/quality/inspection-plans",
            new { productId = bracket, name = "x", trigger = "EveryN", createNcrOnFail = true, isActive = true, items = new[] { new { text = "a" } } })).StatusCode);   // N missing
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/quality/inspection-plans",
            new { productId = bracket, name = "x", trigger = "FirstPiece", createNcrOnFail = true, isActive = true, items = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/quality/inspection-plans",
            new { productId = bracket, name = "Visual", trigger = "EveryN", everyN = 25, createNcrOnFail = false, isActive = true, items = new[] { new { text = "Looks right" } } })).StatusCode);
    }
}
