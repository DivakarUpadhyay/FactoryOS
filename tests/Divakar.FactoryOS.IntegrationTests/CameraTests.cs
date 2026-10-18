using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Divakar.FactoryOS.Application.Alarms;
using Divakar.FactoryOS.Application.Plant;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Divakar.FactoryOS.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class CameraTests(FactoryOsApp app)
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0xFF, 0xD9];

    /// <summary>A camera on the LAN: /snap.jpg needs Basic sign-in cam / pass123, /page.html is not a picture.</summary>
    private sealed class FakeCamera : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        public string Base => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";

        public FakeCamera()
        {
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    var text = new StringBuilder();
                    while (!text.ToString().Contains("\r\n\r\n"))
                    {
                        var n = await stream.ReadAsync(buffer, stop.Token);
                        if (n == 0) break;
                        text.Append(Encoding.ASCII.GetString(buffer, 0, n));
                    }
                    var request = text.ToString();
                    var good = $"Authorization: Basic {Convert.ToBase64String(Encoding.ASCII.GetBytes("cam:pass123"))}";
                    byte[] head, body;
                    if (request.StartsWith("GET /page.html"))
                    {
                        body = Encoding.ASCII.GetBytes("<html></html>");
                        head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    }
                    else if (!request.Contains(good))
                    {
                        body = [];
                        head = Encoding.ASCII.GetBytes("HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"cam\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    }
                    else
                    {
                        body = Jpeg;
                        head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/jpeg\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    }
                    await stream.WriteAsync(head, stop.Token);
                    await stream.WriteAsync(body, stop.Token);
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            listener.Stop();
        }
    }

    [Fact]
    public async Task Asset_camera_shows_a_live_picture_and_one_is_kept_with_each_new_alarm()
    {
        await using var cam = new FakeCamera();
        var (tenantId, _, admin) = await app.NewTenantAsync("pro");
        Guid? parent = null;
        foreach (var level in new[] { "Site", "Plant", "Area", "Line" })
        {
            var r = await admin.PostAsJsonAsync("/api/v1/plant/nodes", new { parentId = parent, level, code = $"cam-{level.ToLowerInvariant()}", name = level });
            parent = (await r.Content.ReadFromJsonAsync<IdResponse>())!.NodeId;
        }
        var robot = (await (await admin.PostAsJsonAsync("/api/v1/plant/assets", new { nodeId = parent, assetTypeCode = "sensor", protocolCode = "http", code = "cell", name = "Weld cell" }))
            .Content.ReadFromJsonAsync<IdResponse>())!.AssetId;

        Assert.False((await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plant/assets/{robot}/camera")).GetProperty("configured").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/plant/assets/{robot}/camera", new { snapshotUrl = "ftp://cam/x.jpg" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/plant/assets/{robot}/camera", new { snapshotUrl = "http://cam:pw@10.0.0.5/x.jpg" })).StatusCode);

        // wrong password: a readable error, not a broken image
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/plant/assets/{robot}/camera",
            new { snapshotUrl = $"{cam.Base}/snap.jpg", username = "cam", password = "wrong" })).StatusCode);
        var refused = await admin.GetAsync($"/api/v1/plant/assets/{robot}/camera/snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("refused", await refused.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/plant/assets/{robot}/camera",
            new { snapshotUrl = $"{cam.Base}/snap.jpg", username = "cam", password = "pass123" })).StatusCode);
        var view = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plant/assets/{robot}/camera");
        Assert.True(view.GetProperty("hasPassword").GetBoolean());
        Assert.DoesNotContain("pass123", view.GetRawText());
        var shot = await admin.GetAsync($"/api/v1/plant/assets/{robot}/camera/snapshot");
        Assert.Equal("image/jpeg", shot.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Jpeg, await shot.Content.ReadAsByteArrayAsync());

        // saving again without a password keeps it
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/plant/assets/{robot}/camera",
            new { snapshotUrl = $"{cam.Base}/snap.jpg", username = "cam", snapOnAlarm = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/plant/assets/{robot}/camera/snapshot")).StatusCode);
        await using (var con = new SqlConnection(FactoryOsApp.ConnectionString))
        {
            await con.OpenAsync();
            await using var cmd = con.CreateCommand();
            cmd.CommandText = "EXEC sp_set_session_context N'System', 1; SELECT Password FROM plant.Cameras WHERE AssetId = @a";
            cmd.Parameters.AddWithValue("@a", robot);
            Assert.StartsWith("enc:v1:", (string)(await cmd.ExecuteScalarAsync())!);
        }

        // an alarm on the cell gets the picture of that moment
        (await admin.PostAsJsonAsync($"/api/v1/plant/assets/{robot}/tags", new { code = "t", name = "Torch", sensorTypeCode = "temperature" })).EnsureSuccessStatusCode();
        var key = (await (await admin.PostAsync($"/api/v1/plant/assets/{robot}/device-keys", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;
        var device = app.CreateClient();
        device.DefaultRequestHeaders.Add("X-Device-Key", key);
        (await admin.PostAsJsonAsync("/api/v1/alarms/rules", new
        {
            name = "Torch hot", sensorTypeCode = "temperature", assetId = robot, @operator = ">", threshold = 80, durationSec = 0, severity = "Warning", notify = false,
        })).EnsureSuccessStatusCode();
        await device.PostAsJsonAsync("/api/v1/ingest", new { readings = new[] { new { tag = "t", value = 95.0, ts = DateTime.UtcNow } } });
        using (var scope = app.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AlarmEngine>().RunOnceAsync(DateTime.UtcNow, default);
            await scope.ServiceProvider.GetRequiredService<CameraSnapshotJob>().RunOnceAsync(DateTime.UtcNow, default);
            await scope.ServiceProvider.GetRequiredService<CameraSnapshotJob>().RunOnceAsync(DateTime.UtcNow, default);   // one picture per alarm
        }
        var alarm = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/alarms?state=open")).EnumerateArray().Single();
        var alarmShot = await admin.GetAsync($"/api/v1/alarms/{alarm.GetProperty("alarmId").GetInt64()}/snapshot");
        Assert.Equal(Jpeg, await alarmShot.Content.ReadAsByteArrayAsync());

        // another company cannot see it; a page that is not a picture is refused; removing the URL removes the camera
        var (_, _, other) = await app.NewTenantAsync("pro");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/alarms/{alarm.GetProperty("alarmId").GetInt64()}/snapshot")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/plant/assets/{robot}/camera", new { snapshotUrl = $"{cam.Base}/page.html" })).StatusCode);
        var notImage = await admin.GetAsync($"/api/v1/plant/assets/{robot}/camera/snapshot");
        Assert.Contains("instead of a picture", await notImage.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/plant/assets/{robot}/camera", new { snapshotUrl = "" })).StatusCode);
        Assert.False((await admin.GetFromJsonAsync<JsonElement>($"/api/v1/plant/assets/{robot}/camera")).GetProperty("configured").GetBoolean());
        _ = tenantId;
    }
}
