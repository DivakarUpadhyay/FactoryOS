using Divakar.FactoryOS.Domain.Plant;

namespace Divakar.FactoryOS.Application.Connectivity;

/// <summary>One tag as a connector sees it. Profile is the sensor type's simulation behaviour (plant.SensorTypes.SimProfile).</summary>
public sealed record TagBinding(Guid TagId, string Code, string SensorTypeCode, string Profile, string? Address, double Scale, double Offset,
    double Deadband, double? MinValue, double? MaxValue);

/// <summary>Everything a connector needs to talk to one asset.</summary>
public sealed record AssetBinding(Guid TenantId, Guid AssetId, string AssetCode, string AssetTypeCode, string ProtocolCode,
    string? Endpoint, string? Settings, int PollMs, decimal? RatedPowerKw, IReadOnlyList<TagBinding> Tags, bool DerivedState = false);

/// <summary>A raw value read from a device, before scaling. Null value with a bad quality means "could not read".</summary>
public sealed record RawValue(Guid TagId, double? Value, byte Quality, DateTime Ts);

/// <summary>A scaled value ready to store. Store = also append to the history (it changed or the heartbeat is due).</summary>
public sealed record ReadingRow(Guid TenantId, Guid TagId, DateTime Ts, double? Value, byte Quality, bool Store);

/// <summary>
/// Talks to one asset. Polling protocols read on demand; push protocols (MQTT) keep the newest values they
/// received and hand them over on each read, so the engine treats every protocol the same way.
/// </summary>
public interface IConnector : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct);
    Task<IReadOnlyList<RawValue>> ReadAsync(DateTime now, CancellationToken ct);
}

public interface IConnectorFactory
{
    /// <summary>Protocol codes (plant.Protocols) this factory builds connectors for.</summary>
    IReadOnlyCollection<string> Protocols { get; }

    IConnector Create(AssetBinding asset);
}

public sealed class ConnectorRegistry(IEnumerable<IConnectorFactory> factories)
{
    private readonly Dictionary<string, IConnectorFactory> byProtocol =
        factories.SelectMany(f => f.Protocols.Select(p => (p, f))).ToDictionary(x => x.p, x => x.f);

    public bool Supports(string protocol) => byProtocol.ContainsKey(protocol);

    public IReadOnlyCollection<string> Protocols => byProtocol.Keys;

    public IConnector Create(AssetBinding asset) =>
        byProtocol.TryGetValue(asset.ProtocolCode, out var f)
            ? f.Create(asset)
            : throw new NotSupportedException($"No built-in driver for protocol '{asset.ProtocolCode}'.");
}

public sealed record AssetHealth(Guid AssetId, string Status, DateTime? LastSeenAt, string? LastError);

/// <summary>Storage used by the ingestion worker. Runs with the system connection: it serves every tenant.</summary>
public interface IIngestionStore
{
    Task<IReadOnlyList<AssetBinding>> ActiveBindingsAsync(IReadOnlyCollection<string> protocols, CancellationToken ct);
    Task WriteAsync(IReadOnlyList<ReadingRow> rows, CancellationToken ct);
    Task UpdateHealthAsync(IReadOnlyList<AssetHealth> health, CancellationToken ct);
}
