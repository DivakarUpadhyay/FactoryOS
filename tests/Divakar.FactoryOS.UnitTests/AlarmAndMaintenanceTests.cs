using Divakar.FactoryOS.Application.Alarms;
using Divakar.FactoryOS.Application.Maintenance;

namespace Divakar.FactoryOS.UnitTests;

public sealed class AlarmAndMaintenanceTests
{
    private sealed class FakeStore : IAlarmStore
    {
        public List<RuleTag> Rows { get; } = [];
        public Dictionary<(Guid, Guid), DateTime> Pending { get; } = [];
        public List<(long Id, string Message)> Raised { get; } = [];
        public List<long> Cleared { get; } = [];
        public List<Escalation> Escalations { get; } = [];
        public List<long> Escalated { get; } = [];

        public Task<IReadOnlyList<RuleTag>> RuleTagsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RuleTag>>([.. Rows.Select(r => r with { PendingSince = Pending.TryGetValue((r.RuleId, r.TagId), out var s) ? s : null })]);
        public Task SetPendingAsync(Guid ruleId, Guid tagId, DateTime since, CancellationToken ct) { Pending.TryAdd((ruleId, tagId), since); return Task.CompletedTask; }
        public Task ClearPendingAsync(Guid ruleId, Guid tagId, CancellationToken ct) { Pending.Remove((ruleId, tagId)); return Task.CompletedTask; }
        public Task<long?> RaiseAsync(Guid tenantId, string kind, Guid? ruleId, Guid assetId, Guid? tagId, string severity, string message, double? value, CancellationToken ct)
        {
            var id = Raised.Count + 1L;
            Raised.Add((id, message));
            return Task.FromResult<long?>(id);
        }
        public Task ClearAsync(long alarmId, CancellationToken ct) { Cleared.Add(alarmId); return Task.CompletedTask; }
        public Task<IReadOnlyList<AnomalyCandidate>> AnomalyCandidatesAsync(IReadOnlyCollection<string> sensorTypes, DateTime now, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AnomalyCandidate>>([]);
        public Task<IReadOnlyList<Escalation>> DueEscalationsAsync(DateTime olderThan, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Escalation>>([.. Escalations.Where(e => e.RaisedAt < olderThan && !Escalated.Contains(e.AlarmId))]);
        public Task MarkEscalatedAsync(long alarmId, CancellationToken ct) { Escalated.Add(alarmId); return Task.CompletedTask; }
    }

    private sealed class Recorder : IAlarmWorkOrders, IAlarmNotifier
    {
        public List<string> Orders { get; } = [];
        public List<string> Messages { get; } = [];
        public Task CreateForAlarmAsync(Guid tenantId, Guid assetId, long alarmId, string type, string title, int priority, CancellationToken ct)
        {
            Orders.Add($"{type}:{title}");
            return Task.CompletedTask;
        }
        public Task NotifyAsync(Guid tenantId, string subject, string body, object payload, CancellationToken ct)
        {
            Messages.Add(subject);
            return Task.CompletedTask;
        }
    }

    private static readonly DateTime T0 = new(2026, 10, 16, 8, 0, 0, DateTimeKind.Utc);

    private static RuleTag Row(double value, DateTime ts, int duration = 60, long? open = null, bool wo = false) =>
        new(Guid.Empty, Guid.Parse("11111111-1111-1111-1111-111111111111"), "Too hot", ">", 50, duration, "Critical", true, wo, Guid.NewGuid(), "Oven 1",
            Guid.Parse("22222222-2222-2222-2222-222222222222"), "Temperature", "°C", value, ts, null, open);

    [Theory]
    [InlineData(51, ">", 50, true)]
    [InlineData(50, ">", 50, false)]
    [InlineData(50, ">=", 50, true)]
    [InlineData(4, "=", 4, true)]
    [InlineData(3, "!=", 4, true)]
    [InlineData(19.9, "<", 20, true)]
    public void Rule_operators(double value, string op, double threshold, bool holds) => Assert.Equal(holds, AlarmService.Holds(value, op, threshold));

    [Fact]
    public async Task A_rule_must_hold_for_its_duration_before_raising()
    {
        var store = new FakeStore();
        var rec = new Recorder();
        var engine = new AlarmEngine(store, rec, rec);
        store.Rows.Add(Row(60, T0, duration: 60, wo: true));

        await engine.RunOnceAsync(T0, default);
        Assert.Empty(store.Raised);                       // pending, not yet 60 s
        Assert.Single(store.Pending);

        store.Rows[0] = Row(61, T0.AddSeconds(30), wo: true);
        await engine.RunOnceAsync(T0.AddSeconds(30), default);
        Assert.Empty(store.Raised);

        store.Rows[0] = Row(62, T0.AddSeconds(61), wo: true);
        await engine.RunOnceAsync(T0.AddSeconds(61), default);
        var (_, message) = Assert.Single(store.Raised);
        Assert.Contains("Oven 1", message);
        Assert.Equal(["[Critical] Too hot — Oven 1"], rec.Messages);
        Assert.Equal(["Corrective:Too hot — Oven 1"], rec.Orders);
        Assert.Empty(store.Pending);
    }

    [Fact]
    public async Task A_dip_resets_the_timer_and_a_normal_value_clears_the_alarm()
    {
        var store = new FakeStore();
        var rec = new Recorder();
        var engine = new AlarmEngine(store, rec, rec);
        store.Rows.Add(Row(60, T0));
        await engine.RunOnceAsync(T0, default);
        store.Rows[0] = Row(40, T0.AddSeconds(30));
        await engine.RunOnceAsync(T0.AddSeconds(30), default);
        Assert.Empty(store.Pending);

        store.Rows[0] = Row(40, T0.AddSeconds(40), open: 7);
        await engine.RunOnceAsync(T0.AddSeconds(40), default);
        Assert.Equal([7L], store.Cleared);
    }

    [Fact]
    public async Task Stale_values_are_not_judged_and_unacknowledged_criticals_escalate_once()
    {
        var store = new FakeStore();
        var rec = new Recorder();
        var engine = new AlarmEngine(store, rec, rec);
        store.Rows.Add(Row(99, T0, duration: 0));
        await engine.RunOnceAsync(T0.AddMinutes(10), default);
        Assert.Empty(store.Raised);

        store.Rows.Clear();
        store.Escalations.Add(new Escalation(Guid.Empty, 5, "Oven 1 too hot", T0));
        await engine.RunOnceAsync(T0.AddMinutes(10), default);
        Assert.Empty(rec.Messages);                       // only after 15 minutes
        await engine.RunOnceAsync(T0.AddMinutes(16), default);
        await engine.RunOnceAsync(T0.AddMinutes(17), default);
        Assert.Single(rec.Messages);
        Assert.StartsWith("[Escalation]", rec.Messages[0]);
    }

    [Fact]
    public void Anomaly_needs_three_sigma_and_thirty_percent_above_normal()
    {
        AnomalyCandidate C(double mean, double std, double recent, int n = 100) =>
            new(Guid.Empty, Guid.Empty, "CNC", Guid.Empty, "Vibration", "vibration_rms", "mm/s", mean, std, n, recent, 5, null);
        Assert.True(AnomalyDetector.IsAnomaly(C(1.6, 0.1, 3.0)));
        Assert.False(AnomalyDetector.IsAnomaly(C(1.6, 0.1, 1.8)));     // within 3σ
        Assert.False(AnomalyDetector.IsAnomaly(C(100, 0.5, 105)));    // 10σ but only 5 % above normal
        Assert.False(AnomalyDetector.IsAnomaly(C(1.6, 0.1, 3.0, n: 5)));   // not enough history
        Assert.True(AnomalyDetector.IsBackToNormal(C(1.6, 0.1, 1.7)));
    }

    [Fact]
    public void Preventive_plans_fall_due_by_days_running_hours_or_cycles()
    {
        PlanUsage P(string trigger, int interval, DateTime lastDone, double? lastCount = null, double? current = null, double runSec = 0, bool open = false) =>
            new(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), "M", "Service", trigger, interval, null, lastDone, lastCount, current, runSec, open);
        Assert.True(MaintenanceRules.IsDue(P("Calendar", 30, T0.AddDays(-31)), T0));
        Assert.False(MaintenanceRules.IsDue(P("Calendar", 30, T0.AddDays(-29)), T0));
        Assert.True(MaintenanceRules.IsDue(P("RunHours", 500, T0, runSec: 500 * 3600), T0));
        Assert.True(MaintenanceRules.IsDue(P("Cycles", 1000, T0, 5_000, 6_000), T0));
        Assert.False(MaintenanceRules.IsDue(P("Cycles", 1000, T0, 5_000, 5_999), T0));
        Assert.False(MaintenanceRules.IsDue(P("Calendar", 30, T0.AddDays(-60), open: true), T0));   // an order is already open
    }
}
