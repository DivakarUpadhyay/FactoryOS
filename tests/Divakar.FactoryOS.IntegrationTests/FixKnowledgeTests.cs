using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Divakar.FactoryOS.Application.Alarms;
using Microsoft.Extensions.DependencyInjection;

namespace Divakar.FactoryOS.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class FixKnowledgeTests(FactoryOsApp app)
{
    private async Task RunAlarms()
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AlarmEngine>().RunOnceAsync(DateTime.UtcNow, default);
    }

    [Fact]
    public async Task A_recorded_fix_is_suggested_when_the_same_alarm_comes_back()
    {
        var (_, _, admin) = await app.NewTenantAsync("pro");
        Guid? parent = null;
        foreach (var level in new[] { "Site", "Plant", "Area", "Line" })
        {
            var r = await admin.PostAsJsonAsync("/api/v1/plant/nodes", new { parentId = parent, level, code = $"fk-{level.ToLowerInvariant()}", name = level });
            parent = (await r.Content.ReadFromJsonAsync<IdResponse>())!.NodeId;
        }
        var oven = (await (await admin.PostAsJsonAsync("/api/v1/plant/assets", new { nodeId = parent, assetTypeCode = "sensor", protocolCode = "http", code = "oven", name = "Reflow oven" }))
            .Content.ReadFromJsonAsync<IdResponse>())!.AssetId;
        (await admin.PostAsJsonAsync($"/api/v1/plant/assets/{oven}/tags", new { code = "temp", name = "Zone 3", sensorTypeCode = "temperature" })).EnsureSuccessStatusCode();
        var key = (await (await admin.PostAsync($"/api/v1/plant/assets/{oven}/device-keys", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;
        var device = app.CreateClient();
        device.DefaultRequestHeaders.Add("X-Device-Key", key);
        Task Push(double v) => device.PostAsJsonAsync("/api/v1/ingest", new { readings = new[] { new { tag = "temp", value = v, ts = DateTime.UtcNow } } });
        (await admin.PostAsJsonAsync("/api/v1/alarms/rules", new
        {
            name = "Zone 3 overheating", sensorTypeCode = "temperature", assetId = oven, @operator = ">", threshold = 250, durationSec = 0, severity = "Critical",
            notify = false, createWorkOrder = true,
        })).EnsureSuccessStatusCode();

        await Push(270);
        await RunAlarms();
        var first = Assert.Single((await admin.GetFromJsonAsync<JsonElement>("/api/v1/maintenance/work-orders?status=open")).EnumerateArray());
        var firstId = first.GetProperty("maintWoId").GetGuid();
        Assert.Equal("Corrective", first.GetProperty("type").GetString());
        Assert.Empty((await admin.GetFromJsonAsync<JsonElement>($"/api/v1/maintenance/work-orders/{firstId}/suggestions")).EnumerateArray());

        // a breakdown cannot be closed without saying what was wrong and what fixed it
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/v1/maintenance/work-orders/{firstId}/complete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/maintenance/work-orders/{firstId}/resolution", new { cause = "", fix = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/maintenance/work-orders/{firstId}/resolution",
            new { failureCode = "tc-drift", cause = "Zone 3 thermocouple drifted and read 20 C low", fix = "Replaced the thermocouple (part TC-K-300) and re-ran the profile" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/maintenance/work-orders/{firstId}/complete", null)).StatusCode);
        var res = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/maintenance/work-orders/{firstId}/resolution");
        Assert.Equal("TC-DRIFT", res.GetProperty("failureCode").GetString());

        // it clears, then overheats again weeks later
        await Push(230);
        await RunAlarms();
        await Push(268);
        await RunAlarms();
        var alarm = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/alarms?state=open")).EnumerateArray().Single();
        var second = Assert.Single((await admin.GetFromJsonAsync<JsonElement>("/api/v1/maintenance/work-orders?status=open")).EnumerateArray());
        var suggestions = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/maintenance/work-orders/{second.GetProperty("maintWoId").GetGuid()}/suggestions"))
            .EnumerateArray().ToList();
        var top = Assert.Single(suggestions);
        Assert.Equal(firstId, top.GetProperty("fix").GetProperty("maintWoId").GetGuid());
        Assert.Contains("same alarm rule", top.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        Assert.Contains("thermocouple", top.GetProperty("fix").GetProperty("fix").GetString());
        var forAlarm = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/maintenance/alarms/{alarm.GetProperty("alarmId").GetInt64()}/suggestions");
        Assert.Equal(firstId, forAlarm[0].GetProperty("fix").GetProperty("maintWoId").GetGuid());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/maintenance/knowledge/{firstId}/helpful", null)).StatusCode);
        var search = await admin.GetFromJsonAsync<JsonElement>("/api/v1/maintenance/knowledge?q=thermo");
        Assert.Equal(1, search.GetProperty("fixes")[0].GetProperty("helpful").GetInt32());
        Assert.Equal("TC-DRIFT", search.GetProperty("failureCodes")[0].GetString());
        Assert.Empty((await admin.GetFromJsonAsync<JsonElement>("/api/v1/maintenance/knowledge?q=hydraulic")).GetProperty("fixes").EnumerateArray());

        // another tenant sees none of it
        var (_, _, other) = await app.NewTenantAsync("pro");
        Assert.Empty((await other.GetFromJsonAsync<JsonElement>("/api/v1/maintenance/knowledge")).GetProperty("fixes").EnumerateArray());
    }
}
