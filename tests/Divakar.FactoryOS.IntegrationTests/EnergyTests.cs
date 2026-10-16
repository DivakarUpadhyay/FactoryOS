using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Divakar.FactoryOS.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class EnergyTests(FactoryOsApp app)
{
    private static string Window => $"from={DateTime.UtcNow.AddHours(-1):O}&to={DateTime.UtcNow.AddHours(2):O}";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? sharedEmail;

    /// <summary>
    /// One demo factory with 40 minutes of simulated readings, shared by the tests in this class (building history is the
    /// slow part). Each test signs in again, so it gets a fresh token.
    /// </summary>
    private async Task<HttpClient> FactoryWithHistory()
    {
        await Gate.WaitAsync();
        try
        {
            if (sharedEmail is null)
            {
                var (_, email, admin) = await app.NewTenantAsync("enterprise");
                (await admin.PostAsync("/api/v1/plant/demo", null)).EnsureSuccessStatusCode();
                await app.RunEngineAsync(passes: 20, secondsApart: 120);
                sharedEmail = email;
            }
        }
        finally
        {
            Gate.Release();
        }
        return await app.SignInAsync(sharedEmail);
    }

    [Fact]
    public async Task Energy_summary_gives_consumption_cost_profile_and_per_machine_figures()
    {
        var admin = await FactoryWithHistory();
        var s = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/energy/summary?{Window}");

        Assert.True(s.GetProperty("gridKwh").GetDouble() > 0);
        Assert.True(s.GetProperty("cost").GetDecimal() > 0);
        Assert.Equal("TWD", s.GetProperty("currency").GetString());
        Assert.Contains("Main incomer energy meter", s.GetProperty("mainMeters").EnumerateArray().Select(m => m.GetString()));
        Assert.True(s.GetProperty("hourly").GetArrayLength() >= 1);
        Assert.True(s.GetProperty("peakKw").GetDouble() > 0);

        var machines = s.GetProperty("machines").EnumerateArray().ToList();
        Assert.True(machines.Count >= 10);
        var cnc = machines.First(m => m.GetProperty("code").GetString() == "demo-cnc-01");
        Assert.True(cnc.GetProperty("kwh").GetDouble() > 0);
        Assert.Equal(JsonValueKind.Number, cnc.GetProperty("kwhPerPart").ValueKind);
        Assert.Equal(100, machines.Sum(m => m.GetProperty("sharePct").GetDouble()), 0);
    }

    [Fact]
    public async Task Contract_capacity_and_tariff_come_from_the_settings()
    {
        var admin = await FactoryWithHistory();
        var settings = await admin.GetFromJsonAsync<JsonElement>("/api/v1/settings");
        Assert.Equal("Asia/Taipei", settings.GetProperty("timeZone").GetString());
        Assert.Equal(2, settings.GetProperty("shifts").GetArrayLength());

        var before = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/energy/summary?{Window}");
        var body = JsonSerializer.Deserialize<Dictionary<string, object?>>(settings.GetRawText())!;
        body["contractKw"] = 1;            // impossible to stay under
        body["peakRate"] = 10.0;
        body["offPeakRate"] = 10.0;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/v1/settings", body)).StatusCode);

        var after = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/energy/summary?{Window}");
        Assert.True(after.GetProperty("contractUsePct").GetDouble() > 100);
        Assert.Contains(after.GetProperty("insights").EnumerateArray(), i => i.GetString()!.Contains("exceeded the contract"));
        Assert.Equal(10m, after.GetProperty("avgRate").GetDecimal());
        Assert.NotEqual(before.GetProperty("cost").GetDecimal(), after.GetProperty("cost").GetDecimal());

        body["timeZone"] = "Mars/Olympus";
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/settings", body)).StatusCode);

        // put the shared factory back the way the other tests expect it
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/v1/settings", JsonSerializer.Deserialize<object>(settings.GetRawText()))).StatusCode);
    }

    [Fact]
    public async Task Power_quality_grades_every_analyser_signal()
    {
        var admin = await FactoryWithHistory();
        var pq = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/energy/power-quality")).EnumerateArray().ToList();
        var analyser = pq.First(p => p.GetProperty("assetTypeCode").GetString() == "power-analyzer");
        var sensors = analyser.GetProperty("signals").EnumerateArray().Select(s => s.GetProperty("sensorTypeCode").GetString()).ToList();
        Assert.Contains("power_factor", sensors);
        Assert.Contains("thd_voltage", sensors);
        Assert.All(analyser.GetProperty("signals").EnumerateArray(),
            s => Assert.Contains(s.GetProperty("state").GetString(), new[] { "ok", "warn", "bad", "muted" }));
    }

    [Fact]
    public async Task Carbon_report_splits_scope_1_and_2_and_uses_the_emission_factor()
    {
        var admin = await FactoryWithHistory();
        var c = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/energy/carbon?{Window}");
        var grid = c.GetProperty("gridKwh").GetDouble();
        Assert.True(grid > 0);
        Assert.InRange(c.GetProperty("scope2Kg").GetDouble(), grid * 0.474 - 1, grid * 0.474 + 1);   // grid is rounded to 0.1 kWh
        Assert.True(c.GetProperty("scope1Kg").GetDouble() >= 0);
        Assert.True(c.GetProperty("daily").GetArrayLength() >= 1);
        Assert.True(c.GetProperty("byArea").GetArrayLength() >= 2);
    }

    [Fact]
    public async Task Modules_outside_the_plan_are_refused_by_the_server()
    {
        var (_, _, trial) = await app.NewTenantAsync("trial");   // trial: plant, energy, production
        Assert.Equal(HttpStatusCode.OK, (await trial.GetAsync("/api/v1/energy/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trial.GetAsync("/api/v1/energy/power-quality")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trial.GetAsync("/api/v1/energy/carbon")).StatusCode);
    }

    [Fact]
    public async Task Another_company_sees_no_energy_of_this_one()
    {
        await FactoryWithHistory();
        var (_, _, other) = await app.NewTenantAsync("enterprise");   // a company with no assets
        var s = await other.GetFromJsonAsync<JsonElement>($"/api/v1/energy/summary?{Window}");
        Assert.Equal(0, s.GetProperty("gridKwh").GetDouble());
        Assert.Empty(s.GetProperty("machines").EnumerateArray());
    }
}
