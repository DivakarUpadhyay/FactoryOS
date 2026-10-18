using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Divakar.FactoryOS.Application.Alarms;
using Divakar.FactoryOS.Application.Notifications;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Divakar.FactoryOS.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class LineTests(FactoryOsApp app)
{
    private const string Secret = "0123456789abcdef0123456789abcdef";
    private const string Token = "test-channel-access-token-xyz";
    private const string Staff = "U11112222333344445555666677778888";
    private const string Group = "Caaaabbbbccccddddeeeeffff00001111";

    private async Task Run<T>(Func<T, Task> job) where T : notnull
    {
        using var scope = app.Services.CreateScope();
        await job(scope.ServiceProvider.GetRequiredService<T>());
    }

    [Fact]
    public async Task Alarms_reach_line_users_and_groups_picked_up_by_the_webhook()
    {
        var (tenantId, _, admin) = await app.NewTenantAsync("pro");

        // set up: both credentials are needed the first time, recipient ids are checked
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/integrations/line", new { channelSecret = Secret, enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/integrations/line",
            new { channelSecret = Secret, accessToken = Token, recipients = new[] { "boss@example.com" }, enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/v1/integrations/line",
            new { channelSecret = Secret, accessToken = Token, recipients = Array.Empty<string>(), enabled = true })).StatusCode);
        var view = await admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/line");
        Assert.True(view.GetProperty("configured").GetBoolean());
        Assert.DoesNotContain(Token, view.GetRawText());
        Assert.DoesNotContain(Secret, view.GetRawText());
        Assert.EndsWith($"/api/v1/integrations/line/webhook/{tenantId}", view.GetProperty("webhookUrl").GetString());
        await using (var con = new SqlConnection(FactoryOsApp.ConnectionString))
        {
            await con.OpenAsync();
            await using var cmd = con.CreateCommand();
            cmd.CommandText = "EXEC sp_set_session_context N'System', 1; SELECT ChannelSecret + AccessToken FROM core.LineChannels WHERE TenantId = @t";
            cmd.Parameters.AddWithValue("@t", tenantId);
            var stored = (string)(await cmd.ExecuteScalarAsync())!;
            Assert.StartsWith("enc:v1:", stored);
            Assert.DoesNotContain(Token, stored);
        }

        // LINE calls the webhook when staff write to the bot or add it to a group
        var anon = app.CreateClient();
        var body = Encoding.UTF8.GetBytes($$$"""
            {"destination":"Ubot","events":[
              {"type":"message","source":{"type":"user","userId":"{{{Staff}}}"},"message":{"type":"text","text":"hi, maintenance here"}},
              {"type":"join","source":{"type":"group","groupId":"{{{Group}}}"}}]}
            """);
        HttpRequestMessage Hook(string signature)
        {
            var m = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/integrations/line/webhook/{tenantId}") { Content = new ByteArrayContent(body) };
            m.Headers.Add("X-Line-Signature", signature);
            return m;
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(Hook("bm9wZQ=="))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.SendAsync(Hook(Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body))))).StatusCode);
        view = await admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/line");
        var contacts = view.GetProperty("contacts").EnumerateArray().ToDictionary(c => c.GetProperty("sourceId").GetString()!, c => c.GetProperty("kind").GetString());
        Assert.Equal("user", contacts[Staff]);
        Assert.Equal("group", contacts[Group]);

        // pick both, keep the stored secret and token
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/v1/integrations/line", new { recipients = new[] { Staff, Group }, enabled = true })).StatusCode);

        // an alarm goes out on LINE through the Messaging API push endpoint with the tenant's token
        var before = LineStub.Instance.Requests.Count;
        Guid? parent = null;
        foreach (var level in new[] { "Site", "Plant", "Area", "Line" })
        {
            var r = await admin.PostAsJsonAsync("/api/v1/plant/nodes", new { parentId = parent, level, code = $"ln-{level.ToLowerInvariant()}", name = level });
            parent = (await r.Content.ReadFromJsonAsync<IdResponse>())!.NodeId;
        }
        var chiller = (await (await admin.PostAsJsonAsync("/api/v1/plant/assets", new { nodeId = parent, assetTypeCode = "sensor", protocolCode = "http", code = "ch", name = "Chiller" }))
            .Content.ReadFromJsonAsync<IdResponse>())!.AssetId;
        (await admin.PostAsJsonAsync($"/api/v1/plant/assets/{chiller}/tags", new { code = "t", name = "Supply", sensorTypeCode = "temperature" })).EnsureSuccessStatusCode();
        var key = (await (await admin.PostAsync($"/api/v1/plant/assets/{chiller}/device-keys", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;
        var device = app.CreateClient();
        device.DefaultRequestHeaders.Add("X-Device-Key", key);
        (await admin.PostAsJsonAsync("/api/v1/alarms/rules", new
        {
            name = "Chiller supply warm", sensorTypeCode = "temperature", assetId = chiller, @operator = ">", threshold = 12, durationSec = 0, severity = "Warning", notify = true,
        })).EnsureSuccessStatusCode();
        await device.PostAsJsonAsync("/api/v1/ingest", new { readings = new[] { new { tag = "t", value = 15.2, ts = DateTime.UtcNow } } });
        await Run<AlarmEngine>(e => e.RunOnceAsync(DateTime.UtcNow, default));
        await Run<NotificationSender>(s => s.RunOnceAsync(default));

        var line = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications")).EnumerateArray().Where(n => n.GetProperty("channel").GetString() == "Line").ToList();
        Assert.Equal(2, line.Count);
        Assert.All(line, n => Assert.Equal("Sent", n.GetProperty("status").GetString()));
        var pushes = LineStub.Instance.Requests.Skip(before).Where(r => r.StartsWith("POST /v2/bot/message/push")).ToList();
        Assert.Equal(2, pushes.Count);
        Assert.All(pushes, p => Assert.Contains($"Authorization: Bearer {Token}", p));
        Assert.Contains(pushes, p => p.Contains($"\"to\":\"{Group}\"") && p.Contains("15.2"));

        // test button
        var test = await admin.PostAsync("/api/v1/integrations/line/test", null);
        Assert.Equal(2, (await test.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("queued").GetInt32());

        // turned off: queued LINE messages are skipped, not sent
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/v1/integrations/line", new { recipients = new[] { Staff }, enabled = false })).StatusCode);
        await Run<NotificationSender>(s => s.RunOnceAsync(default));
        var skipped = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications")).EnumerateArray()
            .Where(n => n.GetProperty("subject").GetString() == "Test from Divakar FactoryOS").ToList();
        Assert.All(skipped, n => Assert.Equal("Skipped", n.GetProperty("status").GetString()));
    }
}
