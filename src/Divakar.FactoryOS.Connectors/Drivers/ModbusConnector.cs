using System.Net;
using System.Net.Sockets;
using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Domain.Plant;
using FluentModbus;

namespace Divakar.FactoryOS.Connectors.Drivers;

/// <summary>
/// Modbus TCP master. Endpoint "host:502"; settings {"unitId": 1}. Tags whose registers sit next to each other in the
/// same area are read in one request (up to 120 registers), so a meter with 20 values costs a few round trips, not 20.
/// </summary>
public sealed class ModbusConnector(AssetBinding asset) : IConnector
{
    private readonly ModbusTcpClient client = new() { ReadTimeout = 5000, WriteTimeout = 5000 };
    private readonly byte unitId = (byte)new Settings(asset.Settings).Int("unitId", 1);
    private readonly List<(TagBinding Tag, ModbusAddress? Address, string? Error)> tags = asset.Tags.Select(t =>
    {
        try
        {
            return (t, (ModbusAddress?)ModbusAddress.Parse(t.Address ?? ""), (string?)null);
        }
        catch (FormatException ex)
        {
            return (t, null, ex.Message);
        }
    }).ToList();

    public async Task ConnectAsync(CancellationToken ct)
    {
        var (host, port) = Endpoints.HostPort(asset.Endpoint, 502);
        var ip = IPAddress.TryParse(host, out var parsed) ? parsed
            : (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct)).First();
        await Task.Run(() => client.Connect(new IPEndPoint(ip, port), ModbusEndianness.BigEndian), ct);
    }

    public Task<IReadOnlyList<RawValue>> ReadAsync(DateTime now, CancellationToken ct) => Task.Run<IReadOnlyList<RawValue>>(() =>
    {
        var result = new List<RawValue>(tags.Count);
        foreach (var bad in tags.Where(t => t.Address is null))
        {
            result.Add(new RawValue(bad.Tag.TagId, null, Quality.Bad, now));
        }
        foreach (var block in Blocks(tags.Where(t => t.Address is not null).Select(t => (t.Tag, t.Address!)).ToList()))
        {
            ct.ThrowIfCancellationRequested();
            var first = block[0].Address;
            var start = block.Min(b => b.Address.Offset);
            var end = block.Max(b => b.Address.Offset + b.Address.Registers);
            switch (first.Area)
            {
                case ModbusArea.HoldingRegister or ModbusArea.InputRegister:
                    var regs = first.Area == ModbusArea.HoldingRegister
                        ? client.ReadHoldingRegisters<ushort>(unitId, start, end - start).ToArray()
                        : client.ReadInputRegisters<ushort>(unitId, start, end - start).ToArray();
                    foreach (var (tag, a) in block)
                    {
                        result.Add(new RawValue(tag.TagId, a.Decode(regs.AsSpan(a.Offset - start)), Quality.Good, now));
                    }
                    break;
                default:
                    var bits = first.Area == ModbusArea.Coil
                        ? client.ReadCoils(unitId, start, end - start).ToArray()
                        : client.ReadDiscreteInputs(unitId, start, end - start).ToArray();
                    foreach (var (tag, a) in block)
                    {
                        var i = a.Offset - start;
                        result.Add(new RawValue(tag.TagId, (bits[i / 8] >> (i % 8) & 1) == 1 ? 1 : 0, Quality.Good, now));
                    }
                    break;
            }
        }
        return result;
    }, ct);

    /// <summary>Groups tags of one area whose registers are close enough to read together.</summary>
    public static List<List<(TagBinding Tag, ModbusAddress Address)>> Blocks(List<(TagBinding Tag, ModbusAddress Address)> items, int maxSpan = 120)
    {
        var blocks = new List<List<(TagBinding, ModbusAddress)>>();
        foreach (var area in items.GroupBy(i => i.Address.Area))
        {
            List<(TagBinding, ModbusAddress)>? current = null;
            int start = 0;
            foreach (var item in area.OrderBy(i => i.Address.Offset))
            {
                var end = item.Address.Offset + item.Address.Registers;
                if (current is null || end - start > maxSpan)
                {
                    current = [];
                    blocks.Add(current);
                    start = item.Address.Offset;
                }
                current.Add(item);
            }
        }
        return blocks;
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class ModbusConnectorFactory : IConnectorFactory
{
    public IReadOnlyCollection<string> Protocols { get; } = ["modbus-tcp"];

    public IConnector Create(AssetBinding asset) => new ModbusConnector(asset);
}
