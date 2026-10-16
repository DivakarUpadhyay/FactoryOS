using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Divakar.FactoryOS.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class LiveAndFloorTests(FactoryOsApp app)
{
    private HubConnection Hub(HttpClient signedIn) => new HubConnectionBuilder()
        .WithUrl(new Uri(app.Server.BaseAddress, "/hubs/live"), o =>
        {
            o.Transports = HttpTransportType.LongPolling;
            o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
            o.AccessTokenProvider = () => Task.FromResult<string?>(FactoryOsApp.TokenOf(signedIn));
        })
        .Build();

    [Fact]
    public async Task Live_hub_needs_a_token()
    {
        var anonymous = new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, "/hubs/live"), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
            })
            .Build();
        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync());
    }

    [Fact]
    public async Task Live_values_reach_only_the_tenants_own_browsers()
    {
        var (_, _, a) = await app.NewTenantAsync();
        var (_, _, b) = await app.NewTenantAsync();
        await a.PostAsync("/api/v1/plant/demo", null);
        var aAssets = (await a.GetFromJsonAsync<JsonElement>("/api/v1/plant/floor")).EnumerateArray()
            .Select(x => x.GetProperty("assetId").GetGuid()).ToHashSet();

        var gotA = new ConcurrentBag<Guid>();
        var gotB = new ConcurrentBag<Guid>();
        await using var hubA = Hub(a);
        await using var hubB = Hub(b);
        hubA.On<List<JsonElement>>("values", v => { foreach (var x in v) gotA.Add(x.GetProperty("assetId").GetGuid()); });
        hubB.On<List<JsonElement>>("values", v => { foreach (var x in v) gotB.Add(x.GetProperty("assetId").GetGuid()); });
        await hubA.StartAsync();
        await hubB.StartAsync();

        await app.RunEngineAsync(passes: 1);
        for (var i = 0; i < 40 && gotA.IsEmpty; i++)
        {
            await Task.Delay(250);
        }
        Assert.NotEmpty(gotA);
        Assert.All(gotA, id => Assert.Contains(id, aAssets));
        Assert.DoesNotContain(gotB, aAssets.Contains);
    }

    [Fact]
    public async Task Floor_plan_lists_assets_with_area_line_and_power_and_saves_positions()
    {
        var (_, _, admin) = await app.NewTenantAsync();
        await admin.PostAsync("/api/v1/plant/demo", null);
        await app.RunEngineAsync(passes: 1);

        var floor = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/plant/floor")).EnumerateArray().ToList();
        Assert.Equal(44, floor.Count);
        var cnc = floor.First(x => x.GetProperty("code").GetString() == "demo-cnc-01");
        Assert.Equal("Machining Hall", cnc.GetProperty("areaName").GetString());
        Assert.False(string.IsNullOrEmpty(cnc.GetProperty("lineName").GetString()));
        Assert.Equal(JsonValueKind.Number, cnc.GetProperty("powerKw").ValueKind);

        var id = cnc.GetProperty("assetId").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/plant/assets/{id}/layout", new { x = 120, y = 340 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/plant/assets/{id}/layout", new { x = 5000, y = 1 })).StatusCode);
        var again = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/plant/floor")).EnumerateArray()
            .First(x => x.GetProperty("assetId").GetGuid() == id);
        Assert.Equal(120, again.GetProperty("layoutX").GetInt32());
        Assert.Equal(340, again.GetProperty("layoutY").GetInt32());

        // another company can neither see nor move it
        var (_, _, other) = await app.NewTenantAsync();
        Assert.Empty((await other.GetFromJsonAsync<JsonElement>("/api/v1/plant/floor")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/v1/plant/assets/{id}/layout", new { x = 1, y = 1 })).StatusCode);
    }
}
