using Divakar.FactoryOS.Application.Connectivity;

namespace Divakar.FactoryOS.UnitTests;

public sealed class PowerSenseTests
{
    [Fact]
    public void Bands_are_learned_from_off_idle_and_running_power()
    {
        var rng = new Random(3);
        var values = Enumerable.Range(0, 300).Select(i => (i % 3) switch
        {
            0 => 0.3 + rng.NextDouble() * 0.2,     // off ≈ 0.4 kW
            1 => 4 + rng.NextDouble(),             // idle ≈ 4.5 kW
            _ => 18 + rng.NextDouble() * 4,        // cutting ≈ 20 kW
        }).ToList();
        var r = PowerSenseMath.Learn(values, null);
        Assert.InRange(r.OffBelow, 1, 4);
        Assert.InRange(r.RunAbove, 6, 17);
        Assert.Equal(300, r.Readings);
    }

    [Fact]
    public void Short_history_falls_back_to_rated_power()
    {
        var r = PowerSenseMath.Learn([1, 2, 3], 20);
        Assert.Equal(1, r.OffBelow);   // 5 % of 20 kW
        Assert.Equal(7, r.RunAbove);   // 35 %
    }

    [Fact]
    public void Each_rise_into_running_counts_one_cycle_with_a_minimum_gap()
    {
        var t0 = new DateTime(2026, 10, 18, 8, 0, 0, DateTimeKind.Utc);
        var readings = new[] { 0.5, 3, 12, 14, 3, 12, 3, 13, 0.5 }.Select((v, i) => new SourceReading(t0.AddSeconds(i * 10), v)).ToList();
        var (rows, above, lastCycle, count) = PowerSenseMath.Step(readings, 1, 10, true, 15, false, null, 100);
        Assert.Equal(103, count);   // three bursts above 10 kW
        Assert.Equal([3, 1, 2, 2, 1, 2, 1, 2, 3], rows.Select(r => r.State));
        Assert.False(above);
        Assert.Equal(t0.AddSeconds(70), lastCycle);

        var (_, _, _, fast) = PowerSenseMath.Step(readings, 1, 10, true, 60, false, null, 0);
        Assert.Equal(1, fast);   // bursts within 60 s of the last counted cycle are not counted again
    }
}
