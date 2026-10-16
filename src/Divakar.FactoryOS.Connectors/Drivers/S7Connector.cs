using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Domain.Plant;
using S7.Net;

namespace Divakar.FactoryOS.Connectors.Drivers;

/// <summary>
/// Siemens S7 (ISO-on-TCP, port 102). Endpoint "plc-host"; settings {"cpu": "S71500", "rack": 0, "slot": 1}.
/// Tag addresses: "DB1.DBD0:real", "DB1.DBW4:int", "DB1.DBX0.0", "MW10". The PLC must allow PUT/GET access.
/// </summary>
public sealed class S7Connector(AssetBinding asset) : IConnector
{
    private Plc? plc;

    public async Task ConnectAsync(CancellationToken ct)
    {
        var s = new Settings(asset.Settings);
        var (host, _) = Endpoints.HostPort(asset.Endpoint, 102);
        var cpu = Enum.TryParse<CpuType>(s.String("cpu") ?? "S71500", true, out var c) ? c : CpuType.S71500;
        plc = new Plc(cpu, host, (short)s.Int("rack", 0), (short)s.Int("slot", cpu is CpuType.S7300 or CpuType.S7400 ? 2 : 1));
        await plc.OpenAsync(ct);
    }

    public async Task<IReadOnlyList<RawValue>> ReadAsync(DateTime now, CancellationToken ct)
    {
        if (plc is null || !plc.IsConnected)
        {
            throw new InvalidOperationException("Not connected to the PLC.");
        }
        var result = new List<RawValue>(asset.Tags.Count);
        foreach (var tag in asset.Tags)
        {
            S7Address address;
            try
            {
                address = S7Address.Parse(tag.Address ?? "");
            }
            catch (FormatException)
            {
                result.Add(new RawValue(tag.TagId, null, Quality.Bad, now));
                continue;
            }
            var raw = await plc.ReadAsync(address.Address, ct);
            result.Add(new RawValue(tag.TagId, Convert(raw, address.Type), Quality.Good, now));
        }
        return result;
    }

    /// <summary>S7.Net returns raw words; turn them into the declared type.</summary>
    public static double? Convert(object? raw, string type) => (raw, type) switch
    {
        (null, _) => null,
        (bool b, _) => b ? 1 : 0,
        (uint u, "real") => BitConverter.UInt32BitsToSingle(u),
        (uint u, "dint") => unchecked((int)u),
        (uint u, _) => u,
        (ushort w, "int") => unchecked((short)w),
        (ushort w, _) => w,
        (byte b, _) => b,
        _ => System.Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture),
    };

    public ValueTask DisposeAsync()
    {
        plc?.Close();
        return ValueTask.CompletedTask;
    }
}

public sealed class S7ConnectorFactory : IConnectorFactory
{
    public IReadOnlyCollection<string> Protocols { get; } = ["s7"];

    public IConnector Create(AssetBinding asset) => new S7Connector(asset);
}
