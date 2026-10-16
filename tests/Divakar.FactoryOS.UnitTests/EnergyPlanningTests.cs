using Divakar.FactoryOS.Application.Energy;
using Divakar.FactoryOS.Connectors.Drivers;

namespace Divakar.FactoryOS.UnitTests;

public sealed class EnergyPlanningTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(1000, 1000, 0)]        // at the contract: no penalty
    [InlineData(1050, 1000, 20000)]    // 50 kW within the first 10 %: 50 × 200 × 2
    [InlineData(1200, 1000, 100000)]   // 100 × 200 × 2 + 100 × 200 × 3
    public void Excess_demand_penalty_is_double_then_triple(double peak, int contract, int expected) =>
        Assert.Equal(expected, EnergyPlanningService.ExcessPenalty(peak, contract, 200));

    [Fact]
    public void No_contract_or_charge_means_no_penalty() => Assert.Equal(0, EnergyPlanningService.ExcessPenalty(5000, null, 200));

    [Fact]
    public void Forecast_projects_the_last_complete_days_and_skips_today()
    {
        var days = new List<DayCost>
        {
            new(Now.Date.AddDays(-2), 1000, 3000, false),
            new(Now.Date.AddDays(-1), 1200, 3600, false),
            new(Now.Date, 100, 300, false),   // today, partial
        };
        var (kwh, cost) = EnergyPlanningService.Project(days, 10);
        Assert.Equal(11000, kwh, 3);
        Assert.Equal(33000, cost);
    }

    private static ShedRuleState Rule(byte priority, double shed, double restore, long? open = null, DateTime? at = null) =>
        new(Guid.Empty, Guid.NewGuid(), $"p{priority}", Guid.NewGuid(), "Load", priority, shed, restore, "Notify", null, null, null, open, at);

    [Fact]
    public void Load_shedding_sheds_by_priority_one_step_at_a_time_and_restores_in_reverse()
    {
        var a = Rule(1, 900, 700);
        var b = Rule(2, 900, 700);
        Assert.Equal(a, EnergyPlanningService_Decide(950, [b, a]).Shed);
        var aShed = a with { OpenEventId = 1, ShedAt = Now.AddMinutes(-5) };
        Assert.Equal(b, EnergyPlanningService_Decide(950, [aShed, b]).Shed);   // still high: next one
        var bShed = b with { OpenEventId = 2, ShedAt = Now.AddMinutes(-3) };
        Assert.Equal(bShed, EnergyPlanningService_Decide(600, [aShed, bShed]).Restore);   // priority 2 back first
        Assert.Null(EnergyPlanningService_Decide(800, [aShed, bShed]).Restore);   // inside the band: hold
        var justShed = a with { OpenEventId = 1, ShedAt = Now.AddSeconds(-30) };
        Assert.Null(EnergyPlanningService_Decide(100, [justShed]).Restore);   // at least 2 minutes off
    }

    private static (ShedRuleState? Shed, ShedRuleState? Restore) EnergyPlanningService_Decide(double kw, IReadOnlyList<ShedRuleState> rules) =>
        LoadShedEngine.Decide(kw, rules, Now);

    [Fact]
    public void Modbus_registers_are_big_endian_and_can_be_word_swapped()
    {
        Assert.Equal([(ushort)0x4248, (ushort)0x0000], ModbusWriter.Registers(ModbusAddress.Parse("hr:1:float32"), 50.0));
        Assert.Equal([(ushort)0x0000, (ushort)0x4248], ModbusWriter.Registers(ModbusAddress.Parse("hr:1:float32:swap"), 50.0));
        Assert.Equal([(ushort)1234], ModbusWriter.Registers(ModbusAddress.Parse("hr:1:uint16"), 1234));
    }
}
