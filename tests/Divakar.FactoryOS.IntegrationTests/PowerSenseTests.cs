using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Divakar.FactoryOS.Application.Connectivity;
using Microsoft.Extensions.DependencyInjection;

namespace Divakar.FactoryOS.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class PowerSenseTests(FactoryOsApp app)
{
    [Fact]
    public async Task A_machine_with_only_a_power_meter_gets_state_cycles_and_oee()
    {
        var (_, _, admin) = await app.NewTenantAsync("pro");
        Guid? parent = null;
        foreach (var level in new[] { "Site", "Plant", "Area", "Line" })
        {
            var r = await admin.PostAsJsonAsync("/api/v1/plant/nodes", new { parentId = parent, level, code = $"ps-{level.ToLowerInvariant()}", name = level });
            parent = (await r.Content.ReadFromJsonAsync<IdResponse>())!.NodeId;
        }
        var lathe = (await (await admin.PostAsJsonAsync("/api/v1/plant/assets",
            new { nodeId = parent, assetTypeCode = "cnc", protocolCode = "http", code = "old-lathe", name = "Old lathe 1987", ratedPowerKw = 15, idealCycleSec = 20 }))
            .Content.ReadFromJsonAsync<IdResponse>())!.AssetId;
        var power = (await (await admin.PostAsJsonAsync($"/api/v1/plant/assets/{lathe}/tags", new { code = "kw", name = "Power", sensorTypeCode = "active_power" }))
            .Content.ReadFromJsonAsync<IdResponse>())!.TagId;
        var key = (await (await admin.PostAsync($"/api/v1/plant/assets/{lathe}/device-keys", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;
        var device = app.CreateClient();
        device.DefaultRequestHeaders.Add("X-Device-Key", key);

        var info = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plant/assets/{lathe}/power-sense");
        Assert.Equal(power, info.GetProperty("sources")[0].GetProperty("tagId").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/plant/assets/{lathe}/power-sense",
            new { enabled = true, sourceTagId = power, offBelow = 10, runAbove = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/plant/assets/{lathe}/power-sense",
            new { enabled = true, sourceTagId = power, offBelow = 1, runAbove = 10, countCycles = true, minCycleSec = 15, autoLearn = false })).StatusCode);

        // twelve bursts of cutting, 25 s apart, idling in between
        var t0 = DateTime.UtcNow.AddMinutes(-4);
        var readings = new List<object>();
        for (var i = 0; i < 12; i++)
        {
            readings.Add(new { tag = "kw", value = 4.0, ts = t0.AddSeconds(i * 20) });
            readings.Add(new { tag = "kw", value = 13.5, ts = t0.AddSeconds(i * 20 + 8) });
        }
        await device.PostAsJsonAsync("/api/v1/ingest", new { readings });
        using (var scope = app.Services.CreateScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<PowerSenseJob>();
            await job.RunOnceAsync(DateTime.UtcNow, default);
            await job.RunOnceAsync(DateTime.UtcNow, default);   // nothing new: no double counting
        }

        var live = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plant/assets/{lathe}/live");
        var values = live.GetProperty("values").EnumerateArray().ToDictionary(v => v.GetProperty("code").GetString()!, v => v.GetProperty("value").GetDouble());
        Assert.Equal(2, values["ps_state"]);    // last reading was cutting: running
        Assert.Equal(12, values["ps_cycles"]);
        Assert.Equal("Running", live.GetProperty("status").GetString());

        var oee = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/production/oee?from={t0.AddMinutes(-1):O}&to={DateTime.UtcNow.AddMinutes(1):O}");
        var m = oee.GetProperty("machines").EnumerateArray().Single();
        Assert.Equal(12, m.GetProperty("good").GetDouble());   // the counter went from 0 to 12 inside the window
        Assert.True(m.GetProperty("runSec").GetDouble() > 0);
    }
}
