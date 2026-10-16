using Divakar.FactoryOS.Application.Connectivity;

namespace Divakar.FactoryOS.Connectors.Simulation;

/// <summary>The "simulator" protocol: every read advances the asset's model to the current time.</summary>
public sealed class SimulatorConnector(AssetBinding asset) : IConnector
{
    private FactorySimulator? sim;

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<Application.Connectivity.RawValue>> ReadAsync(DateTime now, CancellationToken ct)
    {
        sim ??= new FactorySimulator(asset, now);
        return Task.FromResult(sim.Step(now));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class SimulatorConnectorFactory : IConnectorFactory
{
    public IReadOnlyCollection<string> Protocols { get; } = ["simulator"];

    public IConnector Create(AssetBinding asset) => new SimulatorConnector(asset);
}
