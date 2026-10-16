using Divakar.FactoryOS.Application.QualityControl;

namespace Divakar.FactoryOS.UnitTests;

public sealed class InspectionTests
{
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 10, 17, 8, 0, 0, DateTimeKind.Utc);

    private static InspectionPlan Plan(string trigger, int? n = null) =>
        new(Guid.NewGuid(), Product, "Bracket", trigger, trigger, n, true, true, [new InspectionItem(1, "Check", null, null, null, null, null)]);

    private static OrderForInspection Order(string status, int produced, DateTime? end = null) =>
        new(Guid.NewGuid(), "wo-1", Product, "Bracket", status, produced, null, end);

    [Fact]
    public void First_piece_is_due_once_when_the_order_runs()
    {
        var p = Plan("FirstPiece");
        var running = Order("Running", 0);
        Assert.Single(InspectionService.Due([p], [running], new Dictionary<(Guid, Guid), int>(), Now));
        Assert.Empty(InspectionService.Due([p], [running], new Dictionary<(Guid, Guid), int> { [(p.PlanId, running.WorkOrderId)] = 1 }, Now));
        Assert.Empty(InspectionService.Due([p], [Order("Planned", 0)], new Dictionary<(Guid, Guid), int>(), Now));
    }

    [Fact]
    public void Every_n_falls_due_at_each_multiple_of_the_counter()
    {
        var p = Plan("EveryN", 50);
        var o = Order("Running", 120);
        var key = (p.PlanId, o.WorkOrderId);
        Assert.Single(InspectionService.Due([p], [o], new Dictionary<(Guid, Guid), int> { [key] = 1 }, Now));   // 2 due, 1 done
        Assert.Empty(InspectionService.Due([p], [o], new Dictionary<(Guid, Guid), int> { [key] = 2 }, Now));
        Assert.Empty(InspectionService.Due([p], [Order("Running", 49)], new Dictionary<(Guid, Guid), int>(), Now));
    }

    [Fact]
    public void Final_is_due_for_recently_completed_orders_only()
    {
        var p = Plan("Final");
        Assert.Single(InspectionService.Due([p], [Order("Completed", 100, Now.AddHours(-2))], new Dictionary<(Guid, Guid), int>(), Now));
        Assert.Empty(InspectionService.Due([p], [Order("Completed", 100, Now.AddDays(-10))], new Dictionary<(Guid, Guid), int>(), Now));
        Assert.Empty(InspectionService.Due([p], [Order("Running", 100)], new Dictionary<(Guid, Guid), int>(), Now));
    }
}
