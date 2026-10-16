using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Divakar.FactoryOS.Application.Alarms;
using Divakar.FactoryOS.Application.Maintenance;
using Divakar.FactoryOS.Application.Notifications;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Divakar.FactoryOS.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class AlarmsMaintenanceTests(FactoryOsApp app)
{
    private async Task Run<T>(Func<T, Task> job) where T : notnull
    {
        using var scope = app.Services.CreateScope();
        await job(scope.ServiceProvider.GetRequiredService<T>());
    }

    /// <summary>A sensor that reports over HTTP with the given tags; returns its id and a device client.</summary>
    private async Task<(Guid Asset, HttpClient Device)> PushAsset(HttpClient admin, string type, params (string Code, string Sensor)[] tags)
    {
        Guid? parent = null;
        var prefix = "a" + Guid.NewGuid().ToString("N")[..6];
        foreach (var level in new[] { "Site", "Plant", "Area", "Line" })
        {
            var r = await admin.PostAsJsonAsync("/api/v1/plant/nodes", new { parentId = parent, level, code = $"{prefix}-{level.ToLowerInvariant()}", name = level });
            parent = (await r.Content.ReadFromJsonAsync<IdResponse>())!.NodeId;
        }
        var asset = (await (await admin.PostAsJsonAsync("/api/v1/plant/assets", new { nodeId = parent, assetTypeCode = type, protocolCode = "http", code = prefix, name = "Oven " + prefix }))
            .Content.ReadFromJsonAsync<IdResponse>())!.AssetId;
        foreach (var (code, sensor) in tags)
        {
            (await admin.PostAsJsonAsync($"/api/v1/plant/assets/{asset}/tags", new { code, name = code, sensorTypeCode = sensor })).EnsureSuccessStatusCode();
        }
        var key = (await (await admin.PostAsync($"/api/v1/plant/assets/{asset}/device-keys", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;
        var device = app.CreateClient();
        device.DefaultRequestHeaders.Add("X-Device-Key", key);
        return (asset, device);
    }

    private static Task Push(HttpClient device, string tag, double value, DateTime? ts = null) =>
        device.PostAsJsonAsync("/api/v1/ingest", new { readings = new[] { new { tag, value, ts = ts ?? DateTime.UtcNow } } });

    /// <summary>A tiny HTTP endpoint that records the bodies posted to it (stands in for a customer's webhook).</summary>
    private sealed class Hook : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        public List<string> Bodies { get; } = [];
        public string Url => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/alarms";

        public Hook()
        {
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    var stream = client.GetStream();
                    var buffer = new byte[65536];
                    var text = new StringBuilder();
                    int contentLength = -1, headerEnd = -1;
                    while (headerEnd < 0 || Encoding.UTF8.GetByteCount(text.ToString()[(headerEnd + 4)..]) < contentLength)
                    {
                        var n = await stream.ReadAsync(buffer, stop.Token);
                        if (n == 0) break;
                        text.Append(Encoding.UTF8.GetString(buffer, 0, n));
                        headerEnd = text.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (headerEnd >= 0 && contentLength < 0)
                        {
                            var line = text.ToString()[..headerEnd].Split("\r\n").First(l => l.StartsWith("Content-Length", StringComparison.OrdinalIgnoreCase));
                            contentLength = int.Parse(line.Split(':')[1].Trim());
                        }
                    }
                    lock (Bodies) Bodies.Add(text.ToString()[(headerEnd + 4)..]);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), stop.Token);
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
    public async Task Rule_raises_notifies_opens_a_work_order_is_acknowledged_and_clears()
    {
        await using var hook = new Hook();
        var (_, _, admin) = await app.NewTenantAsync("pro");
        var settings = JsonSerializer.Deserialize<Dictionary<string, object?>>((await admin.GetFromJsonAsync<JsonElement>("/api/v1/settings")).GetRawText())!;
        settings["alertEmails"] = "plant.manager@example.com";
        settings["alertWebhookUrl"] = hook.Url;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/v1/settings", settings)).StatusCode);

        var (oven, device) = await PushAsset(admin, "sensor", ("temp", "temperature"));
        var bad = await admin.PostAsJsonAsync("/api/v1/alarms/rules", new { name = "x", sensorTypeCode = "temperature", @operator = "~", threshold = 1, durationSec = 0, severity = "Critical" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var rule = await admin.PostAsJsonAsync("/api/v1/alarms/rules", new
        {
            name = "Oven too hot", sensorTypeCode = "temperature", assetId = oven, @operator = ">", threshold = 50, durationSec = 0, severity = "Critical",
            notify = true, createWorkOrder = true,
        });
        Assert.Equal(HttpStatusCode.Created, rule.StatusCode);

        await Push(device, "temp", 63.5);
        await Run<AlarmEngine>(e => e.RunOnceAsync(DateTime.UtcNow, default));
        var alarm = Assert.Single((await admin.GetFromJsonAsync<JsonElement>("/api/v1/alarms?state=open")).EnumerateArray());
        Assert.Equal("Critical", alarm.GetProperty("severity").GetString());
        Assert.Contains("63.5", alarm.GetProperty("message").GetString());
        var summary = await admin.GetFromJsonAsync<JsonElement>("/api/v1/alarms/summary");
        Assert.Equal(1, summary.GetProperty("critical").GetInt32());

        // a second pass does not raise it again
        await Run<AlarmEngine>(e => e.RunOnceAsync(DateTime.UtcNow, default));
        Assert.Single((await admin.GetFromJsonAsync<JsonElement>("/api/v1/alarms?state=open")).EnumerateArray());

        var orders = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/maintenance/work-orders?status=open")).EnumerateArray().ToList();
        var wo = Assert.Single(orders);
        Assert.Equal("Corrective", wo.GetProperty("type").GetString());
        Assert.Equal(alarm.GetProperty("alarmId").GetInt64(), wo.GetProperty("alarmId").GetInt64());

        await Run<NotificationSender>(s => s.RunOnceAsync(default));
        var sent = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications")).EnumerateArray().ToList();
        Assert.Equal("Skipped", sent.Single(n => n.GetProperty("channel").GetString() == "Email").GetProperty("status").GetString());   // no SMTP server in tests
        Assert.Equal("Sent", sent.Single(n => n.GetProperty("channel").GetString() == "Webhook").GetProperty("status").GetString());
        var body = JsonDocument.Parse(Assert.Single(hook.Bodies)).RootElement;
        Assert.Equal("Oven too hot", body.GetProperty("data").GetProperty("rule").GetString());

        var id = alarm.GetProperty("alarmId").GetInt64();
        var (_, _, stranger) = await app.NewTenantAsync("pro");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"/api/v1/alarms/{id}/ack", new { comment = "not mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/v1/alarms/{id}/ack", new { comment = "Door left open" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/alarms/{id}/ack", new { comment = "again" })).StatusCode);

        await Push(device, "temp", 21);
        await Run<AlarmEngine>(e => e.RunOnceAsync(DateTime.UtcNow, default));
        Assert.Empty((await admin.GetFromJsonAsync<JsonElement>("/api/v1/alarms?state=open")).EnumerateArray());
        var history = Assert.Single((await admin.GetFromJsonAsync<JsonElement>("/api/v1/alarms")).EnumerateArray());
        Assert.Equal("Cleared", history.GetProperty("state").GetString());
        Assert.Equal("Door left open", history.GetProperty("ackComment").GetString());
    }

    [Fact]
    public async Task Rules_wait_for_their_duration()
    {
        var (_, _, admin) = await app.NewTenantAsync("pro");
        var (oven, device) = await PushAsset(admin, "sensor", ("temp", "temperature"));
        await admin.PostAsJsonAsync("/api/v1/alarms/rules", new { name = "Hot for 2 min", sensorTypeCode = "temperature", assetId = oven, @operator = ">", threshold = 50, durationSec = 120, severity = "Warning", notify = false });
        await Push(device, "temp", 70);
        var now = DateTime.UtcNow;
        await Run<AlarmEngine>(e => e.RunOnceAsync(now, default));
        Assert.Empty((await admin.GetFromJsonAsync<JsonElement>("/api/v1/alarms?state=open")).EnumerateArray());
        await Run<AlarmEngine>(e => e.RunOnceAsync(now.AddMinutes(2.1), default));
        Assert.Single((await admin.GetFromJsonAsync<JsonElement>("/api/v1/alarms?state=open")).EnumerateArray());
    }

    [Fact]
    public async Task Rising_vibration_raises_an_anomaly_and_a_predictive_work_order()
    {
        var (_, _, admin) = await app.NewTenantAsync("pro");
        var (_, device) = await PushAsset(admin, "cnc", ("vib", "vibration_rms"));
        var now = DateTime.UtcNow;
        var rng = new Random(4);
        for (var i = 0; i < 60; i++)   // three normal hours
        {
            await Push(device, "vib", 1.6 + (rng.NextDouble() - 0.5) * 0.2, now.AddMinutes(-200 + i * 3));
        }
        foreach (var s in new[] { 4.8, 5.1, 5.3 })
        {
            await Push(device, "vib", s, now.AddSeconds(-90 + Array.IndexOf(new[] { 4.8, 5.1, 5.3 }, s) * 30));
        }
        await Run<AnomalyDetector>(d => d.RunOnceAsync(DateTime.UtcNow, default));
        var alarm = Assert.Single((await admin.GetFromJsonAsync<JsonElement>("/api/v1/alarms?state=open")).EnumerateArray());
        Assert.Equal("Anomaly", alarm.GetProperty("kind").GetString());
        Assert.Contains("bearing", alarm.GetProperty("message").GetString());
        var wo = Assert.Single((await admin.GetFromJsonAsync<JsonElement>("/api/v1/maintenance/work-orders?status=open")).EnumerateArray());
        Assert.Equal("Predictive", wo.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Preventive_plans_open_orders_that_need_their_checklist_and_reset_the_plan()
    {
        var (tenantId, _, admin) = await app.NewTenantAsync("pro");
        var (press, device) = await PushAsset(admin, "press", ("strokes", "part_count"));
        await Push(device, "strokes", 1000);

        var cal = await (await admin.PostAsJsonAsync("/api/v1/maintenance/plans", new
        {
            assetId = press, name = "Weekly press check", triggerType = "Calendar", interval = 7, checklist = new[] { "Check oil", "Check guards" }, estimatedMinutes = 30,
        })).Content.ReadFromJsonAsync<JsonElement>();
        var calId = cal.GetProperty("planId").GetGuid();
        var cyc = await admin.PostAsJsonAsync("/api/v1/maintenance/plans", new { assetId = press, name = "Die service", triggerType = "Cycles", interval = 500 });
        Assert.Equal(HttpStatusCode.Created, cyc.StatusCode);

        await Run<PmScheduler>(s => s.RunOnceAsync(DateTime.UtcNow, default));
        Assert.Empty((await admin.GetFromJsonAsync<JsonElement>("/api/v1/maintenance/work-orders?status=open")).EnumerateArray());

        // eight days later and 600 strokes on
        await using (var con = new SqlConnection(FactoryOsApp.ConnectionString))
        {
            await con.OpenAsync();
            await using var cmd = con.CreateCommand();
            cmd.CommandText = "EXEC sp_set_session_context N'System', 1; UPDATE maintenance.Plans SET LastDoneAt = DATEADD(DAY, -8, LastDoneAt) WHERE PlanId = @id";
            cmd.Parameters.AddWithValue("@id", calId);
            await cmd.ExecuteNonQueryAsync();
        }
        await Push(device, "strokes", 1600);
        await Run<PmScheduler>(s => s.RunOnceAsync(DateTime.UtcNow, default));
        await Run<PmScheduler>(s => s.RunOnceAsync(DateTime.UtcNow, default));   // no duplicates
        var orders = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/maintenance/work-orders?status=open")).EnumerateArray().ToList();
        Assert.Equal(2, orders.Count);
        var weekly = orders.Single(o => o.GetProperty("title").GetString() == "PM: Weekly press check");
        Assert.Equal(2, weekly.GetProperty("checklist").GetArrayLength());
        var plans = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/maintenance/plans")).EnumerateArray().ToList();
        Assert.Equal(600, plans.Single(p => p.GetProperty("name").GetString() == "Die service").GetProperty("used").GetDouble());

        var woId = weekly.GetProperty("maintWoId").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/maintenance/work-orders/{woId}/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/v1/maintenance/work-orders/{woId}/complete", null)).StatusCode);
        await admin.PutAsJsonAsync($"/api/v1/maintenance/work-orders/{woId}/checklist",
            new { items = new[] { new { text = "Check oil", done = true }, new { text = "Check guards", done = true } }, notes = "Topped up 0.5 L" });
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/maintenance/work-orders/{woId}/complete", null)).StatusCode);

        plans = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/maintenance/plans")).EnumerateArray().ToList();
        Assert.True(plans.Single(p => p.GetProperty("planId").GetGuid() == calId).GetProperty("used").GetDouble() < 0.1);
        await Run<PmScheduler>(s => s.RunOnceAsync(DateTime.UtcNow, default));
        Assert.Single((await admin.GetFromJsonAsync<JsonElement>("/api/v1/maintenance/work-orders?status=open")).EnumerateArray());   // only the die service

        var kpis = await admin.GetFromJsonAsync<JsonElement>("/api/v1/maintenance/kpis");
        Assert.Equal(1, kpis.GetProperty("open").GetInt32() + kpis.GetProperty("inProgress").GetInt32());
        Assert.Equal(1, kpis.GetProperty("doneLast30").GetInt32());
    }

    [Fact]
    public async Task Alarms_and_maintenance_need_their_modules()
    {
        var (_, _, energy) = await app.NewTenantAsync("energy");
        Assert.Equal(HttpStatusCode.Forbidden, (await energy.GetAsync("/api/v1/alarms")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await energy.GetAsync("/api/v1/maintenance/work-orders")).StatusCode);
    }
}
