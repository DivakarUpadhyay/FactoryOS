using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Divakar.FactoryOS.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class PlatformFeatureTests(FactoryOsApp app)
{
    private static async Task<JsonElement> Get(HttpClient c, string url) => await c.GetFromJsonAsync<JsonElement>(url);

    [Fact]
    public async Task Reports_come_as_json_and_as_excel_friendly_csv()
    {
        var (_, _, admin) = await app.NewTenantAsync("enterprise");
        await admin.PostAsync("/api/v1/plant/demo", null);
        await app.RunEngineAsync(passes: 6, secondsApart: 60);
        var window = $"from={DateTime.UtcNow.AddHours(-1):O}&to={DateTime.UtcNow.AddHours(1):O}";

        var prod = await Get(admin, $"/api/v1/reports/production?{window}");
        Assert.Equal("Production and OEE", prod.GetProperty("title").GetString());
        Assert.Contains(prod.GetProperty("figures").EnumerateArray(), f => f.GetProperty("label").GetString() == "Plant OEE");
        Assert.True(prod.GetProperty("rows").GetArrayLength() >= 8);
        Assert.Equal(prod.GetProperty("columns").GetArrayLength(), prod.GetProperty("rows")[0].GetArrayLength());

        var csv = await admin.GetAsync($"/api/v1/reports/energy?{window}&format=csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.EndsWith(".csv", csv.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        var bytes = await csv.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.StartsWith("Divakar FactoryOS — Energy", text);
        Assert.Contains("Main incomer energy meter", text);

        foreach (var type in new[] { "maintenance", "quality", "alarms" })
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/reports/{type}?{window}")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/reports/payroll")).StatusCode);

        var schedule = await admin.PostAsJsonAsync("/api/v1/reports/schedules", new { reportType = "production", recipients = "boss@example.com; qa@example.com", localHour = 7 });
        Assert.Equal(HttpStatusCode.Created, schedule.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/reports/schedules", new { reportType = "production", recipients = "not-an-email", localHour = 7 })).StatusCode);
        var list = (await Get(admin, "/api/v1/reports/schedules")).EnumerateArray().Single();
        Assert.Equal("boss@example.com, qa@example.com", list.GetProperty("recipients").GetString());
    }

    [Fact]
    public async Task Dashboards_belong_to_their_owner_and_can_be_shared_read_only()
    {
        var (_, _, admin) = await app.NewTenantAsync("pro");
        var widgets = new object[] { new { type = "oee" }, new { type = "tag", tagId = Guid.NewGuid(), title = "Oven" }, new { type = "alarms" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/dashboards", new { name = "Bad", isShared = false, widgets = new[] { new { type = "pie-in-the-sky" } } })).StatusCode);
        var res = await admin.PostAsJsonAsync("/api/v1/dashboards", new { name = "Morning meeting", isShared = true, widgets });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("dashboardId").GetGuid();
        var mine = (await Get(admin, "/api/v1/dashboards")).EnumerateArray().Single();
        Assert.True(mine.GetProperty("mine").GetBoolean());
        Assert.Equal(3, mine.GetProperty("widgets").GetArrayLength());

        var email = $"v{Guid.NewGuid():N}"[..12] + "@x.test";
        (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = "Viewer", password = FactoryOsApp.Password, roles = new[] { "viewer" } })).EnsureSuccessStatusCode();
        var viewer = await app.SignInAsync(email);
        var shared = (await Get(viewer, "/api/v1/dashboards")).EnumerateArray().Single();
        Assert.False(shared.GetProperty("mine").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync($"/api/v1/dashboards/{id}", new { name = "Mine now", isShared = true, widgets })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync($"/api/v1/dashboards/{id}")).StatusCode);

        var (_, _, other) = await app.NewTenantAsync("pro");
        Assert.Empty((await Get(other, "/api/v1/dashboards")).EnumerateArray());
    }

    [Fact]
    public async Task Invoices_are_issued_once_per_month_with_tax_and_marked_paid()
    {
        var (tenantId, _, admin) = await app.NewTenantAsync("pro");
        var op = await app.OperatorAsync();
        var month = DateTime.UtcNow.ToString("yyyy-MM-01");
        var first = await (await op.PostAsync($"/api/v1/platform/billing/generate?month={month}", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(first.GetProperty("created").GetInt32() >= 1);
        var again = await (await op.PostAsync($"/api/v1/platform/billing/generate?month={month}", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, again.GetProperty("created").GetInt32());

        var mine = (await Get(admin, "/api/v1/billing/invoices")).EnumerateArray().Single();
        Assert.Equal(18000m, mine.GetProperty("subtotal").GetDecimal());
        Assert.Equal(900m, mine.GetProperty("tax").GetDecimal());
        Assert.Equal(18900m, mine.GetProperty("total").GetDecimal());
        Assert.StartsWith("FOS-", mine.GetProperty("number").GetString());

        var id = mine.GetProperty("invoiceId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync($"/api/v1/platform/billing/invoices/{id}/paid", new { paymentRef = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsJsonAsync($"/api/v1/platform/billing/invoices/{id}/paid", new { paymentRef = "Bank transfer 0042" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync($"/api/v1/platform/billing/invoices/{id}/void", new { paymentRef = (string?)null })).StatusCode);
        Assert.Equal("Paid", (await Get(admin, "/api/v1/billing/invoices"))[0].GetProperty("status").GetString());

        var overview = await Get(op, "/api/v1/platform/billing");
        Assert.True(overview.GetProperty("mrr").GetDecimal() >= 18000);
        Assert.Contains(overview.GetProperty("invoices").EnumerateArray(), i => i.GetProperty("tenantId").GetGuid() == tenantId);

        var usage = await Get(admin, "/api/v1/billing/usage");
        Assert.Equal("pro", usage.GetProperty("planCode").GetString());
        Assert.Equal(300, usage.GetProperty("maxAssets").GetInt32());

        var (_, _, trial) = await app.NewTenantAsync("trial");
        await op.PostAsync($"/api/v1/platform/billing/generate?month={month}", null);
        Assert.Empty((await Get(trial, "/api/v1/billing/invoices")).EnumerateArray());   // trials are not invoiced
    }

    [Fact]
    public async Task Api_keys_give_other_systems_read_only_access_to_one_company()
    {
        var (_, _, admin) = await app.NewTenantAsync("pro");
        await admin.PostAsync("/api/v1/plant/demo", null);
        var created = await (await admin.PostAsJsonAsync("/api/v1/integrations/api-keys", new { name = "SAP ERP" })).Content.ReadFromJsonAsync<JsonElement>();
        var key = created.GetProperty("key").GetString()!;
        Assert.StartsWith("fos_", key);

        var erp = app.CreateClient();
        erp.DefaultRequestHeaders.Add("X-Api-Key", key);
        var floor = await erp.GetAsync("/api/v1/plant/floor");
        Assert.Equal(HttpStatusCode.OK, floor.StatusCode);
        Assert.True((await floor.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength() >= 25);
        Assert.Equal(HttpStatusCode.OK, (await erp.GetAsync("/api/v1/production/work-orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await erp.PostAsJsonAsync("/api/v1/plant/nodes", new { level = "Site", code = "x", name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await erp.GetAsync("/api/v1/users")).StatusCode);   // no user administration

        var listed = (await Get(admin, "/api/v1/integrations/api-keys")).EnumerateArray().Single();
        Assert.Equal(created.GetProperty("prefix").GetString(), listed.GetProperty("prefix").GetString());
        Assert.DoesNotContain(key, listed.GetRawText());   // the key itself is never shown again

        (await admin.DeleteAsync($"/api/v1/integrations/api-keys/{created.GetProperty("prefix").GetString()}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await erp.GetAsync("/api/v1/plant/floor")).StatusCode);

        var wrong = app.CreateClient();
        wrong.DefaultRequestHeaders.Add("X-Api-Key", "fos_made_up");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/v1/plant/floor")).StatusCode);
    }

    [Fact]
    public async Task Assistant_answers_from_the_factory_data_without_an_ai_key()
    {
        var (_, _, admin) = await app.NewTenantAsync("enterprise");
        await admin.PostAsync("/api/v1/plant/demo", null);
        await app.RunEngineAsync(passes: 3, secondsApart: 60);
        var status = await Get(admin, "/api/v1/assistant/status");
        Assert.False(status.GetProperty("aiEnabled").GetBoolean());

        var res = await (await admin.PostAsJsonAsync("/api/v1/assistant/ask", new { question = "How is our energy use today?" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("built-in", res.GetProperty("source").GetString());
        Assert.Contains("kWh", res.GetProperty("answer").GetString());
        Assert.Contains("Energy", res.GetProperty("sections").EnumerateArray().Select(s => s.GetString()));

        var zh = await (await admin.PostAsJsonAsync("/api/v1/assistant/ask", new { question = "庫存不足的有哪些?" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Bearing 6205", zh.GetProperty("answer").GetString());

        var brief = await (await admin.PostAsJsonAsync("/api/v1/assistant/ask", new { question = "hello" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("Here is the factory right now", brief.GetProperty("answer").GetString());

        var (_, _, energyOnly) = await app.NewTenantAsync("energy");
        var limited = await (await energyOnly.PostAsJsonAsync("/api/v1/assistant/ask", new { question = "What is our OEE?" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("not in your plan", limited.GetProperty("answer").GetString());
    }
}
