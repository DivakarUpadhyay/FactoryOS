using Divakar.FactoryOS.Application.Reports;

namespace Divakar.FactoryOS.UnitTests;

public sealed class TransformationTests
{
    [Fact]
    public void Percent_kpis_change_in_points_and_others_in_percent()
    {
        var oee = TransformationMath.Compare("Production", "OEE", "%", 52.4, 61.0, higherIsBetter: true);
        Assert.Equal("better", oee.Verdict);
        Assert.Null(oee.ChangePct);
        Assert.Equal("+8.6 pts", TransformationMath.FormatChange(oee));

        var kwh = TransformationMath.Compare("Energy", "Electricity per good part", "kWh/pcs", 0.84, 0.71, higherIsBetter: false);
        Assert.Equal("better", kwh.Verdict);
        Assert.Equal("-15.5%", TransformationMath.FormatChange(kwh));

        var mttr = TransformationMath.Compare("Maintenance", "MTTR", "min", 30, 42, higherIsBetter: false);
        Assert.Equal("worse", mttr.Verdict);
    }

    [Fact]
    public void Tiny_changes_are_the_same_and_missing_data_is_shown_as_such()
    {
        Assert.Equal("same", TransformationMath.Compare("Production", "OEE", "%", 60.0, 60.3, true).Verdict);
        Assert.Equal("same", TransformationMath.Compare("Energy", "Grid", "kWh", 1000, 1005, false).Verdict);
        var none = TransformationMath.Compare("Maintenance", "MTBF", "h", null, 12, true);
        Assert.Equal("no data", none.Verdict);
        Assert.Equal("—", TransformationMath.FormatChange(none));
        Assert.Equal("context", TransformationMath.Compare("Production", "Good parts", "pcs", 0, 40, true, judge: false).Verdict);
    }

    [Fact]
    public void Default_baseline_is_the_period_just_before()
    {
        var from = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var (bf, bt) = TransformationMath.DefaultBaseline(from, from.AddDays(30));
        Assert.Equal(from.AddDays(-30), bf);
        Assert.Equal(from, bt);
    }
}
