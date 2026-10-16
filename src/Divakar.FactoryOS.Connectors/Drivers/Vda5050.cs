using System.Text.Json;
using System.Text.Json.Serialization;
using Divakar.FactoryOS.Application.Robotics;
using MQTTnet;
using MQTTnet.Protocol;

namespace Divakar.FactoryOS.Connectors.Drivers;

/// <summary>
/// VDA 5050 v2 messages. A transport mission becomes an order with two nodes (pick-up, drop-off) joined by one edge;
/// the pick-up node carries a "pick" action and the drop-off node a "drop" action.
/// </summary>
public static class Vda5050
{
    public const string Version = "2.0.0";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed record NodePosition(double X, double Y, string MapId, double? Theta = null);
    public sealed record ActionParameter(string Key, string Value);
    public sealed record OrderAction(string ActionType, string ActionId, string BlockingType, IReadOnlyList<ActionParameter> ActionParameters);
    public sealed record Node(string NodeId, int SequenceId, bool Released, NodePosition NodePosition, IReadOnlyList<OrderAction> Actions);
    public sealed record Edge(string EdgeId, int SequenceId, bool Released, string StartNodeId, string EndNodeId, IReadOnlyList<OrderAction> Actions);
    public sealed record Order(uint HeaderId, string Timestamp, string Version, string Manufacturer, string SerialNumber, string OrderId,
        uint OrderUpdateId, IReadOnlyList<Node> Nodes, IReadOnlyList<Edge> Edges);

    /// <summary>The MQTT topic: interfaceName/majorVersion/manufacturer/serialNumber/order (default interface "uagv", version "v2").</summary>
    public static string Topic(string manufacturer, string serial, string interfaceName = "uagv") =>
        $"{interfaceName}/v2/{Clean(manufacturer)}/{Clean(serial)}/order";

    private static string Clean(string s) => string.Concat(s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'));

    public static Order BuildOrder(string manufacturer, string serial, string orderId, uint headerId, DateTime now, string mapId,
        (string Id, decimal X, decimal Y) from, (string Id, decimal X, decimal Y) to, string? payload)
    {
        IReadOnlyList<ActionParameter> load = payload is null ? [] : [new ActionParameter("loadType", payload)];
        return new Order(headerId, now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), Version, manufacturer, serial, orderId, 0,
            [
                new Node(from.Id, 0, true, new NodePosition((double)from.X, (double)from.Y, mapId), [new OrderAction("pick", $"{orderId}-pick", "HARD", load)]),
                new Node(to.Id, 2, true, new NodePosition((double)to.X, (double)to.Y, mapId), [new OrderAction("drop", $"{orderId}-drop", "HARD", load)]),
            ],
            [new Edge($"{from.Id}-{to.Id}", 1, true, from.Id, to.Id, [])]);
    }

    public static string Serialize(Order order) => JsonSerializer.Serialize(order, Json);
}

/// <summary>Publishes VDA 5050 orders to the vehicle's MQTT broker (the asset's endpoint and connection settings).</summary>
public sealed class Vda5050OrderPublisher : IOrderPublisher
{
    private static uint header;

    public bool Handles(string? protocolCode) => protocolCode == "vda5050";

    public async Task PublishAsync(VehicleState vehicle, OpenMission mission, string orderId, CancellationToken ct)
    {
        var s = new Settings(vehicle.Settings);
        if (string.IsNullOrWhiteSpace(vehicle.Vendor) || string.IsNullOrWhiteSpace(vehicle.SerialNo))
        {
            throw new InvalidOperationException("the vehicle needs its vendor (manufacturer) and serial number set");
        }
        var tls = s.Bool("tls", vehicle.Endpoint?.StartsWith("mqtts", StringComparison.OrdinalIgnoreCase) == true);
        var (host, port) = Endpoints.HostPort(vehicle.Endpoint, tls ? 8883 : 1883);
        var options = new MqttClientOptionsBuilder().WithTcpServer(host, port).WithClientId($"factoryos-dispatch-{Guid.NewGuid():N}"[..40])
            .WithTimeout(TimeSpan.FromSeconds(10));
        if (tls)
        {
            options = options.WithTlsOptions(o => o.UseTls());
        }
        if (s.String("username") is { Length: > 0 } user)
        {
            options = options.WithCredentials(user, s.String("password"));
        }

        var order = Vda5050.BuildOrder(vehicle.Vendor, vehicle.SerialNo, orderId, Interlocked.Increment(ref header), DateTime.UtcNow,
            s.String("mapId") ?? "factory", (mission.FromCode, mission.FromX, mission.FromY), (mission.ToCode, mission.ToX, mission.ToY), null);
        using var client = new MqttClientFactory().CreateMqttClient();
        await client.ConnectAsync(options.Build(), ct);
        await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(Vda5050.Topic(vehicle.Vendor, vehicle.SerialNo, s.String("interfaceName") ?? "uagv"))
            .WithPayload(Vda5050.Serialize(order))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), ct);
        await client.DisconnectAsync(cancellationToken: ct);
    }
}
