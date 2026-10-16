using System.Net;
using System.Net.Sockets;
using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Application.Energy;
using FluentModbus;
using MQTTnet;
using MQTTnet.Protocol;

namespace Divakar.FactoryOS.Connectors.Drivers;

/// <summary>
/// Writes to a Modbus TCP device: a coil (on when the value is not zero) or a holding register (16-bit integers and 32-bit
/// floats/integers, honouring :swap). The tag's scale and offset are undone first, so you write engineering values.
/// </summary>
public sealed class ModbusWriter : IConnectorWriter
{
    public bool Supports(string protocolCode) => protocolCode == "modbus-tcp";

    public static ushort[] Registers(ModbusAddress a, double raw)
    {
        byte[] bytes = a.Type switch
        {
            ModbusType.Int16 => BitConverter.GetBytes(checked((short)Math.Round(raw))),
            ModbusType.UInt16 => BitConverter.GetBytes(checked((ushort)Math.Round(raw))),
            ModbusType.Int32 => BitConverter.GetBytes(checked((int)Math.Round(raw))),
            ModbusType.UInt32 => BitConverter.GetBytes(checked((uint)Math.Round(raw))),
            ModbusType.Float32 => BitConverter.GetBytes((float)raw),
            ModbusType.Float64 => BitConverter.GetBytes(raw),
            _ => throw new NotSupportedException($"cannot write {a.Type} to a register"),
        };
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);   // big-endian, high word first
        }
        var words = Enumerable.Range(0, bytes.Length / 2).Select(i => (ushort)(bytes[i * 2] << 8 | bytes[i * 2 + 1])).ToArray();
        return a.WordSwap ? [.. words.Reverse()] : words;
    }

    public async Task WriteAsync(AssetBinding asset, TagBinding tag, double value, CancellationToken ct)
    {
        var a = ModbusAddress.Parse(tag.Address ?? throw new InvalidOperationException("the tag has no address"));
        var raw = tag.Scale == 0 ? value : (value - tag.Offset) / tag.Scale;
        var unit = (byte)new Settings(asset.Settings).Int("unitId", 1);
        var (host, port) = Endpoints.HostPort(asset.Endpoint, 502);
        var ip = IPAddress.TryParse(host, out var parsed) ? parsed : (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct)).First();
        await Task.Run(() =>
        {
            using var client = new ModbusTcpClient { ReadTimeout = 5000, WriteTimeout = 5000 };
            client.Connect(new IPEndPoint(ip, port), ModbusEndianness.BigEndian);
            switch (a.Area)
            {
                case ModbusArea.Coil:
                    client.WriteSingleCoil(unit, (ushort)a.Offset, raw != 0);
                    break;
                case ModbusArea.HoldingRegister:
                    var regs = Registers(a, raw);
                    if (regs.Length == 1)
                    {
                        client.WriteSingleRegister(unit, (ushort)a.Offset, regs[0]);
                    }
                    else
                    {
                        client.WriteMultipleRegisters(unit, (ushort)a.Offset, regs);
                    }
                    break;
                default:
                    throw new InvalidOperationException("input registers and discrete inputs are read-only");
            }
            client.Disconnect();
        }, ct);
    }
}

/// <summary>Publishes the value to the tag's MQTT topic (the part before any "|$.json.path"), QoS 1.</summary>
public sealed class MqttWriter : IConnectorWriter
{
    public bool Supports(string protocolCode) => protocolCode == "mqtt";

    public async Task WriteAsync(AssetBinding asset, TagBinding tag, double value, CancellationToken ct)
    {
        var topic = MqttAddress.Parse(tag.Address ?? throw new InvalidOperationException("the tag has no address")).Topic;
        if (topic.Contains('+') || topic.Contains('#'))
        {
            throw new InvalidOperationException("cannot publish to a wildcard topic");
        }
        var s = new Settings(asset.Settings);
        var tls = s.Bool("tls", asset.Endpoint?.StartsWith("mqtts", StringComparison.OrdinalIgnoreCase) == true);
        var (host, port) = Endpoints.HostPort(asset.Endpoint, tls ? 8883 : 1883);
        var options = new MqttClientOptionsBuilder().WithTcpServer(host, port).WithClientId($"factoryos-write-{Guid.NewGuid():N}"[..40]).WithTimeout(TimeSpan.FromSeconds(10));
        if (s.String("username") is { } user)
        {
            options = options.WithCredentials(user, s.String("password"));
        }
        if (tls)
        {
            options = options.WithTlsOptions(o => o.UseTls());
        }
        var raw = tag.Scale == 0 ? value : (value - tag.Offset) / tag.Scale;
        using var client = new MqttClientFactory().CreateMqttClient();
        await client.ConnectAsync(options.Build(), ct);
        await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic)
            .WithPayload(raw.ToString(System.Globalization.CultureInfo.InvariantCulture)).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), ct);
        await client.DisconnectAsync(cancellationToken: ct);
    }
}
