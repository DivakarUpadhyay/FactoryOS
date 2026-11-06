using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Divakar.FactoryOS.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class EnmsTests(FactoryOsApp app)
{
    private static string Today => DateTime.UtcNow.AddHours(8).ToString("yyyy-MM-dd");

    private async Task<HttpClient> FactoryWithHistory()
    {
        var (_, _, admin) = await app.NewTenantAsync("enterprise");
        (await admin.PostAsync("/api/v1/plant/demo", null)).EnsureSuccessStatusCode();
        await app.RunEngineAsync(passes: 6, secondsApart: 120);
        return admin;
    }

    private static async Task<Guid> Created(HttpResponseMessage res, string idName)
    {
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty(idName).GetGuid();
    }

    [Fact]
    public async Task A_new_company_gets_the_published_Taiwan_emission_factors()
    {
        var (_, _, admin) = await app.NewTenantAsync("enterprise");
        var factors = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/enms/factors")).EnumerateArray().ToList();
        var grid2024 = factors.Single(f => f.GetProperty("source").GetString() == "electricity" && f.GetProperty("year").GetInt32() == 2024);
        Assert.Equal(0.474m, grid2024.GetProperty("co2e").GetDecimal());
        Assert.Contains("MOEA", grid2024.GetProperty("reference").GetString());
        var diesel = factors.Single(f => f.GetProperty("source").GetString() == "diesel");
        Assert.InRange(diesel.GetProperty("co2e").GetDouble(), 2.685, 2.687);

        // a supplier-specific factor can be added; a duplicate of an existing year is refused
        var dup = await admin.PostAsJsonAsync("/api/v1/enms/factors", new
        {
            source = "electricity", name = "dup", scope = 2, method = "Location", region = "TW", year = 2024, unit = "kWh", kgCo2ePerUnit = 0.5, reference = "x", isActive = true,
        });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        var market = await admin.PostAsJsonAsync("/api/v1/enms/factors", new
        {
            source = "electricity", name = "Green retailer", scope = 2, method = "Market", region = "TW", year = 2026, unit = "kWh", kgCo2ePerUnit = 0.0,
            reference = "Supplier disclosure 2026", isActive = true,
        });
        await Created(market, "factorId");
    }

    [Fact]
    public async Task Fuel_deliveries_and_green_power_flow_into_the_carbon_report()
    {
        var admin = await FactoryWithHistory();
        var window = $"from={DateTime.UtcNow.AddDays(-1):O}&to={DateTime.UtcNow.AddHours(1):O}";
        var before = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/energy/carbon?{window}");

        await Created(await admin.PostAsJsonAsync("/api/v1/enms/entries", new
        {
            source = "diesel", periodFrom = Today, periodTo = Today, quantity = 100, reference = "Delivery note 1",
        }), "entryId");
        await Created(await admin.PostAsJsonAsync("/api/v1/enms/entries", new
        {
            source = "green_power", periodFrom = Today, periodTo = Today, quantity = 1_000_000, instrument = "T-REC",
        }), "entryId");
        var bad = await admin.PostAsJsonAsync("/api/v1/enms/entries", new { source = "green_power", periodFrom = Today, periodTo = Today, quantity = 5 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);   // green power needs its certificate type

        var after = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/energy/carbon?{window}");
        var diesel = after.GetProperty("fuels").EnumerateArray().Single(f => f.GetProperty("source").GetString() == "diesel");
        Assert.Equal(1, diesel.GetProperty("scope").GetInt32());
        Assert.True(diesel.GetProperty("kg").GetDouble() > 0);
        Assert.True(after.GetProperty("scope1Kg").GetDouble() > before.GetProperty("scope1Kg").GetDouble());
        // more certified green power than the plant used: market-based Scope 2 is zero, location-based is unchanged
        Assert.Equal(0, after.GetProperty("scope2MarketKg").GetDouble());
        Assert.True(after.GetProperty("scope2Kg").GetDouble() > 0);
        Assert.Contains("MOEA", after.GetProperty("electricityReference").GetString());
        var gridKwh = after.GetProperty("gridKwh").GetDouble();
        var factor = after.GetProperty("electricityFactor").GetDouble();
        Assert.InRange(after.GetProperty("scope2Kg").GetDouble(), gridKwh * factor - 1, gridKwh * factor + 1);
    }

    [Fact]
    public async Task The_EnMS_records_feed_the_clause_readiness_overview()
    {
        var admin = await FactoryWithHistory();
        var empty = await admin.GetFromJsonAsync<JsonElement>("/api/v1/enms/overview");
        Assert.Equal(0, empty.GetProperty("readinessPct").GetDouble());

        var assets = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/plant/floor")).EnumerateArray()
            .Select(a => a.GetProperty("assetId").GetGuid()).Take(2).ToList();
        var seu = await Created(await admin.PostAsJsonAsync("/api/v1/enms/seus", new
        {
            name = "CNC machining", assetIds = assets, relevantVariables = "parts made", operatingCriteria = "Spindles off after 10 min idle", owner = "Maintenance lead",
        }), "seuId");
        await Created(await admin.PostAsJsonAsync("/api/v1/enms/documents", new
        {
            kind = "Policy", title = "Energy policy", body = "We commit to continual improvement of energy performance.", approvedBy = "Plant manager", approvedOn = Today,
        }), "documentId");
        var objective = await Created(await admin.PostAsJsonAsync("/api/v1/enms/objectives", new
        {
            title = "Cut site energy per part 5 %", targetPct = 5, targetDate = DateTime.UtcNow.AddMonths(6).ToString("yyyy-MM-dd"),
        }), "objectiveId");
        var action = await Created(await admin.PostAsJsonAsync("/api/v1/enms/actions", new
        {
            objectiveId = objective, title = "Fix compressed-air leaks", status = "Planned", expectedKwhYear = 12000,
        }), "actionId");
        // verified needs the verified savings and how they were measured
        var unverified = await admin.PutAsJsonAsync($"/api/v1/enms/actions/{action}", new { objectiveId = objective, title = "Fix compressed-air leaks", status = "Verified" });
        Assert.Equal(HttpStatusCode.BadRequest, unverified.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/enms/actions/{action}", new
        {
            objectiveId = objective, title = "Fix compressed-air leaks", status = "Verified", expectedKwhYear = 12000, verifiedKwhYear = 10500,
            verification = "Compressor kWh before/after, normalised to air demand",
        })).StatusCode);
        await Created(await admin.PostAsJsonAsync("/api/v1/enms/findings", new { kind = "Audit", title = "Internal audit 2026", clause = "9.2", heldOn = Today }), "findingId");
        var nc = await Created(await admin.PostAsJsonAsync("/api/v1/enms/findings", new
        {
            kind = "Nonconformity", title = "Meter calibration overdue", clause = "9.1", heldOn = Today, dueDate = Today,
        }), "findingId");
        var closeWithoutAction = await admin.PutAsJsonAsync($"/api/v1/enms/findings/{nc}", new
        {
            kind = "Nonconformity", title = "Meter calibration overdue", clause = "9.1", heldOn = Today, closedOn = Today,
        });
        Assert.Equal(HttpStatusCode.BadRequest, closeWithoutAction.StatusCode);

        var o = await admin.GetFromJsonAsync<JsonElement>("/api/v1/enms/overview");
        Assert.True(o.GetProperty("readinessPct").GetDouble() > 0);
        var clauses = o.GetProperty("clauses").EnumerateArray().ToDictionary(c => c.GetProperty("clause").GetString()!, c => c.GetProperty("state").GetString());
        Assert.Equal("ok", clauses["5.2"]);
        Assert.Equal("ok", clauses["6.2"]);
        Assert.Equal("ok", clauses["6.3"]);
        Assert.Equal("ok", clauses["9.2"]);
        Assert.Equal("missing", clauses["9.3"]);
        Assert.Equal(10500, o.GetProperty("verifiedKwhYear").GetDouble());
        Assert.Equal(1, o.GetProperty("openNonconformities").GetInt32());

        // an SEU still measured by nothing can be deleted
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/enms/seus/{seu}")).StatusCode);
    }

    [Fact]
    public async Task Energy_review_ranks_uses_and_an_EnPI_needs_enough_baseline_days()
    {
        var admin = await FactoryWithHistory();
        var review = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/enms/review?from={DateTime.UtcNow.AddDays(-1):yyyy-MM-dd}&to={DateTime.UtcNow.AddDays(2):yyyy-MM-dd}");
        var uses = review.GetProperty("uses").EnumerateArray().ToList();
        Assert.NotEmpty(uses);
        Assert.Contains(uses, u => u.GetProperty("suggested").GetBoolean());
        Assert.Contains(review.GetProperty("sources").EnumerateArray(), s => s.GetProperty("source").GetString() == "electricity");

        // the test factory has minutes of history, not the 7 days a regression baseline needs
        var enpi = await admin.PostAsJsonAsync("/api/v1/enms/enpis", new
        {
            name = "Site kWh vs parts", kind = "Regression", baselineFrom = DateTime.UtcNow.AddDays(-10).ToString("yyyy-MM-dd"), baselineTo = Today,
        });
        Assert.Equal(HttpStatusCode.BadRequest, enpi.StatusCode);
    }

    [Fact]
    public async Task Other_companies_see_none_of_the_EnMS_records_and_every_plan_has_it()
    {
        var admin = await FactoryWithHistory();
        await Created(await admin.PostAsJsonAsync("/api/v1/enms/documents", new { kind = "Scope", title = "Whole Taoyuan site" }), "documentId");
        var (_, _, other) = await app.NewTenantAsync("enterprise");
        Assert.Empty((await other.GetFromJsonAsync<JsonElement>("/api/v1/enms/documents")).EnumerateArray());

        var (_, _, trial) = await app.NewTenantAsync("trial");
        Assert.Equal(HttpStatusCode.OK, (await trial.GetAsync("/api/v1/enms/overview")).StatusCode);   // energy is in every plan
    }
}
