using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Domain.Plant;

namespace Divakar.FactoryOS.Connectors.Drivers;

/// <summary>
/// Universal Robots Real-Time Data Exchange (RTDE), protocol version 2, port 30004. Each tag's address is an RTDE output
/// variable, with an index for vectors: "robot_mode", "runtime_state", "actual_TCP_speed[0]", "joint_temperatures[3]",
/// "actual_current[1]". The connector negotiates the version, sets up one output recipe for all variables at a frequency that
/// matches the poll interval, starts the stream and keeps the newest package. Packages: uint16 size, uint8 type, payload (big-endian).
/// </summary>
public sealed partial class UrRtdeConnector(AssetBinding asset) : IConnector
{
    public const byte RequestProtocolVersion = 86, ControlPackageSetupOutputs = 79, ControlPackageStart = 83, ControlPackagePause = 80,
        DataPackage = 85, TextMessage = 77;

    private readonly TcpClient tcp = new();
    private NetworkStream? stream;
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<string, (double[] Values, DateTime Ts)> latest = new();
    private readonly List<(TagBinding Tag, string Variable, int Index)> tags =
        [.. asset.Tags.Where(t => !string.IsNullOrWhiteSpace(t.Address)).Select(t => Parse(t))];
    private string[] variables = [];
    private string[] types = [];
    private byte recipe;
    private Task? receiving;

    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_]*)(?:\[(\d)\])?$")]
    private static partial Regex AddressPattern();

    private static (TagBinding, string, int) Parse(TagBinding t)
    {
        var m = AddressPattern().Match(t.Address!.Trim());
        if (!m.Success)
        {
            throw new FormatException($"RTDE address '{t.Address}' must be a variable name, with [index] for vectors.");
        }
        return (t, m.Groups[1].Value, m.Groups[2].Success ? int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 0);
    }

    public static int Size(string type) => type switch
    {
        "BOOL" or "UINT8" => 1, "UINT32" or "INT32" => 4, "UINT64" or "DOUBLE" => 8, "VECTOR3D" => 24, "VECTOR6D" => 48,
        "VECTOR6INT32" or "VECTOR6UINT32" => 24, _ => throw new FormatException($"Unknown RTDE type {type}."),
    };

    public static byte[] Package(byte type, ReadOnlySpan<byte> payload)
    {
        var b = new byte[3 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)b.Length);
        b[2] = type;
        payload.CopyTo(b.AsSpan(3));
        return b;
    }

    private async Task<(byte Type, byte[] Payload)> ReceiveAsync(CancellationToken ct)
    {
        var head = new byte[3];
        await stream!.ReadExactlyAsync(head, ct);
        var size = BinaryPrimitives.ReadUInt16BigEndian(head);
        if (size < 3)
        {
            throw new IOException("Malformed RTDE package.");
        }
        var payload = new byte[size - 3];
        await stream.ReadExactlyAsync(payload, ct);
        return (head[2], payload);
    }

    /// <summary>Waits for a reply of one type, skipping text messages and data packages that arrive in between.</summary>
    private async Task<byte[]> ExpectAsync(byte type, CancellationToken ct)
    {
        for (var i = 0; i < 50; i++)
        {
            var (t, p) = await ReceiveAsync(ct);
            if (t == type)
            {
                return p;
            }
        }
        throw new IOException($"No RTDE reply of type '{(char)type}'.");
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        var (host, port) = Endpoints.HostPort(asset.Endpoint, 30004);
        await tcp.ConnectAsync(host, port, ct);
        tcp.NoDelay = true;
        stream = tcp.GetStream();

        var version = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(version, 2);
        await stream.WriteAsync(Package(RequestProtocolVersion, version), ct);
        if ((await ExpectAsync(RequestProtocolVersion, ct)) is not [1, ..])
        {
            throw new IOException("The robot did not accept RTDE protocol version 2 (needs PolyScope 3.4 / 5.0 or newer).");
        }

        variables = [.. tags.Select(t => t.Variable).Distinct()];
        var frequency = Math.Clamp(1000.0 / Math.Max(1, asset.PollMs), 1, 125);
        var names = Encoding.ASCII.GetBytes(string.Join(',', variables));
        var setup = new byte[8 + names.Length];
        BinaryPrimitives.WriteDoubleBigEndian(setup, frequency);
        names.CopyTo(setup, 8);
        await stream.WriteAsync(Package(ControlPackageSetupOutputs, setup), ct);
        var reply = await ExpectAsync(ControlPackageSetupOutputs, ct);
        recipe = reply[0];
        types = Encoding.ASCII.GetString(reply, 1, reply.Length - 1).Split(',');
        var missing = variables.Where((v, i) => i >= types.Length || types[i] is "NOT_FOUND" or "IN_USE").ToList();
        if (missing.Count > 0)
        {
            throw new FormatException($"The robot does not offer RTDE output(s): {string.Join(", ", missing)}.");
        }

        await stream.WriteAsync(Package(ControlPackageStart, []), ct);
        if ((await ExpectAsync(ControlPackageStart, ct)) is not [1, ..])
        {
            throw new IOException("The robot refused to start the RTDE stream.");
        }
        receiving = Task.Run(() => LoopAsync(stop.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (t, p) = await ReceiveAsync(ct);
                if (t == DataPackage && p.Length > 0 && p[0] == recipe)
                {
                    Apply(Decode(types, p.AsSpan(1)), DateTime.UtcNow);
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested || !tcp.Connected)
        {
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Decodes a data package payload (after the recipe id) into one array of numbers per variable.</summary>
    public static double[][] Decode(IReadOnlyList<string> types, ReadOnlySpan<byte> p)
    {
        var result = new double[types.Count][];
        var pos = 0;
        for (var i = 0; i < types.Count; i++)
        {
            var t = types[i];
            var span = p.Slice(pos, Size(t));
            pos += Size(t);
            result[i] = t switch
            {
                "BOOL" or "UINT8" => [span[0]],
                "UINT32" => [BinaryPrimitives.ReadUInt32BigEndian(span)],
                "INT32" => [BinaryPrimitives.ReadInt32BigEndian(span)],
                "UINT64" => [BinaryPrimitives.ReadUInt64BigEndian(span)],
                "DOUBLE" => [BinaryPrimitives.ReadDoubleBigEndian(span)],
                _ => Vector(t, span),
            };
        }
        return result;
    }

    private static double[] Vector(string type, ReadOnlySpan<byte> span)
    {
        var width = type is "VECTOR3D" or "VECTOR6D" ? 8 : 4;
        var v = new double[span.Length / width];
        for (var k = 0; k < v.Length; k++)
        {
            var part = span[(k * width)..];
            v[k] = type switch
            {
                "VECTOR6INT32" => BinaryPrimitives.ReadInt32BigEndian(part),
                "VECTOR6UINT32" => BinaryPrimitives.ReadUInt32BigEndian(part),
                _ => BinaryPrimitives.ReadDoubleBigEndian(part),
            };
        }
        return v;
    }

    public void Apply(double[][] values, DateTime now)
    {
        for (var i = 0; i < variables.Length && i < values.Length; i++)
        {
            latest[variables[i]] = (values[i], now);
        }
    }

    public Task<IReadOnlyList<RawValue>> ReadAsync(DateTime now, CancellationToken ct)
    {
        if (!tcp.Connected || receiving is { IsCompleted: true })
        {
            throw new InvalidOperationException("Lost the RTDE connection to the robot.");
        }
        IReadOnlyList<RawValue> list = [.. tags.Where(t => latest.ContainsKey(t.Variable)).Select(t =>
        {
            var v = latest[t.Variable];
            return t.Index < v.Values.Length
                ? new RawValue(t.Tag.TagId, v.Values[t.Index], now - v.Ts > TimeSpan.FromSeconds(10) ? Quality.Offline : Quality.Good, v.Ts)
                : new RawValue(t.Tag.TagId, null, Quality.Bad, v.Ts);
        })];
        return Task.FromResult(list);
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try
        {
            if (stream is not null && tcp.Connected)
            {
                await stream.WriteAsync(Package(ControlPackagePause, []));
            }
        }
        catch (IOException)
        {
        }
        if (receiving is not null)
        {
            await Task.WhenAny(receiving, Task.Delay(2000));
        }
        tcp.Dispose();
        stop.Dispose();
    }
}

public sealed class UrRtdeConnectorFactory : IConnectorFactory
{
    public IReadOnlyCollection<string> Protocols { get; } = ["ur-rtde"];

    public IConnector Create(AssetBinding asset) => new UrRtdeConnector(asset);
}
