using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Divakar.FactoryOS.Application.Connectivity;
using Microsoft.Extensions.DependencyInjection;

namespace Divakar.FactoryOS.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class OtCoreIntegrationTests(FactoryOsApp app)
{
    private static string Day(int offset) => DateTime.UtcNow.AddHours(8).AddDays(offset).ToString("yyyy-MM-dd");

    private static async Task<Guid> Created(HttpResponseMessage res, string idName)
    {
        Assert.True(res.StatusCode == HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty(idName).GetGuid();
    }

    private async Task<(Guid TenantId, HttpClient Admin)> DemoFactory()
    {
        var (tenantId, _, admin) = await app.NewTenantAsync("enterprise");
        (await admin.PostAsync("/api/v1/plant/demo", null)).EnsureSuccessStatusCode();
        await app.RunEngineAsync(passes: 3, secondsApart: 60);
        return (tenantId, admin);
    }

    // ---------- ISO 50001 9.1: meters and calibration ----------

    [Fact]
    public async Task Connected_meters_are_registered_and_calibration_records_drive_clause_9_1()
    {
        var (_, admin) = await DemoFactory();
        var added = (await (await admin.PostAsync("/api/v1/enms/meters/register-connected", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("added").GetInt32();
        Assert.True(added > 0);
        Assert.Equal(0, (await (await admin.PostAsync("/api/v1/enms/meters/register-connected", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("added").GetInt32());

        var meters = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/enms/meters")).EnumerateArray().ToList();
        Assert.Equal(added, meters.Count);
        var energyMeter = meters.First(m => m.GetProperty("meter").GetProperty("intervalMonths").ValueKind == JsonValueKind.Number);
        Assert.Equal("never", energyMeter.GetProperty("state").GetString());
        var id = energyMeter.GetProperty("meter").GetProperty("meterId").GetGuid();

        var future = await admin.PostAsJsonAsync($"/api/v1/enms/meters/{id}/calibrations", new { calibratedOn = Day(10), result = "Pass" });
        Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);
        await Created(await admin.PostAsJsonAsync($"/api/v1/enms/meters/{id}/calibrations", new
        {
            calibratedOn = Day(-30), result = "Adjusted", asFoundErrorPct = 0.9, asLeftErrorPct = 0.1, lab = "TAF accredited lab", certificateNo = "CAL-2026-118",
        }), "calibrationId");
        var after = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/enms/meters")).EnumerateArray()
            .Single(m => m.GetProperty("meter").GetProperty("meterId").GetGuid() == id);
        Assert.Equal("ok", after.GetProperty("state").GetString());
        Assert.Single((await admin.GetFromJsonAsync<JsonElement>($"/api/v1/enms/meters/{id}/calibrations")).EnumerateArray());

        // other meters are still uncalibrated, so 9.1 is not shown as met
        var overview = await admin.GetFromJsonAsync<JsonElement>("/api/v1/enms/overview");
        var c91 = overview.GetProperty("clauses").EnumerateArray().Single(c => c.GetProperty("clause").GetString() == "9.1");
        Assert.NotEqual("ok", c91.GetProperty("state").GetString());

        var bad = await admin.PostAsJsonAsync("/api/v1/enms/meters", new { name = "x", measures = "electricity", kind = "Main", uncertaintyPct = 80 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    // ---------- M&V ----------

    [Fact]
    public async Task Option_C_savings_are_computed_with_ASHRAE_statistics_and_option_A_can_verify_an_action()
    {
        var (tenantId, admin) = await DemoFactory();
        var to = DateTime.UtcNow;
        await app.BackfillAsync(tenantId, to.AddDays(-16), to.AddHours(-1), 3 * 3600);

        var enpiId = await Created(await admin.PostAsJsonAsync("/api/v1/enms/enpis", new
        {
            name = "Site kWh vs parts", kind = "Regression", baselineFrom = Day(-15), baselineTo = Day(-6),
        }), "enpiId");

        // option B needs an SEU boundary; option C is the site
        var wrong = await admin.PostAsJsonAsync("/api/v1/enms/mv", new
        {
            name = "LED retrofit", enpiId, mvOption = "B", reportingFrom = Day(-5), reportingTo = Day(0), nonRoutineKwh = 0, confidencePct = 90,
        });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var planC = await Created(await admin.PostAsJsonAsync("/api/v1/enms/mv", new
        {
            name = "Whole site after compressor upgrade", enpiId, mvOption = "C", reportingFrom = Day(-5), reportingTo = Day(0), nonRoutineKwh = 0, confidencePct = 90,
        }), "planId");
        var result = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/enms/mv/{planC}/result");
        Assert.Contains("Option C", result.GetProperty("method").GetString());
        var fit = result.GetProperty("fit");
        Assert.True(fit.GetProperty("n").GetInt32() >= 7);
        Assert.True(fit.GetProperty("cvRmsePct").GetDouble() >= 0);
        Assert.Contains(result.GetProperty("checks").EnumerateArray(), c => c.GetProperty("name").GetString() == "Model CV(RMSE)");
        Assert.Contains(result.GetProperty("checks").EnumerateArray(), c => c.GetProperty("name").GetString() == "Meter accuracy");
        Assert.True(result.GetProperty("savings").GetProperty("reportingDays").GetInt32() > 0);

        // option A: the engineer's measured savings verify the action
        var objective = await Created(await admin.PostAsJsonAsync("/api/v1/enms/objectives", new { title = "−5 % site energy", enpiId, targetPct = 5, targetDate = Day(200) }), "objectiveId");
        var action = await Created(await admin.PostAsJsonAsync("/api/v1/enms/actions", new
        {
            objectiveId = objective, title = "VSD on compressor 2", expectedKwhYear = 40000, status = "Done",
        }), "actionId");
        var noSavings = await admin.PostAsJsonAsync("/api/v1/enms/mv", new
        {
            name = "VSD", actionId = action, mvOption = "A", reportingFrom = Day(-30), reportingTo = Day(0), nonRoutineKwh = 0, confidencePct = 90,
        });
        Assert.Equal(HttpStatusCode.BadRequest, noSavings.StatusCode);
        var planA = await Created(await admin.PostAsJsonAsync("/api/v1/enms/mv", new
        {
            name = "VSD on compressor 2 — key parameter", actionId = action, mvOption = "A", reportingFrom = Day(-30), reportingTo = Day(0), nonRoutineKwh = 0,
            confidencePct = 90, manualSavingsKwh = 3000, manualUncertaintyPct = 10,
        }), "planId");
        (await admin.PostAsync($"/api/v1/enms/mv/{planA}/apply", null)).EnsureSuccessStatusCode();
        var verified = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/enms/actions")).EnumerateArray().Single(a => a.GetProperty("actionId").GetGuid() == action);
        Assert.Equal("Verified", verified.GetProperty("status").GetString());
        Assert.Contains("Option A", verified.GetProperty("verification").GetString());
        Assert.InRange(verified.GetProperty("verifiedKwhYear").GetDouble(), 36000, 37500);   // 3,000 kWh in 30 days × 365
    }

    // ---------- GHG inventory ----------

    [Fact]
    public async Task Refrigerant_and_scope_3_activities_reach_the_carbon_report_and_the_grid_region_can_change()
    {
        var (_, admin) = await DemoFactory();
        var factors = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/enms/factors")).EnumerateArray().ToList();
        Guid Factor(string source) => factors.Single(f => f.GetProperty("source").GetString() == source).GetProperty("factorId").GetGuid();
        Assert.Contains(factors, f => f.GetProperty("source").GetString() == "electricity" && f.GetProperty("region").GetString() == "UK"
            && !f.GetProperty("isActive").GetBoolean());

        await Created(await admin.PostAsJsonAsync("/api/v1/enms/activities", new
        {
            factorId = Factor("ref_r410a"), periodFrom = Day(0), periodTo = Day(0), quantity = 2, reference = "Chiller service log 0417",
        }), "activityId");
        await Created(await admin.PostAsJsonAsync("/api/v1/enms/activities", new
        {
            factorId = Factor("s3_freight_hgv"), periodFrom = Day(0), periodTo = Day(0), quantity = 1000,
        }), "activityId");
        var energyAsActivity = await admin.PostAsJsonAsync("/api/v1/enms/activities", new { factorId = Factor("diesel"), periodFrom = Day(0), periodTo = Day(0), quantity = 5 });
        Assert.Equal(HttpStatusCode.BadRequest, energyAsActivity.StatusCode);

        var window = $"from={DateTime.UtcNow.AddDays(-2):O}&to={DateTime.UtcNow.AddDays(2):O}";
        var carbon = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/energy/carbon?{window}");
        Assert.Equal(2 * 1924, carbon.GetProperty("fugitiveKg").GetDouble(), 0);   // 2 kg of R-410A (GWP 1,924) topped up
        var s3 = Assert.Single(carbon.GetProperty("scope3").EnumerateArray());
        Assert.Equal(4, s3.GetProperty("category").GetInt32());
        Assert.Equal("TW", carbon.GetProperty("gridRegion").GetString());

        var unknown = await admin.PostAsJsonAsync("/api/v1/enms/factors/region", new { region = "XX" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        (await admin.PostAsJsonAsync("/api/v1/enms/factors/region", new { region = "uk" })).EnsureSuccessStatusCode();
        var uk = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/energy/carbon?{window}");
        Assert.Equal(0.177, uk.GetProperty("electricityFactor").GetDouble(), 3);
        Assert.Equal("UK", uk.GetProperty("gridRegion").GetString());

        var badCode = await admin.PostAsJsonAsync("/api/v1/enms/factors", new
        {
            source = "ref_R-22", name = "x", scope = 1, method = "Fugitive", region = "GLOBAL", year = 2025, unit = "kg", kgCo2ePerUnit = 1760, reference = "x", isActive = true,
        });
        Assert.Equal(HttpStatusCode.BadRequest, badCode.StatusCode);

        var footprint = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/energy/carbon/footprint?{window}");
        Assert.Contains("Cradle to gate", footprint.GetProperty("boundary").GetString());
        Assert.NotEmpty(footprint.GetProperty("notes").EnumerateArray());
    }

    // ---------- edge agent ----------

    [Fact]
    public async Task An_edge_agent_downloads_its_assets_uploads_buffered_readings_and_the_cloud_stops_polling_them()
    {
        var (tenantId, admin) = await DemoFactory();
        var floor = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/plant/floor")).EnumerateArray().ToList();
        var assetId = floor.First().GetProperty("assetId").GetGuid();
        var otherAsset = floor.Skip(1).First().GetProperty("assetId").GetGuid();

        var created = await (await admin.PostAsJsonAsync("/api/v1/edge/agents", new { name = "Line 1 gateway" })).Content.ReadFromJsonAsync<JsonElement>();
        var agentId = created.GetProperty("agentId").GetGuid();
        var key = created.GetProperty("key").GetString()!;
        Assert.StartsWith("fos_edge_", key);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/v1/edge/agents", new { name = "Line 1 gateway" })).StatusCode);
        (await admin.PutAsJsonAsync($"/api/v1/edge/agents/{agentId}/assets", new { assetIds = new[] { assetId } })).EnsureSuccessStatusCode();

        // a second agent cannot take the same asset
        var second = await (await admin.PostAsJsonAsync("/api/v1/edge/agents", new { name = "Line 2 gateway" })).Content.ReadFromJsonAsync<JsonElement>();
        var clash = await admin.PutAsJsonAsync($"/api/v1/edge/agents/{second.GetProperty("agentId").GetGuid()}/assets", new { assetIds = new[] { assetId, otherAsset } });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);

        var device = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/api/v1/edge/config")).StatusCode);
        device.DefaultRequestHeaders.Add("X-Edge-Key", key);
        var config = await device.GetFromJsonAsync<EdgeConfig>("/api/v1/edge/config");
        var binding = Assert.Single(config!.Assets);
        Assert.Equal(assetId, binding.AssetId);
        Assert.NotEmpty(binding.Tags);

        // the cloud worker no longer polls this asset
        using (var scope = app.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IIngestionStore>();
            var active = await store.ActiveBindingsAsync(["simulator"], default);
            Assert.DoesNotContain(active, b => b.AssetId == assetId);
            Assert.Contains(active, b => b.AssetId == otherAsset);
        }

        // readings buffered while offline (an hour old) and one for a tag the agent does not own
        var tag = binding.Tags[0];
        var foreign = Guid.NewGuid();
        var at = DateTime.UtcNow.AddHours(-1);
        var upload = await (await device.PostAsJsonAsync("/api/v1/edge/upload", new EdgeUpload(
            [new EdgeReading(tag.TagId, at, 12.5, 0, true), new EdgeReading(tag.TagId, at.AddMinutes(30), 13.5, 0, true), new EdgeReading(foreign, at, 1, 0, true)],
            [new AssetHealth(assetId, "Running", DateTime.UtcNow, null)]))).Content.ReadFromJsonAsync<EdgeUploadResult>();
        Assert.Equal(2, upload!.Accepted);
        Assert.Equal(1, upload.Rejected);
        // late readings go into the history; they do not overwrite a newer live value
        var history = (await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/telemetry/history?tagId={tag.TagId}&from={at.AddMinutes(-1):O}&to={at.AddMinutes(31):O}")).EnumerateArray().ToList();
        Assert.Contains(history, h => h.GetProperty("value").GetDouble() == 12.5);
        Assert.Contains(history, h => h.GetProperty("value").GetDouble() == 13.5);

        (await device.PostAsJsonAsync("/api/v1/edge/heartbeat", new EdgeHeartbeat("1.0.0", "GW-01", 42, 0, null))).EnsureSuccessStatusCode();
        var agent = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/edge/agents")).EnumerateArray().Single(a => a.GetProperty("agentId").GetGuid() == agentId);
        Assert.True(agent.GetProperty("online").GetBoolean());
        Assert.Equal(42, agent.GetProperty("buffered").GetInt64());

        // a local finding from the agent becomes an insight; operator feedback tunes the detector
        var added = await (await device.PostAsJsonAsync("/api/v1/edge/insights", new[]
        {
            new EdgeInsight(assetId, tag.TagId, "Spike", "Warning", "Spindle temperature: sudden jump (edge)", "z = 7.1", 7.1),
            new EdgeInsight(otherAsset, null, "Spike", "Warning", "not this agent's asset", null, 1),
        })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, added.GetProperty("added").GetInt32());
        var insights = await admin.GetFromJsonAsync<JsonElement>("/api/v1/ai/insights");
        var insight = Assert.Single(insights.GetProperty("insights").EnumerateArray(), i => i.GetProperty("source").GetString() == "edge:Line 1 gateway");
        var insightId = insight.GetProperty("insightId").GetInt64();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/ai/insights/{insightId}/feedback", new { verdict = "Maybe" })).StatusCode);
        (await admin.PostAsJsonAsync($"/api/v1/ai/insights/{insightId}/feedback", new { verdict = "False", note = "Door was open for cleaning" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/ai/insights/{insightId}/feedback", new { verdict = "Real" })).StatusCode);
        var tuned = await admin.GetFromJsonAsync<JsonElement>("/api/v1/ai/insights?all=true");
        var t = Assert.Single(tuned.GetProperty("tuning").EnumerateArray());
        Assert.Equal(1.25, t.GetProperty("sensitivity").GetDouble(), 6);
        Assert.Equal(1, tuned.GetProperty("falseAlarms").GetInt32());

        // the detectors run on demand without failing on a fresh factory
        (await admin.PostAsync("/api/v1/ai/run", null)).EnsureSuccessStatusCode();

        // a switched-off agent is refused; another company sees none of it
        (await admin.PutAsync($"/api/v1/edge/agents/{agentId}/active?active=false", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await device.GetAsync("/api/v1/edge/config")).StatusCode);
        var (_, _, other) = await app.NewTenantAsync("enterprise");
        Assert.Empty((await other.GetFromJsonAsync<JsonElement>("/api/v1/edge/agents")).EnumerateArray());
        Assert.Empty((await other.GetFromJsonAsync<JsonElement>("/api/v1/ai/insights?all=true")).GetProperty("insights").EnumerateArray());
        Assert.Empty((await other.GetFromJsonAsync<JsonElement>("/api/v1/enms/activities")).EnumerateArray());
        _ = tenantId;
    }
}
