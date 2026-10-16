using Divakar.FactoryOS.Application.Production;

namespace Divakar.FactoryOS.UnitTests;

public sealed class OeeTests
{
    [Fact]
    public void Oee_is_availability_times_performance_times_quality()
    {
        // 6 h running, 2 h stopped; ideal 30 s; 600 parts of which 30 scrap
        var (oee, planned) = ProductionService.Calculate(run: 6 * 3600, idle: 0, stopped: 2 * 3600, fault: 0, plannedDown: 0, good: 570, scrap: 30, idealCycleSec: 30);
        Assert.Equal(8 * 3600, planned);
        Assert.Equal(75.0, oee.Availability);
        Assert.Equal(83.3, oee.Performance);   // 30 s × 600 / 21 600 s
        Assert.Equal(95.0, oee.Quality);
        Assert.Equal(59.4, oee.Value);
    }

    [Fact]
    public void Planned_stops_do_not_lower_availability()
    {
        var (oee, planned) = ProductionService.Calculate(run: 3600, idle: 1800, stopped: 0, fault: 0, plannedDown: 1800, good: 100, scrap: 0, idealCycleSec: 36);
        Assert.Equal(3600, planned);
        Assert.Equal(100.0, oee.Availability);
        Assert.Equal(100.0, oee.Value);
    }

    [Fact]
    public void Performance_is_capped_and_missing_inputs_give_no_oee()
    {
        var (fast, _) = ProductionService.Calculate(3600, 0, 0, 0, 0, good: 1000, scrap: 0, idealCycleSec: 10);
        Assert.Equal(100.0, fast.Performance);

        var (noIdeal, _) = ProductionService.Calculate(3600, 0, 0, 0, 0, good: 10, scrap: 0, idealCycleSec: null);
        Assert.Null(noIdeal.Performance);
        Assert.Null(noIdeal.Value);
        Assert.Equal(100.0, noIdeal.Availability);

        var (nothing, _) = ProductionService.Calculate(0, 0, 0, 0, 0, 0, 0, 30);
        Assert.Null(nothing.Availability);
    }

    [Theory]
    [InlineData("Running", "Execute")]
    [InlineData("Fault", "Aborted")]
    [InlineData("Offline", "Unknown")]
    public void Machine_states_map_to_packml(string status, string packml) => Assert.Equal(packml, ProductionService.PackMl(status));
}
