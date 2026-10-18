using Divakar.FactoryOS.Application.Maintenance;

namespace Divakar.FactoryOS.UnitTests;

public sealed class FixMatcherTests
{
    private static readonly Guid Rule = Guid.NewGuid(), Press = Guid.NewGuid(), OtherPress = Guid.NewGuid();

    private static PastFix Past(Guid asset, Guid? rule, string? sensor, string title, string cause, int helpful = 0) =>
        new(Guid.NewGuid(), "cm-1", title, asset, "Press", "press", rule, sensor, null, cause, "fixed it", DateTime.UtcNow, null, helpful);

    [Fact]
    public void Same_rule_beats_same_words_and_unrelated_fixes_are_dropped()
    {
        var problem = new FixCase(Press, "press", Rule, "hydraulic_pressure", "Hydraulic pressure low on main cylinder");
        var ranked = FixMatcher.Rank(problem, [
            Past(OtherPress, null, null, "Guard door switch", "Loose wire on door switch"),
            Past(OtherPress, null, "hydraulic_pressure", "Hydraulic pressure dropping", "Worn seal on main cylinder"),
            Past(Press, Rule, "hydraulic_pressure", "Pressure alarm", "Pump relief valve stuck open"),
        ]);
        Assert.Equal(2, ranked.Count);
        Assert.Equal("Pump relief valve stuck open", ranked[0].Fix.Cause);
        Assert.Contains("same alarm rule", ranked[0].Reasons);
        Assert.Contains(ranked[1].Reasons, r => r.StartsWith("mentions"));
    }

    [Fact]
    public void Reused_fixes_rank_higher_when_otherwise_equal()
    {
        var problem = new FixCase(Press, "press", null, null, "Motor overheating");
        var ranked = FixMatcher.Rank(problem, [Past(Press, null, null, "Motor overheating", "Fan clogged"), Past(Press, null, null, "Motor overheating", "Bearing dry", helpful: 3)]);
        Assert.Equal("Bearing dry", ranked[0].Fix.Cause);
    }
}
