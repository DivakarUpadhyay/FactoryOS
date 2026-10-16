using Divakar.FactoryOS.Application.QualityControl;

namespace Divakar.FactoryOS.UnitTests;

public sealed class SpcTests
{
    private static readonly DateTime T0 = new(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc);

    private static List<Spc.Sample> Subgroups(params double[][] groups) =>
        [.. groups.SelectMany((g, i) => g.Select(v => new Spc.Sample(i + 1, T0.AddMinutes(i), v, null)))];

    [Fact]
    public void Xbar_r_limits_use_the_shewhart_constants()
    {
        // three subgroups of 5: means 10, 11, 12; ranges 2, 2, 2
        var chart = Spc.Build(Subgroups([9, 10, 10, 10, 11], [10, 11, 11, 11, 12], [11, 12, 12, 12, 13]), 5, null, null);
        Assert.Equal("Xbar-R", chart.ChartType);
        Assert.Equal(11, chart.Limits!.Centre, 6);
        Assert.Equal(2, chart.Limits.RangeCentre, 6);
        Assert.Equal(11 + 0.577 * 2, chart.Limits.Ucl, 6);
        Assert.Equal(2.114 * 2, chart.Limits.RangeUcl, 6);
        Assert.Equal(2 / 2.326, chart.Limits.Sigma, 6);
    }

    [Fact]
    public void Individuals_chart_uses_the_moving_range()
    {
        var samples = new[] { 10.0, 12, 11, 13, 12 }.Select((v, i) => new Spc.Sample(i, T0.AddMinutes(i), v, null)).ToList();
        var chart = Spc.Build(samples, 1, null, null);
        Assert.Equal("I-MR", chart.ChartType);
        var mrBar = (2 + 1 + 2 + 1) / 4.0;
        Assert.Equal(mrBar, chart.Limits!.RangeCentre, 6);
        Assert.Equal(11.6 + 2.66 * mrBar, chart.Limits.Ucl, 6);
    }

    [Fact]
    public void Capability_indices_follow_their_definitions()
    {
        var rng = new Random(1);
        var samples = Enumerable.Range(0, 200).Select(i => new Spc.Sample(i, T0.AddMinutes(i), 10 + 0.01 * (rng.NextDouble() - 0.5) * 3.4, null)).ToList();
        var chart = Spc.Build(samples, 1, 9.95, 10.05);
        var c = chart.Capability!;
        Assert.Equal(Math.Round((10.05 - 9.95) / (6 * c.SigmaWithin), 2), c.Cp);
        Assert.Equal(Math.Round(Math.Min(10.05 - c.Mean, c.Mean - 9.95) / (3 * c.SigmaWithin), 2), c.Cpk);
        Assert.True(c.Cpk > 1.33);
        Assert.Equal(0, c.PctOutOfSpec);
    }

    [Fact]
    public void Run_rules_flag_a_point_beyond_the_limit_and_eight_on_one_side()
    {
        var values = Enumerable.Range(0, 20).Select(i => 10 + (i % 3 - 1) * 0.1).ToList();   // stable around 10
        values.Add(13);                                                                     // way out
        values.AddRange([10.6, 10.65, 10.6, 10.7, 10.6, 10.65, 10.6, 10.7]);               // drifted up
        var samples = values.Select((v, i) => new Spc.Sample(i, T0.AddMinutes(i), v, null)).ToList();
        var chart = Spc.Build(samples, 1, null, null);
        Assert.Contains("Beyond control limit", chart.Points[20].Violations);
        Assert.Contains(chart.Points.Skip(21), p => p.Violations.Contains("8 in a row on one side"));
        Assert.StartsWith("Out of control", chart.Verdict);
    }

    [Fact]
    public void No_data_gives_an_empty_chart()
    {
        var chart = Spc.Build([], 5, 1, 2);
        Assert.Null(chart.Limits);
        Assert.Empty(chart.Points);
    }
}
