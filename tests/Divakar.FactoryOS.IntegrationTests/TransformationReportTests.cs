using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Divakar.FactoryOS.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class TransformationReportTests(FactoryOsApp app)
{
    [Fact]
    public async Task Baseline_and_current_periods_are_compared_kpi_by_kpi()
    {
        var (_, _, admin) = await app.NewTenantAsync("enterprise");
        Guid? parent = null;
        foreach (var level in new[] { "Site", "Plant", "Area", "Line" })
        {
            var r = await admin.PostAsJsonAsync("/api/v1/plant/nodes", new { parentId = parent, level, code = $"tr-{level.ToLowerInvariant()}", name = level });
            parent = (await r.Content.ReadFromJsonAsync<IdResponse>())!.NodeId;
        }
        var press = (await (await admin.PostAsJsonAsync("/api/v1/plant/assets",
            new { nodeId = parent, assetTypeCode = "press", protocolCode = "http", code = "p1", name = "Press 1", idealCycleSec = 10 }))
            .Content.ReadFromJsonAsync<IdResponse>())!.AssetId;
        (await admin.PostAsJsonAsync($"/api/v1/plant/assets/{press}/tags", new { code = "cnt", name = "Strokes", sensorTypeCode = "part_count" })).EnsureSuccessStatusCode();
        var key = (await (await admin.PostAsync($"/api/v1/plant/assets/{press}/device-keys", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;
        var device = app.CreateClient();
        device.DefaultRequestHeaders.Add("X-Device-Key", key);

        // baseline hour: 100 strokes; current hour: 160 strokes
        var now = DateTime.UtcNow;
        var b0 = now.AddHours(-2);
        var c0 = now.AddHours(-1);
        await device.PostAsJsonAsync("/api/v1/ingest", new
        {
            readings = new object[]
            {
                new { tag = "cnt", value = 0.0, ts = b0.AddMinutes(1) }, new { tag = "cnt", value = 100.0, ts = b0.AddMinutes(59) },
                new { tag = "cnt", value = 100.0, ts = c0.AddMinutes(1) }, new { tag = "cnt", value = 260.0, ts = c0.AddMinutes(59) },
            },
        });

        var q = $"from={c0:O}&to={now:O}";
        var report = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/reports/transformation?{q}");
        Assert.Equal("Digital transformation: baseline vs. current", report.GetProperty("title").GetString());
        var rows = report.GetProperty("rows").EnumerateArray().Select(r => r.EnumerateArray().Select(c => c.GetString()!).ToArray()).ToList();
        var good = rows.Single(r => r[1] == "Good parts");
        Assert.Equal(["Production", "Good parts", "pcs", "100", "160", "+60.0%", "context"], good);
        Assert.Contains(rows, r => r[0] == "Energy");
        Assert.Contains(rows, r => r[0] == "Maintenance" && r[1].StartsWith("MTTR"));
        Assert.Contains(report.GetProperty("notes").EnumerateArray(), n => n.GetString()!.StartsWith("Baseline period:"));

        // explicit baseline; it may not overlap the current period
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/reports/transformation?{q}&baseFrom={b0:O}&baseTo={c0:O}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/v1/reports/transformation?{q}&baseFrom={b0:O}&baseTo={now:O}")).StatusCode);

        var csv = await admin.GetAsync($"/api/v1/reports/transformation?{q}&format=csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Good parts,pcs,100,160,+60.0%,context", await csv.Content.ReadAsStringAsync());
    }
}
