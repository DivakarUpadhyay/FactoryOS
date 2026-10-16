using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Application.Robotics;
using Divakar.FactoryOS.Connectors.Drivers;
using Divakar.FactoryOS.Connectors.Simulation;
using Microsoft.Extensions.DependencyInjection;

namespace Divakar.FactoryOS.Connectors;

public static class DependencyInjection
{
    /// <summary>Every built-in driver. The ConnectorRegistry picks the right one by the asset's protocol.</summary>
    public static IServiceCollection AddConnectors(this IServiceCollection services)
    {
        services.AddSingleton<IConnectorFactory, SimulatorConnectorFactory>();
        services.AddSingleton<IConnectorFactory, ModbusConnectorFactory>();
        services.AddSingleton<IConnectorFactory, MqttConnectorFactory>();
        services.AddSingleton<IConnectorFactory, S7ConnectorFactory>();
        services.AddSingleton<IConnectorFactory, OpcUaConnectorFactory>();
        services.AddSingleton<IConnectorFactory>(_ => new MtConnectConnectorFactory(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }));
        services.AddSingleton<IOrderPublisher, Vda5050OrderPublisher>();
        services.AddSingleton<Divakar.FactoryOS.Application.Energy.IConnectorWriter, ModbusWriter>();
        services.AddSingleton<Divakar.FactoryOS.Application.Energy.IConnectorWriter, MqttWriter>();
        return services;
    }
}
