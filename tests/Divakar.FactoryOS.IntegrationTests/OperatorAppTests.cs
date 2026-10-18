using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Divakar.FactoryOS.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class OperatorAppTests(FactoryOsApp app)
{
    [Fact]
    public async Task Hand_counts_from_the_floor_feed_oee_and_the_operator_home()
    {
        var (_, _, admin) = await app.NewTenantAsync("pro");
        Guid? parent = null;
        foreach (var level in new[] { "Site", "Plant", "Area", "Line" })
        {
            var r = await admin.PostAsJsonAsync("/api/v1/plant/nodes", new { parentId = parent, level, code = $"op-{level.ToLowerInvariant()}", name = level });
            parent = (await r.Content.ReadFromJsonAsync<IdResponse>())!.NodeId;
        }
        var press = (await (await admin.PostAsJsonAsync("/api/v1/plant/assets",
            new { nodeId = parent, assetTypeCode = "press", protocolCode = "http", code = "hand-press", name = "Hand press", idealCycleSec = 30 }))
            .Content.ReadFromJsonAsync<IdResponse>())!.AssetId;
        var t0 = DateTime.UtcNow.AddMinutes(-1);

        var home = await admin.GetFromJsonAsync<JsonElement>("/api/v1/production/operator");
        var m = home.GetProperty("machines").EnumerateArray().Single(x => x.GetProperty("assetId").GetGuid() == press);
        Assert.False(m.GetProperty("countsParts").GetBoolean());
        Assert.Contains("Burr", home.GetProperty("scrapReasons").EnumerateArray().Select(x => x.GetString()));

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/production/operator/counts", new { assetId = press, good = 0, scrap = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/production/operator/counts", new { assetId = press, good = 0, scrap = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/production/operator/counts", new { assetId = press, good = 40, scrap = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/production/operator/counts",
            new { assetId = press, good = 8, scrap = 2, scrapReason = "Burr", note = "deburr tool worn" })).StatusCode);

        var oee = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/production/oee?from={t0:O}&to={DateTime.UtcNow.AddMinutes(1):O}");
        var row = oee.GetProperty("machines").EnumerateArray().Single(x => x.GetProperty("assetId").GetGuid() == press);
        Assert.Equal(48, row.GetProperty("good").GetDouble());   // the first entry counts too, not only the step between entries
        Assert.Equal(2, row.GetProperty("scrap").GetDouble());

        home = await admin.GetFromJsonAsync<JsonElement>("/api/v1/production/operator");
        m = home.GetProperty("machines").EnumerateArray().Single(x => x.GetProperty("assetId").GetGuid() == press);
        Assert.Equal(48, m.GetProperty("goodToday").GetDouble());
        Assert.Equal(2, m.GetProperty("scrapToday").GetDouble());
        Assert.False(m.GetProperty("countsParts").GetBoolean());   // the hand-count tags do not make it a counting machine
        Assert.Equal("Burr", home.GetProperty("scrapReasons")[0].GetString());

        var entries = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/production/operator/entries?assetId={press}")).EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal("deburr tool worn", entries[0].GetProperty("note").GetString());

        var (_, _, other) = await app.NewTenantAsync("pro");
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync("/api/v1/production/operator/counts", new { assetId = press, good = 1, scrap = 0 })).StatusCode);
    }
}
