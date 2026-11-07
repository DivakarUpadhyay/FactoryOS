using System.Collections.Concurrent;
using System.Text.Json;
using Divakar.FactoryOS.Application.Ai;
using Divakar.FactoryOS.Application.Connectivity;
using MQTTnet;

namespace Divakar.FactoryOS.Edge;

/// <summary>The configuration the agent works from: the cloud's when it answers, else the cached one.</summary>
public sealed class EdgeState
{
    private volatile EdgeConfig? config;
    public EdgeConfig? Config { get => config; set => config = value; }
    public string? LastError { get; set; }
}

/// <summary>
/// The ingestion engine's store on the edge: bindings come from the agent's configuration; readings go to the disk buffer, the
/// local detectors and (optionally) the Unified Namespace; machine health is kept for the next upload.
/// </summary>
public sealed class EdgeStore(EdgeState state, DiskBuffer buffer, EdgeAi ai, UnsPublisher uns) : IIngestionStore
{
    private readonly ConcurrentDictionary<Guid, AssetHealth> health = new();

    public Task<IReadOnlyList<AssetBinding>> ActiveBindingsAsync(IReadOnlyCollection<string> protocols, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AssetBinding>>([.. (state.Config?.Assets ?? []).Where(a => protocols.Contains(a.ProtocolCode))]);

    public async Task WriteAsync(IReadOnlyList<ReadingRow> rows, CancellationToken ct)
    {
        buffer.Append(rows.Select(r => new EdgeReading(r.TagId, r.Ts, r.Value, r.Quality, r.Store)));
        ai.Observe(rows);
        await uns.PublishAsync(rows, ct);
    }

    public Task UpdateHealthAsync(IReadOnlyList<AssetHealth> list, CancellationToken ct)
    {
        foreach (var h in list)
        {
            health[h.AssetId] = h;
        }
        return Task.CompletedTask;
    }

    /// <summary>Health gathered since the last upload (taken away).</summary>
    public IReadOnlyList<AssetHealth> TakeHealth()
    {
        var list = new List<AssetHealth>();
        foreach (var key in health.Keys)
        {
            if (health.TryRemove(key, out var h))
            {
                list.Add(h);
            }
        }
        return list;
    }
}

/// <summary>
/// The OT detectors next to the machines: a streaming model per analog tag (spikes, drift, frozen values), so a problem is seen
/// within seconds even when the link to the cloud is down. Findings are queued and sent; one per tag and kind every 15 minutes.
/// </summary>
public sealed class EdgeAi(EdgeState state, EdgeOptions options)
{
    private static readonly HashSet<string> Analog = [.. OtAiJob.AnalogSensors, "active_power", "voltage_ln", "frequency", "power_factor"];
    private readonly ConcurrentDictionary<Guid, OnlineDetector> models = new();
    private readonly ConcurrentDictionary<(Guid, string), DateTime> lastSent = new();
    private readonly ConcurrentQueue<EdgeInsight> queue = new();

    public int Models => models.Count;

    public void Observe(IReadOnlyList<ReadingRow> rows)
    {
        if (!options.LocalAi || state.Config is not { } config)
        {
            return;
        }
        var tags = config.Assets.SelectMany(a => a.Tags.Select(t => (Asset: a, Tag: t))).ToDictionary(x => x.Tag.TagId);
        foreach (var r in rows)
        {
            if (r.Value is not { } v || r.Quality > 1 || !tags.TryGetValue(r.TagId, out var x) || !Analog.Contains(x.Tag.SensorTypeCode))
            {
                continue;
            }
            foreach (var f in models.GetOrAdd(r.TagId, _ => new OnlineDetector()).Add(v))
            {
                var key = (r.TagId, f.Kind);
                if (lastSent.TryGetValue(key, out var at) && r.Ts - at < TimeSpan.FromMinutes(15))
                {
                    continue;
                }
                lastSent[key] = r.Ts;
                var what = f.Kind switch { "Spike" => "sudden jump", "Drift" => "drifting", _ => "frozen" };
                queue.Enqueue(new EdgeInsight(x.Asset.AssetId, r.TagId, f.Kind, f.Kind == "Spike" && f.Score > 10 ? "Critical" : "Warning",
                    $"{x.Tag.Code} on {x.Asset.AssetCode}: {what} (edge)", $"Seen at {r.Ts:yyyy-MM-dd HH:mm:ss} UTC: {f.Detail}.", Math.Round(f.Score, 2)));
            }
        }
    }

    public IReadOnlyList<EdgeInsight> Take(int max = 200)
    {
        var list = new List<EdgeInsight>();
        while (list.Count < max && queue.TryDequeue(out var i))
        {
            list.Add(i);
        }
        return list;
    }

    public void Requeue(IEnumerable<EdgeInsight> list)
    {
        foreach (var i in list)
        {
            queue.Enqueue(i);
        }
    }
}

/// <summary>
/// Optional Unified Namespace publisher: each reading goes to {prefix}/{asset code}/{tag code} on a local MQTT broker as a retained
/// JSON message {"value", "ts", "quality"}, so other OT applications on the plant network can subscribe to one consistent tree.
/// </summary>
public sealed class UnsPublisher(EdgeState state, EdgeOptions options, ILogger<UnsPublisher> log) : IAsyncDisposable
{
    private IMqttClient? client;
    private DateTime retryAt;

    public async Task PublishAsync(IReadOnlyList<ReadingRow> rows, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.UnsBroker) || state.Config is not { } config || DateTime.UtcNow < retryAt)
        {
            return;
        }
        try
        {
            if (client is not { IsConnected: true })
            {
                client?.Dispose();
                client = new MqttClientFactory().CreateMqttClient();
                var parts = options.UnsBroker.Split(':');
                await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 1883)
                    .WithClientId($"factoryos-edge-{config.AgentId:N}"[..36]).Build(), ct);
            }
            var tags = config.Assets.SelectMany(a => a.Tags.Select(t => (a.AssetCode, t))).ToDictionary(x => x.t.TagId);
            foreach (var r in rows.GroupBy(r => r.TagId).Select(g => g.MaxBy(x => x.Ts)!))
            {
                if (!tags.TryGetValue(r.TagId, out var x))
                {
                    continue;
                }
                var payload = JsonSerializer.Serialize(new { value = r.Value, ts = r.Ts, quality = r.Quality });
                await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic($"{options.UnsPrefix}/{x.AssetCode}/{x.t.Code}")
                    .WithPayload(payload).WithRetainFlag().Build(), ct);
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Unified Namespace broker {Broker} unavailable: {Error}; retrying in 30 s", options.UnsBroker, e.Message);
            retryAt = DateTime.UtcNow.AddSeconds(30);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (client is { IsConnected: true })
        {
            await client.DisconnectAsync();
        }
        client?.Dispose();
    }
}
