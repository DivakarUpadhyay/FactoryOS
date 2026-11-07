using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Domain.Plant;

namespace Divakar.FactoryOS.Connectors.Drivers;

/// <summary>A ROS 2 tag address: "/topic|msg/type|$.path", e.g. "/battery_state|sensor_msgs/msg/BatteryState|$.percentage".</summary>
public sealed record Ros2Address(string Topic, string? Type, string? JsonPath)
{
    public static Ros2Address Parse(string address)
    {
        var parts = address.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !parts[0].StartsWith('/'))
        {
            throw new FormatException($"ROS 2 address '{address}' must start with the topic, e.g. /battery_state|sensor_msgs/msg/BatteryState|$.percentage.");
        }
        return parts.Length switch
        {
            1 => new Ros2Address(parts[0], null, "$.data"),
            2 => parts[1].StartsWith('$') ? new Ros2Address(parts[0], null, parts[1]) : new Ros2Address(parts[0], parts[1], "$.data"),
            _ => new Ros2Address(parts[0], parts[1], parts[2]),
        };
    }
}

/// <summary>
/// ROS 2 through rosbridge (rosbridge_suite, WebSocket JSON protocol v2). Endpoint "ws://robot:9090"; each tag subscribes to a topic
/// ({"op":"subscribe"}, throttled to the asset's poll interval) and reads a number out of the message with a JSON path. Messages
/// that arrive between polls are kept; each read hands over the newest value. A topic silent for three minutes reads as offline.
/// </summary>
public sealed class Ros2Connector(AssetBinding asset) : IConnector
{
    private readonly ClientWebSocket socket = new();
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<Guid, (double? Value, DateTime Ts)> latest = new();
    private readonly List<(TagBinding Tag, Ros2Address Address)> tags =
        asset.Tags.Where(t => !string.IsNullOrWhiteSpace(t.Address)).Select(t => (t, Ros2Address.Parse(t.Address!))).ToList();
    private Task? receiving;

    public async Task ConnectAsync(CancellationToken ct)
    {
        var endpoint = asset.Endpoint ?? throw new FormatException("The asset has no endpoint (ws://robot:9090).");
        var uri = new Uri(endpoint.Contains("://", StringComparison.Ordinal) ? endpoint : $"ws://{endpoint}");
        await socket.ConnectAsync(uri, ct);
        var throttle = Math.Max(0, asset.PollMs);
        foreach (var t in tags.Select(t => t.Address).DistinctBy(a => a.Topic))
        {
            var msg = new Dictionary<string, object?> { ["op"] = "subscribe", ["id"] = $"factoryos-{t.Topic}", ["topic"] = t.Topic, ["throttle_rate"] = throttle };
            if (t.Type is not null)
            {
                msg["type"] = t.Type;
            }
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(msg), WebSocketMessageType.Text, true, ct);
        }
        receiving = Task.Run(() => ReceiveAsync(stop.Token));
    }

    private async Task ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult r;
            try
            {
                r = await socket.ReceiveAsync(buffer, ct);
            }
            catch (Exception) when (ct.IsCancellationRequested || socket.State != WebSocketState.Open)
            {
                return;
            }
            if (r.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
            message.Write(buffer, 0, r.Count);
            if (!r.EndOfMessage)
            {
                continue;
            }
            Handle(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length), DateTime.UtcNow);
            message.SetLength(0);
        }
    }

    /// <summary>Applies one rosbridge message (only "publish" operations carry data).</summary>
    public void Handle(string json, DateTime now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("op", out var op) || op.GetString() != "publish" || !root.TryGetProperty("topic", out var topic)
                || !root.TryGetProperty("msg", out var msg))
            {
                return;
            }
            var raw = msg.GetRawText();
            foreach (var (tag, address) in tags.Where(t => t.Address.Topic == topic.GetString()))
            {
                if (new MqttAddress(address.Topic, address.JsonPath).Extract(raw) is { } v)
                {
                    latest[tag.TagId] = (v, now);
                }
            }
        }
        catch (JsonException)
        {
        }
    }

    public Task<IReadOnlyList<RawValue>> ReadAsync(DateTime now, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Lost the rosbridge connection.");
        }
        return Task.FromResult(Snapshot(now));
    }

    public IReadOnlyList<RawValue> Snapshot(DateTime now) =>
        [.. tags.Where(t => latest.ContainsKey(t.Tag.TagId)).Select(t =>
        {
            var v = latest[t.Tag.TagId];
            return new RawValue(t.Tag.TagId, v.Value, now - v.Ts > TimeSpan.FromSeconds(180) ? Quality.Offline : Quality.Good, v.Ts);
        })];

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
        }
        if (receiving is not null)
        {
            await Task.WhenAny(receiving, Task.Delay(2000));
        }
        socket.Dispose();
        stop.Dispose();
    }
}

public sealed class Ros2ConnectorFactory : IConnectorFactory
{
    public IReadOnlyCollection<string> Protocols { get; } = ["ros2"];

    public IConnector Create(AssetBinding asset) => new Ros2Connector(asset);
}
