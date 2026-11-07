using System.Collections.Concurrent;
using System.Text;
using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Domain.Plant;
using MQTTnet;

namespace Divakar.FactoryOS.Connectors.Drivers;

/// <summary>One metric of a Sparkplug B payload (sparkplug_b.proto: Payload.Metric).</summary>
public sealed record SpMetric(string? Name, ulong? Alias, ulong? Timestamp, uint DataType, bool IsNull, double? Value);

public sealed record SpPayload(ulong? Timestamp, ulong? Seq, IReadOnlyList<SpMetric> Metrics);

/// <summary>
/// The parts of the Sparkplug B protobuf a host application needs, read and written by hand (no generated code): Payload
/// {timestamp = 1, metrics = 2, seq = 3} and Metric {name = 1, alias = 2, timestamp = 3, datatype = 4, is_null = 7, int_value = 10,
/// long_value = 11, float_value = 12, double_value = 13, boolean_value = 14, string_value = 15}. Other fields are skipped.
/// </summary>
public static class SparkplugCodec
{
    public const uint Int8 = 1, Int16 = 2, Int32 = 3, Int64 = 4, UInt8 = 5, UInt16 = 6, UInt32 = 7, UInt64 = 8, Float = 9, Double = 10, Boolean = 11,
        String = 12, DateTime = 13, Text = 14;

    public static SpPayload Decode(ReadOnlySpan<byte> data)
    {
        ulong? ts = null, seq = null;
        var metrics = new List<SpMetric>();
        var r = new Reader(data);
        while (!r.End)
        {
            var (field, wire) = r.Key();
            switch (field)
            {
                case 1 when wire == 0: ts = r.Varint(); break;
                case 2 when wire == 2: metrics.Add(Metric(r.Bytes())); break;
                case 3 when wire == 0: seq = r.Varint(); break;
                default: r.Skip(wire); break;
            }
        }
        return new SpPayload(ts, seq, metrics);
    }

    private static SpMetric Metric(ReadOnlySpan<byte> data)
    {
        string? name = null;
        ulong? alias = null, ts = null;
        uint type = 0;
        var isNull = false;
        ulong? intValue = null;
        double? value = null;
        string? text = null;
        var r = new Reader(data);
        while (!r.End)
        {
            var (field, wire) = r.Key();
            switch (field)
            {
                case 1 when wire == 2: name = Encoding.UTF8.GetString(r.Bytes()); break;
                case 2 when wire == 0: alias = r.Varint(); break;
                case 3 when wire == 0: ts = r.Varint(); break;
                case 4 when wire == 0: type = (uint)r.Varint(); break;
                case 7 when wire == 0: isNull = r.Varint() != 0; break;
                case 10 when wire == 0: intValue = (uint)r.Varint(); break;
                case 11 when wire == 0: intValue = r.Varint(); break;
                case 12 when wire == 5: value = BitConverter.Int32BitsToSingle((int)r.Fixed32()); break;
                case 13 when wire == 1: value = BitConverter.Int64BitsToDouble((long)r.Fixed64()); break;
                case 14 when wire == 0: value = r.Varint() != 0 ? 1 : 0; break;
                case 15 when wire == 2: text = Encoding.UTF8.GetString(r.Bytes()); break;
                default: r.Skip(wire); break;
            }
        }
        if (intValue is { } iv)
        {
            // signed types travel as two's complement in the unsigned fields
            value = type switch
            {
                Int8 => (sbyte)(byte)iv, Int16 => (short)(ushort)iv, Int32 => (int)(uint)iv, Int64 => (long)iv,
                Boolean => iv != 0 ? 1 : 0, _ => iv,
            };
        }
        if (text is not null && value is null && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            value = d;
        }
        return new SpMetric(name, alias, ts, type, isNull, isNull ? null : value);
    }

    /// <summary>Encodes a payload (used for the rebirth command, and by tests and simulators).</summary>
    public static byte[] Encode(SpPayload p)
    {
        var w = new Writer();
        if (p.Timestamp is { } ts)
        {
            w.Key(1, 0).Varint(ts);
        }
        foreach (var m in p.Metrics)
        {
            var mw = new Writer();
            if (m.Name is not null)
            {
                mw.Key(1, 2).Bytes(Encoding.UTF8.GetBytes(m.Name));
            }
            if (m.Alias is { } a)
            {
                mw.Key(2, 0).Varint(a);
            }
            if (m.Timestamp is { } mt)
            {
                mw.Key(3, 0).Varint(mt);
            }
            mw.Key(4, 0).Varint(m.DataType);
            if (m.IsNull || m.Value is null)
            {
                mw.Key(7, 0).Varint(1);
            }
            else
            {
                var v = m.Value.Value;
                switch (m.DataType)
                {
                    case Int8 or Int16 or Int32: mw.Key(10, 0).Varint((uint)(int)v); break;
                    case UInt8 or UInt16 or UInt32: mw.Key(10, 0).Varint((uint)v); break;
                    case Int64: mw.Key(11, 0).Varint((ulong)(long)v); break;
                    case UInt64 or DateTime: mw.Key(11, 0).Varint((ulong)v); break;
                    case Float: mw.Key(12, 5).Fixed32((uint)BitConverter.SingleToInt32Bits((float)v)); break;
                    case Boolean: mw.Key(14, 0).Varint(v != 0 ? 1UL : 0); break;
                    case String or Text: mw.Key(15, 2).Bytes(Encoding.UTF8.GetBytes(v.ToString(System.Globalization.CultureInfo.InvariantCulture))); break;
                    default: mw.Key(13, 1).Fixed64((ulong)BitConverter.DoubleToInt64Bits(v)); break;
                }
            }
            w.Key(2, 2).Bytes(mw.ToArray());
        }
        if (p.Seq is { } seq)
        {
            w.Key(3, 0).Varint(seq);
        }
        return w.ToArray();
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;
        private int pos;

        public readonly bool End => pos >= data.Length;

        public (int Field, int Wire) Key()
        {
            var k = Varint();
            return ((int)(k >> 3), (int)(k & 7));
        }

        public ulong Varint()
        {
            ulong v = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (pos >= data.Length)
                {
                    throw new FormatException("Truncated varint in Sparkplug payload.");
                }
                var b = data[pos++];
                v |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return v;
                }
            }
            throw new FormatException("Varint too long in Sparkplug payload.");
        }

        public uint Fixed32()
        {
            var v = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
            pos += 4;
            return v;
        }

        public ulong Fixed64()
        {
            var v = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data[pos..]);
            pos += 8;
            return v;
        }

        public ReadOnlySpan<byte> Bytes()
        {
            var len = (int)Varint();
            if (len < 0 || pos + len > data.Length)
            {
                throw new FormatException("Truncated field in Sparkplug payload.");
            }
            var s = data.Slice(pos, len);
            pos += len;
            return s;
        }

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: Varint(); break;
                case 1: pos += 8; break;
                case 2: Bytes(); break;
                case 5: pos += 4; break;
                default: throw new FormatException($"Unsupported protobuf wire type {wire}.");
            }
        }
    }

    private sealed class Writer
    {
        private readonly MemoryStream ms = new();

        public Writer Key(int field, int wire) => Varint((ulong)((field << 3) | wire));

        public Writer Varint(ulong v)
        {
            while (v >= 0x80)
            {
                ms.WriteByte((byte)(v | 0x80));
                v >>= 7;
            }
            ms.WriteByte((byte)v);
            return this;
        }

        public Writer Fixed32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            ms.Write(b);
            return this;
        }

        public Writer Fixed64(ulong v)
        {
            Span<byte> b = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(b, v);
            ms.Write(b);
            return this;
        }

        public Writer Bytes(byte[] b)
        {
            Varint((ulong)b.Length);
            ms.Write(b);
            return this;
        }

        public byte[] ToArray() => ms.ToArray();
    }
}

/// <summary>A Sparkplug tag address: "group/edgeNode/device|metric name" (device optional for node metrics).</summary>
public sealed record SparkplugAddress(string Group, string Node, string? Device, string Metric)
{
    public static SparkplugAddress Parse(string address)
    {
        var i = address.IndexOf('|');
        if (i < 0)
        {
            throw new FormatException($"Sparkplug address '{address}' must be group/edgeNode[/device]|metric.");
        }
        var parts = address[..i].Trim().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3)
        {
            throw new FormatException($"Sparkplug address '{address}' must be group/edgeNode[/device]|metric.");
        }
        return new SparkplugAddress(parts[0], parts[1], parts.Length == 3 ? parts[2] : null, address[(i + 1)..].Trim());
    }

    public string Source => Device is null ? $"{Group}/{Node}" : $"{Group}/{Node}/{Device}";
}

/// <summary>
/// Sparkplug B host application (Eclipse Sparkplug 3.0). Endpoint "broker:1883"; settings as for MQTT plus {"hostId"}. Subscribes to the
/// groups and nodes its tags name; BIRTH messages give metric names and aliases, DATA messages carry values (often by alias only),
/// DEATH marks the node or device offline. When a DATA message uses an alias the host has not seen in a BIRTH, it asks the edge
/// node to rebirth (NCMD "Node Control/Rebirth"), at most once a minute per node.
/// </summary>
public sealed class SparkplugConnector(AssetBinding asset) : IConnector
{
    private readonly IMqttClient client = new MqttClientFactory().CreateMqttClient();
    private readonly List<(TagBinding Tag, SparkplugAddress Address)> tags =
        asset.Tags.Where(t => !string.IsNullOrWhiteSpace(t.Address)).Select(t => (t, SparkplugAddress.Parse(t.Address!))).ToList();
    private readonly ConcurrentDictionary<Guid, (double? Value, DateTime Ts)> latest = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ulong, string>> aliases = new();
    private readonly ConcurrentDictionary<string, DateTime> dead = new();
    private readonly ConcurrentDictionary<string, DateTime> rebirthAsked = new();

    public int RebirthRequests { get; private set; }

    public async Task ConnectAsync(CancellationToken ct)
    {
        var s = new Settings(asset.Settings);
        var tls = s.Bool("tls", asset.Endpoint?.StartsWith("mqtts", StringComparison.OrdinalIgnoreCase) == true);
        var (host, port) = Endpoints.HostPort(asset.Endpoint, tls ? 8883 : 1883);
        var builder = new MqttClientOptionsBuilder().WithTcpServer(host, port)
            .WithClientId(s.String("clientId") ?? $"factoryos-spb-{asset.AssetCode}-{Guid.NewGuid():N}"[..40]).WithCleanSession();
        if (s.String("username") is { } user)
        {
            builder = builder.WithCredentials(user, s.String("password"));
        }
        if (tls)
        {
            builder = builder.WithTlsOptions(o => o.UseTls());
        }
        client.ApplicationMessageReceivedAsync += async e =>
        {
            var rebirth = Handle(e.ApplicationMessage.Topic, System.Buffers.BuffersExtensions.ToArray(e.ApplicationMessage.Payload), DateTime.UtcNow);
            if (rebirth is not null && client.IsConnected)
            {
                await client.PublishAsync(RebirthMessage(rebirth.Value.Group, rebirth.Value.Node));
            }
        };
        await client.ConnectAsync(builder.Build(), ct);
        var subscribe = new MqttClientSubscribeOptionsBuilder();
        foreach (var node in tags.Select(t => (t.Address.Group, t.Address.Node)).Distinct())
        {
            subscribe = subscribe.WithTopicFilter(f => f.WithTopic($"spBv1.0/{node.Group}/+/{node.Node}"))
                .WithTopicFilter(f => f.WithTopic($"spBv1.0/{node.Group}/+/{node.Node}/+"));
        }
        if (tags.Count > 0)
        {
            await client.SubscribeAsync(subscribe.Build(), ct);
        }
    }

    public static MqttApplicationMessage RebirthMessage(string group, string node) =>
        new MqttApplicationMessageBuilder().WithTopic($"spBv1.0/{group}/NCMD/{node}")
            .WithPayload(SparkplugCodec.Encode(new SpPayload((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null,
                [new SpMetric("Node Control/Rebirth", null, null, SparkplugCodec.Boolean, false, 1)])))
            .Build();

    /// <summary>Processes one message; returns the node to ask for a rebirth, if an unknown alias arrived.</summary>
    public (string Group, string Node)? Handle(string topic, byte[] payload, DateTime now)
    {
        var p = topic.Split('/');
        if (p.Length is < 4 or > 5 || p[0] != "spBv1.0")
        {
            return null;
        }
        var (group, type, node) = (p[1], p[2], p[3]);
        var source = p.Length == 5 ? $"{group}/{node}/{p[4]}" : $"{group}/{node}";
        if (type is "NDEATH" or "DDEATH")
        {
            dead[source] = now;
            if (type == "NDEATH")
            {
                foreach (var k in aliases.Keys.Where(k => k == source || k.StartsWith(source + "/", StringComparison.Ordinal)))
                {
                    dead[k] = now;
                }
            }
            return null;
        }
        if (type is not ("NBIRTH" or "DBIRTH" or "NDATA" or "DDATA"))
        {
            return null;
        }
        SpPayload data;
        try
        {
            data = SparkplugCodec.Decode(payload);
        }
        catch (FormatException)
        {
            return null;
        }
        var map = aliases.GetOrAdd(source, _ => new ConcurrentDictionary<ulong, string>());
        if (type.EndsWith("BIRTH", StringComparison.Ordinal))
        {
            map.Clear();
            dead.TryRemove(source, out _);
        }
        var unknownAlias = false;
        foreach (var m in data.Metrics)
        {
            var name = m.Name;
            if (name is not null && m.Alias is { } a)
            {
                map[a] = name;
            }
            else if (name is null && m.Alias is { } alias && !map.TryGetValue(alias, out name))
            {
                unknownAlias = true;
                continue;
            }
            if (name is null)
            {
                continue;
            }
            var ms = m.Timestamp ?? data.Timestamp;
            var ts = ms is { } t ? DateTimeOffset.FromUnixTimeMilliseconds((long)t).UtcDateTime : now;
            foreach (var (tag, address) in tags)
            {
                if (address.Source == source && address.Metric == name)
                {
                    latest[tag.TagId] = (m.Value, ts > now.AddMinutes(1) ? now : ts);
                }
            }
        }
        if (unknownAlias && (!rebirthAsked.TryGetValue($"{group}/{node}", out var asked) || now - asked > TimeSpan.FromMinutes(1)))
        {
            rebirthAsked[$"{group}/{node}"] = now;
            RebirthRequests++;
            return (group, node);
        }
        return null;
    }

    public Task<IReadOnlyList<RawValue>> ReadAsync(DateTime now, CancellationToken ct)
    {
        if (!client.IsConnected)
        {
            throw new InvalidOperationException("Lost the connection to the MQTT broker.");
        }
        return Task.FromResult(Snapshot(now));
    }

    /// <summary>The newest value of each tag; tags of a dead node or device, or silent for three minutes, read as offline.</summary>
    public IReadOnlyList<RawValue> Snapshot(DateTime now) =>
        [.. tags.Where(t => latest.ContainsKey(t.Tag.TagId)).Select(t =>
        {
            var v = latest[t.Tag.TagId];
            var isDead = dead.TryGetValue(t.Address.Source, out var at) && at >= v.Ts;
            return new RawValue(t.Tag.TagId, isDead ? null : v.Value,
                isDead || now - v.Ts > TimeSpan.FromSeconds(180) ? Quality.Offline : v.Value is null ? Quality.Bad : Quality.Good, isDead ? now : v.Ts);
        })];

    public async ValueTask DisposeAsync()
    {
        if (client.IsConnected)
        {
            await client.DisconnectAsync();
        }
        client.Dispose();
    }
}

public sealed class SparkplugConnectorFactory : IConnectorFactory
{
    public IReadOnlyCollection<string> Protocols { get; } = ["sparkplugb"];

    public IConnector Create(AssetBinding asset) => new SparkplugConnector(asset);
}
