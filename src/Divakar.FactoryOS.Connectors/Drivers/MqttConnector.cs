using System.Collections.Concurrent;
using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Domain.Plant;
using MQTTnet;

namespace Divakar.FactoryOS.Connectors.Drivers;

/// <summary>
/// MQTT subscriber. Endpoint "broker:1883" (or mqtts://broker:8883); settings {"username", "password", "tls", "clientId"}.
/// Each tag's address is a topic (wildcards allowed) with an optional JSON path: "plant/line1/temp" or
/// "factory/+/meter|$.power.kw". Messages arriving between polls are kept; each read hands over the newest value.
/// A tag with no message for three heartbeats reads as offline.
/// </summary>
public sealed class MqttConnector(AssetBinding asset) : IConnector
{
    private readonly IMqttClient client = new MqttClientFactory().CreateMqttClient();
    private readonly ConcurrentDictionary<Guid, (double? Value, DateTime Ts)> latest = new();
    private readonly List<(TagBinding Tag, MqttAddress Address)> tags =
        asset.Tags.Where(t => !string.IsNullOrWhiteSpace(t.Address)).Select(t => (t, MqttAddress.Parse(t.Address!))).ToList();

    public async Task ConnectAsync(CancellationToken ct)
    {
        var s = new Settings(asset.Settings);
        var tls = s.Bool("tls", asset.Endpoint?.StartsWith("mqtts", StringComparison.OrdinalIgnoreCase) == true);
        var (host, port) = Endpoints.HostPort(asset.Endpoint, tls ? 8883 : 1883);
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId(s.String("clientId") ?? $"factoryos-{asset.AssetCode}-{Guid.NewGuid():N}"[..40])
            .WithCleanSession();
        if (s.String("username") is { } user)
        {
            builder = builder.WithCredentials(user, s.String("password"));
        }
        if (tls)
        {
            builder = builder.WithTlsOptions(o => o.UseTls());
        }
        client.ApplicationMessageReceivedAsync += e =>
        {
            var topic = e.ApplicationMessage.Topic;
            var payload = e.ApplicationMessage.ConvertPayloadToString() ?? "";
            foreach (var (tag, address) in tags)
            {
                if (MqttAddress.Matches(address.Topic, topic) && address.Extract(payload) is { } v)
                {
                    latest[tag.TagId] = (v, DateTime.UtcNow);
                }
            }
            return Task.CompletedTask;
        };
        await client.ConnectAsync(builder.Build(), ct);
        var subscribe = new MqttClientSubscribeOptionsBuilder();
        foreach (var topic in tags.Select(t => t.Address.Topic).Distinct())
        {
            subscribe = subscribe.WithTopicFilter(f => f.WithTopic(topic));
        }
        if (tags.Count > 0)
        {
            await client.SubscribeAsync(subscribe.Build(), ct);
        }
    }

    public Task<IReadOnlyList<RawValue>> ReadAsync(DateTime now, CancellationToken ct)
    {
        if (!client.IsConnected)
        {
            throw new InvalidOperationException("Lost the connection to the MQTT broker.");
        }
        var stale = TimeSpan.FromSeconds(180);
        IReadOnlyList<RawValue> values = asset.Tags.Select(t => latest.TryGetValue(t.TagId, out var v)
                ? new RawValue(t.TagId, v.Value, now - v.Ts > stale ? Quality.Offline : Quality.Good, v.Ts)
                : new RawValue(t.TagId, null, Quality.Uncertain, now))
            .Where(v => v.Quality != Quality.Uncertain)   // nothing received yet: report nothing rather than a gap
            .ToList();
        return Task.FromResult(values);
    }

    public async ValueTask DisposeAsync()
    {
        if (client.IsConnected)
        {
            await client.DisconnectAsync();
        }
        client.Dispose();
    }
}

public sealed class MqttConnectorFactory : IConnectorFactory
{
    public IReadOnlyCollection<string> Protocols { get; } = ["mqtt", "vda5050"];

    public IConnector Create(AssetBinding asset) => new MqttConnector(asset);
}
