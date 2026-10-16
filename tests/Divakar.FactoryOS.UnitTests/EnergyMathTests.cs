using Divakar.FactoryOS.Application.Energy;
using Divakar.FactoryOS.Application.Settings;

namespace Divakar.FactoryOS.UnitTests;

public sealed class EnergyMathTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();
    private static readonly DateTime H0 = new(2026, 10, 14, 8, 0, 0);

    [Fact]
    public void Consumption_per_bucket_counts_the_rise_across_bucket_edges()
    {
        var rows = new[]
        {
            new CounterBucket(A, H0, 110, 100),            // 10 inside the first hour
            new CounterBucket(A, H0.AddHours(1), 130, 112), // 20 = 130 - previous max 110
            new CounterBucket(B, H0, 55, 50),
        };
        var c = EnergyService.Consumption(rows);
        Assert.Equal(15, c[0].Value);   // 10 + 5
        Assert.Equal(20, c[1].Value);
    }

    [Fact]
    public void A_counter_that_resets_is_not_counted_as_negative_or_huge()
    {
        var c = EnergyService.Consumption([new CounterBucket(A, H0, 9_000, 8_990), new CounterBucket(A, H0.AddHours(1), 12, 2)]);
        Assert.Equal(10, c[0].Value);
        Assert.Equal(10, c[1].Value);
    }

    [Theory]
    [InlineData(2026, 10, 14, 16, true)]   // Wednesday 16:00 peak
    [InlineData(2026, 10, 14, 21, true)]
    [InlineData(2026, 10, 14, 22, false)]  // end hour is off-peak
    [InlineData(2026, 10, 14, 9, false)]
    [InlineData(2026, 10, 17, 18, false)]  // Saturday
    public void Time_of_use_rate_depends_on_weekday_and_hour(int y, int m, int d, int h, bool peak)
    {
        var s = TenantSettings.Defaults;
        Assert.Equal(peak ? s.PeakRate : s.OffPeakRate, s.RateAt(new DateTime(y, m, d, h, 0, 0)));
        Assert.Equal(peak, s.IsPeak(new DateTime(y, m, d, h, 0, 0)));
    }

    [Theory]
    [InlineData("power_factor", 0.95, "ok")]
    [InlineData("power_factor", 0.85, "warn")]
    [InlineData("power_factor", 0.75, "bad")]
    [InlineData("thd_voltage", 6.0, "warn")]
    [InlineData("thd_voltage", 9.0, "bad")]
    [InlineData("frequency", 60.02, "ok")]
    [InlineData("frequency", 61.0, "bad")]
    [InlineData("voltage_ln", 236.0, "warn")]
    public void Power_quality_values_are_graded_against_common_limits(string sensor, double value, string expected) =>
        Assert.Equal(expected, EnergyService.Grade(sensor, value).State);

    [Fact]
    public void Main_meter_is_the_configured_one_else_the_plant_level_meters()
    {
        CounterDelta Meter(string level, Guid id) => new(id, "m", "m", "energy-meter", "Energy", level, Guid.NewGuid(), "energy_kwh", 1);
        var plant = Meter("Plant", A);
        var area = Meter("Area", B);
        Assert.Equal([plant], EnergyService.MainMeters([plant, area], null));
        Assert.Equal([area], EnergyService.MainMeters([plant, area], B));
        Assert.Equal([area], EnergyService.MainMeters([area], null));
    }
}
