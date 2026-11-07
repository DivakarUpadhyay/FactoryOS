using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Domain.Plant;

namespace Divakar.FactoryOS.Connectors.Drivers;

/// <summary>
/// A SECS-II data item (SEMI E5): format code, and either child items (list) or raw bytes. Item header = format byte
/// (code &lt;&lt; 2 | number of length bytes), then the length (bytes, or items for a list), big-endian.
/// </summary>
public sealed class SecsItem
{
    public const byte L = 0x00, B = 0x08, Boolean = 0x09, A = 0x10, J = 0x11, I8 = 0x18, I1 = 0x19, I2 = 0x1A, I4 = 0x1C, F8 = 0x20, F4 = 0x24,
        U8 = 0x28, U1 = 0x29, U2 = 0x2A, U4 = 0x2C;

    public byte Format { get; }
    public IReadOnlyList<SecsItem> Items { get; } = [];
    public byte[] Data { get; } = [];

    private SecsItem(byte format, IReadOnlyList<SecsItem>? items, byte[]? data)
    {
        Format = format;
        Items = items ?? [];
        Data = data ?? [];
    }

    public static SecsItem List(params SecsItem[] items) => new(L, items, null);
    public static SecsItem Ascii(string s) => new(A, null, Encoding.ASCII.GetBytes(s));
    public static SecsItem Binary(params byte[] b) => new(B, null, b);

    public static SecsItem U4Of(params uint[] v)
    {
        var b = new byte[v.Length * 4];
        for (var i = 0; i < v.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(i * 4), v[i]);
        }
        return new(U4, null, b);
    }

    public static SecsItem F8Of(params double[] v)
    {
        var b = new byte[v.Length * 8];
        for (var i = 0; i < v.Length; i++)
        {
            BinaryPrimitives.WriteDoubleBigEndian(b.AsSpan(i * 8), v[i]);
        }
        return new(F8, null, b);
    }

    public static SecsItem Raw(byte format, byte[] data) => new(format, null, data);

    public static int Width(byte format) => format switch
    {
        I8 or U8 or F8 => 8, I4 or U4 or F4 => 4, I2 or U2 => 2, _ => 1,
    };

    public void Write(Stream s)
    {
        var count = Format == L ? Items.Count : Data.Length;
        var lenBytes = count <= 0xFF ? 1 : count <= 0xFFFF ? 2 : 3;
        s.WriteByte((byte)((Format << 2) | lenBytes));
        for (var i = lenBytes - 1; i >= 0; i--)
        {
            s.WriteByte((byte)(count >> (8 * i)));
        }
        if (Format == L)
        {
            foreach (var item in Items)
            {
                item.Write(s);
            }
        }
        else
        {
            s.Write(Data);
        }
    }

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        Write(ms);
        return ms.ToArray();
    }

    public static SecsItem Read(ReadOnlySpan<byte> data, ref int pos)
    {
        var head = data[pos++];
        var format = (byte)(head >> 2);
        var lenBytes = head & 3;
        if (lenBytes == 0)
        {
            throw new FormatException("SECS-II item with no length bytes.");
        }
        var count = 0;
        for (var i = 0; i < lenBytes; i++)
        {
            count = (count << 8) | data[pos++];
        }
        if (format == L)
        {
            var items = new List<SecsItem>(count);
            for (var i = 0; i < count; i++)
            {
                items.Add(Read(data, ref pos));
            }
            return new SecsItem(L, items, null);
        }
        var bytes = data.Slice(pos, count).ToArray();
        pos += count;
        return new SecsItem(format, null, bytes);
    }

    /// <summary>The item as a number: the first value of a numeric array, a boolean as 0/1, ASCII parsed; null otherwise.</summary>
    public double? ToDouble()
    {
        if (Format == L || Data.Length == 0)
        {
            return null;
        }
        var d = Data.AsSpan();
        return Format switch
        {
            B or U1 => d[0],
            Boolean => d[0] != 0 ? 1 : 0,
            I1 => (sbyte)d[0],
            I2 when d.Length >= 2 => BinaryPrimitives.ReadInt16BigEndian(d),
            U2 when d.Length >= 2 => BinaryPrimitives.ReadUInt16BigEndian(d),
            I4 when d.Length >= 4 => BinaryPrimitives.ReadInt32BigEndian(d),
            U4 when d.Length >= 4 => BinaryPrimitives.ReadUInt32BigEndian(d),
            I8 when d.Length >= 8 => BinaryPrimitives.ReadInt64BigEndian(d),
            U8 when d.Length >= 8 => BinaryPrimitives.ReadUInt64BigEndian(d),
            F4 when d.Length >= 4 => BinaryPrimitives.ReadSingleBigEndian(d),
            F8 when d.Length >= 8 => BinaryPrimitives.ReadDoubleBigEndian(d),
            A or J => double.TryParse(Encoding.ASCII.GetString(Data).Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null,
            _ => null,
        };
    }
}

/// <summary>An HSMS message (SEMI E37): 4-byte length, 10-byte header, SECS-II body for data messages.</summary>
public sealed record HsmsMessage(ushort SessionId, byte Stream, byte Function, bool WaitBit, byte SType, uint SystemBytes, SecsItem? Body)
{
    public const byte Data = 0, SelectReq = 1, SelectRsp = 2, DeselectReq = 3, DeselectRsp = 4, LinktestReq = 5, LinktestRsp = 6, RejectReq = 7,
        SeparateReq = 9;

    public byte[] ToBytes()
    {
        var body = Body?.ToBytes() ?? [];
        var b = new byte[14 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)(10 + body.Length));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), SessionId);
        b[6] = (byte)((WaitBit ? 0x80 : 0) | (Stream & 0x7F));
        b[7] = Function;
        b[8] = 0;
        b[9] = SType;
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(10), SystemBytes);
        body.CopyTo(b, 14);
        return b;
    }

    public static async Task<HsmsMessage> ReadAsync(Stream s, CancellationToken ct)
    {
        var len = new byte[4];
        await s.ReadExactlyAsync(len, ct);
        var n = BinaryPrimitives.ReadUInt32BigEndian(len);
        if (n is < 10 or > 16 * 1024 * 1024)
        {
            throw new IOException($"HSMS message length {n} is not valid.");
        }
        var m = new byte[n];
        await s.ReadExactlyAsync(m, ct);
        SecsItem? body = null;
        if (n > 10)
        {
            var pos = 10;
            body = SecsItem.Read(m, ref pos);
        }
        return new HsmsMessage(BinaryPrimitives.ReadUInt16BigEndian(m), (byte)(m[2] & 0x7F), m[3], (m[2] & 0x80) != 0, m[5],
            BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(6)), body);
    }
}

/// <summary>
/// SECS/GEM equipment over HSMS-SS, the host in active mode. Endpoint "tool:5000"; settings {"deviceId": 0, "t3": 45, "t6": 5,
/// "linktest": 60}. Each tag's address is a status variable id (SVID), e.g. "1001" or "svid:1001". On connect: Select.req, then
/// S1F13 (establish communication, COMMACK must be 0). Each read sends S1F3 (selected equipment status request) for all SVIDs and
/// takes the S1F4 values in order. Equipment-initiated messages that want a reply get one (S5F1 → S5F2, S6F11 → S6F12, others an
/// F0 abort), and link tests are answered, so the tool never times the host out.
/// </summary>
public sealed class SecsGemConnector(AssetBinding asset) : IConnector
{
    private readonly TcpClient tcp = new();
    private NetworkStream? stream;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<HsmsMessage>> pending = new();
    private readonly CancellationTokenSource stop = new();
    private readonly List<(TagBinding Tag, uint Svid)> tags =
        [.. asset.Tags.Where(t => !string.IsNullOrWhiteSpace(t.Address)).Select(t => (t, ParseSvid(t.Address!)))];
    private ushort deviceId;
    private TimeSpan t3, t6;
    private int system;
    private Task? receiving;
    private DateTime nextLinktest;
    private TimeSpan linktest;

    public static uint ParseSvid(string address)
    {
        var a = address.Trim();
        if (a.StartsWith("svid:", StringComparison.OrdinalIgnoreCase))
        {
            a = a[5..];
        }
        return uint.TryParse(a, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : throw new FormatException($"SECS/GEM address '{address}' must be a status variable id (SVID), e.g. 1001.");
    }

    private uint NextSystem() => (uint)Interlocked.Increment(ref system);

    private async Task SendAsync(HsmsMessage m, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct);
        try
        {
            await stream!.WriteAsync(m.ToBytes(), ct);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async Task<HsmsMessage> RequestAsync(HsmsMessage m, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<HsmsMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[m.SystemBytes] = tcs;
        try
        {
            await SendAsync(m, ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await using var _ = cts.Token.Register(() => tcs.TrySetException(new TimeoutException(
                m.SType == HsmsMessage.Data ? $"T3 reply timeout for S{m.Stream}F{m.Function}." : "T6 control timeout.")));
            return await tcs.Task;
        }
        finally
        {
            pending.TryRemove(m.SystemBytes, out _);
        }
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        var s = new Settings(asset.Settings);
        deviceId = (ushort)s.Int("deviceId", 0);
        t3 = TimeSpan.FromSeconds(s.Int("t3", 45));
        t6 = TimeSpan.FromSeconds(s.Int("t6", 5));
        linktest = TimeSpan.FromSeconds(s.Int("linktest", 60));
        var (host, port) = Endpoints.HostPort(asset.Endpoint, 5000);
        await tcp.ConnectAsync(host, port, ct);
        tcp.NoDelay = true;
        stream = tcp.GetStream();
        receiving = Task.Run(() => LoopAsync(stop.Token));

        var select = await RequestAsync(new HsmsMessage(0xFFFF, 0, 0, false, HsmsMessage.SelectReq, NextSystem(), null), t6, ct);
        if (select.SType != HsmsMessage.SelectRsp || select.Function != 0)
        {
            throw new IOException($"The equipment refused the HSMS select (status {select.Function}).");
        }
        var s1f14 = await RequestAsync(new HsmsMessage(deviceId, 1, 13, true, HsmsMessage.Data, NextSystem(), SecsItem.List()), t3, ct);
        var commack = s1f14.Body is { Format: SecsItem.L, Items.Count: > 0 } b ? b.Items[0].ToDouble() : null;
        if (s1f14.Stream != 1 || s1f14.Function != 14 || commack is not 0)
        {
            throw new IOException($"S1F13 establish communication was not accepted (COMMACK {commack?.ToString() ?? "missing"}).");
        }
        nextLinktest = DateTime.UtcNow + linktest;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var m = await HsmsMessage.ReadAsync(stream!, ct);
                var isReply = m.SType is HsmsMessage.SelectRsp or HsmsMessage.LinktestRsp or HsmsMessage.DeselectRsp
                    || (m.SType == HsmsMessage.Data && m.Function % 2 == 0);
                if (isReply && pending.TryRemove(m.SystemBytes, out var waiter))
                {
                    waiter.TrySetResult(m);
                    continue;
                }
                switch (m.SType)
                {
                    case HsmsMessage.LinktestReq:
                        await SendAsync(m with { SType = HsmsMessage.LinktestRsp, Body = null }, ct);
                        break;
                    case HsmsMessage.SelectReq:
                        await SendAsync(m with { SType = HsmsMessage.SelectRsp, Function = 0, Body = null }, ct);
                        break;
                    case HsmsMessage.SeparateReq:
                        tcp.Close();
                        return;
                    case HsmsMessage.Data when m.WaitBit && m.Function % 2 == 1:
                        var reply = (m.Stream, m.Function) switch
                        {
                            (5, 1) => m with { Function = 2, WaitBit = false, Body = SecsItem.Binary(0) },
                            (6, 11) => m with { Function = 12, WaitBit = false, Body = SecsItem.Binary(0) },
                            (1, 1) => m with { Function = 2, WaitBit = false, Body = SecsItem.List() },
                            _ => m with { Function = 0, WaitBit = false, Body = null },
                        };
                        await SendAsync(reply, ct);
                        break;
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested || !tcp.Connected)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            foreach (var p in pending.Values)
            {
                p.TrySetException(new IOException("The HSMS connection closed."));
            }
        }
    }

    public async Task<IReadOnlyList<RawValue>> ReadAsync(DateTime now, CancellationToken ct)
    {
        if (!tcp.Connected || receiving is { IsCompleted: true })
        {
            throw new InvalidOperationException("Lost the HSMS connection to the equipment.");
        }
        if (DateTime.UtcNow >= nextLinktest)
        {
            await RequestAsync(new HsmsMessage(0xFFFF, 0, 0, false, HsmsMessage.LinktestReq, NextSystem(), null), t6, ct);
            nextLinktest = DateTime.UtcNow + linktest;
        }
        if (tags.Count == 0)
        {
            return [];
        }
        var svids = tags.Select(t => t.Svid).Distinct().ToArray();
        var reply = await RequestAsync(new HsmsMessage(deviceId, 1, 3, true, HsmsMessage.Data, NextSystem(),
            SecsItem.List([.. svids.Select(v => SecsItem.U4Of(v))])), t3, ct);
        if (reply.Stream != 1 || reply.Function != 4 || reply.Body is not { Format: SecsItem.L } list)
        {
            throw new IOException(reply.Function == 0 ? "The equipment aborted S1F3." : $"Unexpected reply S{reply.Stream}F{reply.Function} to S1F3.");
        }
        var values = svids.Select((v, i) => (v, i < list.Items.Count ? list.Items[i] : null)).ToDictionary(x => x.v, x => x.Item2);
        return [.. tags.Select(t => values[t.Svid] is { } item && item.ToDouble() is { } d
            ? new RawValue(t.Tag.TagId, d, Quality.Good, now)
            : new RawValue(t.Tag.TagId, null, Quality.Bad, now))];   // an unknown SVID comes back as an empty item
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try
        {
            if (stream is not null && tcp.Connected)
            {
                await stream.WriteAsync(new HsmsMessage(0xFFFF, 0, 0, false, HsmsMessage.SeparateReq, NextSystem(), null).ToBytes());
            }
        }
        catch (IOException)
        {
        }
        tcp.Dispose();
        if (receiving is not null)
        {
            await Task.WhenAny(receiving, Task.Delay(2000));
        }
        stop.Dispose();
        writeLock.Dispose();
    }
}

public sealed class SecsGemConnectorFactory : IConnectorFactory
{
    public IReadOnlyCollection<string> Protocols { get; } = ["secs-gem"];

    public IConnector Create(AssetBinding asset) => new SecsGemConnector(asset);
}
