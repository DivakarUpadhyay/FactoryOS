using Dapper;
using Divakar.FactoryOS.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Divakar.FactoryOS.UnitTests;

public sealed class InfrastructureSetupTests
{
    [Fact]
    public void Registering_the_infrastructure_installs_the_date_handler_before_any_query()
    {
        // regression: work orders and NCRs (DateOnly columns) failed with 500 when read before any asset after a restart
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:FactoryOS"] = "Server=.;Database=x" }).Build();
        new ServiceCollection().AddInfrastructure(config);
        Assert.True(SqlMapper.HasTypeHandler(typeof(DateOnly)));
    }
}
