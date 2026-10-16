using System.Globalization;
using System.Xml.Linq;
using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Domain.Plant;

namespace Divakar.FactoryOS.Connectors.Drivers;

/// <summary>
/// MTConnect agent (CNC machine tools). Endpoint "http://agent:5000" or with a device path
/// "http://agent:5000/Mazak01"; the connector reads {endpoint}/current. Tag address = the data item id.
/// Text values are mapped: EXECUTION ACTIVE → running, READY/STOPPED → idle/stopped, AVAILABILITY UNAVAILABLE → offline.
/// </summary>
public sealed class MtConnectConnector(AssetBinding asset, HttpClient http) : IConnector
{
    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task<IReadOnlyList<RawValue>> ReadAsync(DateTime now, CancellationToken ct)
    {
        var url = (asset.Endpoint ?? throw new FormatException("The asset has no endpoint.")).TrimEnd('/') + "/current";
        var xml = await http.GetStringAsync(url, ct);
        var values = Parse(xml);
        return asset.Tags.Select(t => values.TryGetValue(t.Address ?? "", out var v)
            ? new RawValue(t.TagId, v, v is null ? Quality.Bad : Quality.Good, now)
            : new RawValue(t.TagId, null, Quality.Bad, now)).ToList();
    }

    /// <summary>dataItemId → numeric value from an MTConnectStreams document.</summary>
    public static Dictionary<string, double?> Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        var result = new Dictionary<string, double?>();
        foreach (var el in doc.Descendants().Where(e => e.Attribute("dataItemId") is not null))
        {
            var text = el.Value.Trim();
            double? value = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d
                : text.ToUpperInvariant() switch
                {
                    "ACTIVE" => MachineStates.Running,
                    "READY" or "INTERRUPTED" or "FEED_HOLD" => MachineStates.Idle,
                    "STOPPED" or "PROGRAM_STOPPED" or "PROGRAM_COMPLETED" => MachineStates.Stopped,
                    "AVAILABLE" => 1,
                    "UNAVAILABLE" => null,
                    "ON" or "TRUE" => 1,
                    "OFF" or "FALSE" => 0,
                    _ => null,
                };
            result[el.Attribute("dataItemId")!.Value] = value;
        }
        return result;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class MtConnectConnectorFactory(HttpClient http) : IConnectorFactory
{
    public IReadOnlyCollection<string> Protocols { get; } = ["mtconnect"];

    public IConnector Create(AssetBinding asset) => new MtConnectConnector(asset, http);
}
