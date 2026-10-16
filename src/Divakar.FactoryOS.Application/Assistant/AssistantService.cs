using System.Text;
using System.Text.Json;
using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Alarms;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Energy;
using Divakar.FactoryOS.Application.Inventory;
using Divakar.FactoryOS.Application.Maintenance;
using Divakar.FactoryOS.Application.Production;
using Divakar.FactoryOS.Application.QualityControl;
using Divakar.FactoryOS.Application.Robotics;

namespace Divakar.FactoryOS.Application.Assistant;

public sealed record ChatTurn(string Role, string Text);

public sealed record AskRequest(string Question, IReadOnlyList<ChatTurn>? History);

public sealed record AskResult(string Answer, string Source, DateTime SnapshotAt, IReadOnlyList<string> Sections);

public sealed record AssistantStatus(bool AiEnabled, string? Model);

/// <summary>Everything the assistant knows: the latest summary of each module the company's plan includes.</summary>
public sealed record FactorySnapshot(DateTime At, OeeReport? Oee, EnergySummary? Energy, IReadOnlyList<Alarm>? OpenAlarms, MaintenanceKpis? Maintenance,
    IReadOnlyList<MaintWorkOrder>? OpenOrders, QualitySummary? Quality, IReadOnlyList<Item>? LowStock, Fleet? Fleet, CarbonReport? Carbon);

/// <summary>A large language model answering from the snapshot (Claude); optional — without a key the built-in analysis answers.</summary>
public interface IAiClient
{
    bool Enabled { get; }
    string? Model { get; }
    Task<string> AskAsync(string system, string question, IReadOnlyList<ChatTurn> history, CancellationToken ct);
}

public sealed class AssistantService(ProductionService production, EnergyService energy, AlarmService alarms, MaintenanceService maintenance,
    QualityService quality, InventoryService inventory, RoboticsService robotics, IAiClient ai, ICurrentUser me)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public AssistantStatus Status() => new(ai.Enabled, ai.Model);

    private static async Task<T?> Try<T>(Func<Task<T>> get) where T : class
    {
        try
        {
            return await get();
        }
        catch (AppException ex) when (ex.Kind is ErrorKind.Forbidden or ErrorKind.NotFound)
        {
            return null;   // module not in the plan
        }
    }

    public async Task<FactorySnapshot> SnapshotAsync(CancellationToken ct)
    {
        me.RequireTenant();
        var now = DateTime.UtcNow;
        return new FactorySnapshot(now,
            await Try(() => production.OeeAsync(now.AddHours(-24), now, ct)),
            await Try(() => energy.SummaryAsync(now.AddHours(-24), now, ct)),
            await Try(() => alarms.AlarmsAsync("open", null, null, null, ct)),
            await Try(() => maintenance.KpisAsync(ct)),
            await Try(() => maintenance.WorkOrdersAsync("open", null, ct)),
            await Try(() => quality.SummaryAsync(ct)),
            await Try(async () => (IReadOnlyList<Item>)[.. (await inventory.ItemsAsync(ct)).Where(i => i.Low)]),
            await Try(() => robotics.FleetAsync(ct)),
            await Try(() => energy.CarbonAsync(now.AddDays(-7), now, ct)));
    }

    public async Task<AskResult> AskAsync(AskRequest r, CancellationToken ct)
    {
        var question = Guard.Required(r.Question, "Question", 1000);
        var snap = await SnapshotAsync(ct);
        if (ai.Enabled)
        {
            var system = """
                You are the assistant inside Divakar FactoryOS, a smart-manufacturing platform. Answer the user's question about their factory
                using ONLY the JSON snapshot below (last 24 hours unless stated). If the data needed is not in the snapshot, say so plainly.
                Be concise and practical: lead with the answer, then at most a few bullet points with numbers and the next action.
                Reply in the language of the question (English, Traditional Chinese or Hindi). Times are UTC.
                SNAPSHOT:
                """ + Compact(snap);
            try
            {
                var answer = await ai.AskAsync(system, question, [.. (r.History ?? []).TakeLast(8)], ct);
                return new AskResult(answer, "ai", snap.At, Sections(snap));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var fallback = BuiltIn.Answer(question, snap);
                return new AskResult($"{fallback}\n\n(The AI service did not answer — {ex.Message}. This answer comes from the built-in analysis.)", "built-in", snap.At, Sections(snap));
            }
        }
        return new AskResult(BuiltIn.Answer(question, snap), "built-in", snap.At, Sections(snap));
    }

    private static IReadOnlyList<string> Sections(FactorySnapshot s) =>
        [.. new (string, object?)[] { ("OEE", s.Oee), ("Energy", s.Energy), ("Alarms", s.OpenAlarms), ("Maintenance", s.Maintenance), ("Quality", s.Quality),
            ("Inventory", s.LowStock), ("Robotics", s.Fleet), ("Carbon", s.Carbon) }.Where(x => x.Item2 is not null).Select(x => x.Item1)];

    /// <summary>The snapshot as compact JSON, trimmed so it fits comfortably in a prompt.</summary>
    public static string Compact(FactorySnapshot s)
    {
        var slim = new
        {
            s.At,
            oee = s.Oee is null ? null : new { s.Oee.Plant, s.Oee.Good, s.Oee.Scrap, machines = s.Oee.Machines.Take(25).Select(m => new { m.Name, m.Status, m.Oee, m.Good, m.Scrap, m.DownSec }) },
            energy = s.Energy is null ? null : new { s.Energy.GridKwh, s.Energy.SolarKwh, s.Energy.Cost, s.Energy.Currency, s.Energy.PeakKw, s.Energy.ContractKw, s.Energy.IdleKwh,
                s.Energy.Co2Kg, s.Energy.Insights, top = s.Energy.Machines.Take(10).Select(m => new { m.Name, m.Kwh, m.KwhPerPart }) },
            alarms = s.OpenAlarms?.Take(30).Select(a => new { a.Severity, a.Kind, a.AssetName, a.Message, a.State, a.RaisedAt }),
            maintenance = s.Maintenance is null ? null : new { s.Maintenance.Open, s.Maintenance.InProgress, s.Maintenance.Overdue, s.Maintenance.PmCompliancePct,
                s.Maintenance.MttrMinutes, s.Maintenance.MtbfHours, worst = s.Maintenance.Assets.Take(8) },
            openOrders = s.OpenOrders?.Take(20).Select(o => new { o.Title, o.AssetName, o.Type, o.Priority, o.Status, o.Overdue, o.DueAt }),
            quality = s.Quality is null ? null : new { s.Quality.OpenNcrs, s.Quality.OutOfControl, s.Quality.NotCapable, rows = s.Quality.Rows.Take(15) },
            lowStock = s.LowStock?.Select(i => new { i.Name, i.OnHand, i.MinStock, i.Unit, i.Category }),
            fleet = s.Fleet is null ? null : new
            {
                vehicles = s.Fleet.Vehicles.Select(v => new { v.Name, v.Status, v.Battery, v.Health, v.CurrentMission }),
                robots = s.Fleet.Robots.Select(r => new { r.Name, r.Status, r.UtilizationPct, r.Health, r.Warnings }),
                activeMissions = s.Fleet.ActiveMissions.Count,
            },
            carbon7d = s.Carbon is null ? null : new { s.Carbon.TotalKg, s.Carbon.Scope1Kg, s.Carbon.Scope2Kg, s.Carbon.KgPerPart },
        };
        var json = JsonSerializer.Serialize(slim, Web);
        return json.Length > 40_000 ? json[..40_000] : json;
    }
}

/// <summary>Answers common questions straight from the snapshot, in English or Chinese keywords, without any external service.</summary>
public static class BuiltIn
{
    private static bool Has(string q, params string[] words) => words.Any(w => q.Contains(w, StringComparison.OrdinalIgnoreCase));

    public static string Answer(string question, FactorySnapshot s)
    {
        var q = question.Trim();
        var parts = new List<string>();
        if (Has(q, "oee", "production", "output", "efficien", "parts", "throughput", "稼動", "產能", "生產", "效率")) parts.Add(Oee(s));
        if (Has(q, "energy", "power", "kwh", "electric", "cost", "bill", "demand", "能源", "電", "用電", "電費")) parts.Add(Energy(s));
        if (Has(q, "alarm", "fault", "problem", "wrong", "issue", "警報", "異常", "故障")) parts.Add(Alarms(s));
        if (Has(q, "maint", "repair", "work order", "mtbf", "mttr", "breakdown", "保養", "維修", "工單")) parts.Add(Maintenance(s));
        if (Has(q, "quality", "spc", "cpk", "defect", "scrap", "ncr", "reject", "品質", "良率", "不良")) parts.Add(Quality(s));
        if (Has(q, "stock", "inventory", "material", "spare", "shortage", "庫存", "物料", "備品")) parts.Add(Stock(s));
        if (Has(q, "carbon", "co2", "esg", "emission", "碳", "排放")) parts.Add(Carbon(s));
        if (Has(q, "robot", "agv", "amr", "fleet", "mission", "battery", "機器人", "搬運")) parts.Add(Fleet(s));
        if (parts.Count == 0)
        {
            parts.Add(Briefing(s));
        }
        return string.Join("\n\n", parts.Where(p => p.Length > 0));
    }

    public static string Briefing(FactorySnapshot s)
    {
        var sb = new StringBuilder("Here is the factory right now (last 24 hours):\n");
        if (s.Oee is { } o) sb.AppendLine($"• OEE {Pct(o.Plant.Value)} — availability {Pct(o.Plant.Availability)}, performance {Pct(o.Plant.Performance)}, quality {Pct(o.Plant.Quality)}; {o.Good:N0} good parts.");
        if (s.Energy is { } e) sb.AppendLine($"• Energy {e.GridKwh:N0} kWh, {e.Currency} {e.Cost:N0}; peak {e.PeakKw:N0} kW.");
        if (s.OpenAlarms is { } a) sb.AppendLine($"• {a.Count} open alarms ({a.Count(x => x.Severity == "Critical")} critical).");
        if (s.Maintenance is { } m) sb.AppendLine($"• {m.Open + m.InProgress} open work orders, {m.Overdue} overdue.");
        if (s.Quality is { } qu) sb.AppendLine($"• Quality: {qu.OutOfControl} characteristics out of control, {qu.OpenNcrs} open non-conformances.");
        if (s.LowStock is { Count: > 0 } ls) sb.AppendLine($"• Low stock: {string.Join(", ", ls.Take(3).Select(i => i.Name))}.");
        var todo = Priorities(s);
        if (todo.Count > 0)
        {
            sb.AppendLine("\nWhat to do first:");
            foreach (var (t, i) in todo.Take(4).Select((t, i) => (t, i)))
            {
                sb.AppendLine($"{i + 1}. {t}");
            }
        }
        sb.Append("\nAsk about OEE, energy, alarms, maintenance, quality, stock, carbon or the robot fleet for details.");
        return sb.ToString();
    }

    public static List<string> Priorities(FactorySnapshot s)
    {
        var list = new List<string>();
        foreach (var a in (s.OpenAlarms ?? []).Where(x => x.Severity == "Critical" && x.State == "Active").Take(2))
        {
            list.Add($"Acknowledge and act on: {a.Message}");
        }
        if (s.Maintenance is { Overdue: > 0 } m)
        {
            list.Add($"Clear {m.Overdue} overdue maintenance order(s).");
        }
        if (s.Oee?.Machines.FirstOrDefault(x => x.Oee.Value is < 50) is { } worst)
        {
            list.Add($"Look at {worst.Name}: OEE {Pct(worst.Oee.Value)} (availability {Pct(worst.Oee.Availability)}).");
        }
        if (s.Quality is { OutOfControl: > 0 } q)
        {
            list.Add($"Investigate {q.OutOfControl} SPC chart(s) out of control.");
        }
        if (s.LowStock is { Count: > 0 } low)
        {
            list.Add($"Reorder {string.Join(", ", low.Take(3).Select(i => i.Name))}.");
        }
        list.AddRange((s.Energy?.Insights ?? []).Take(1));
        return list;
    }

    private static string Pct(double? v) => v is { } x ? $"{x:0.#}%" : "—";

    private static string Oee(FactorySnapshot s)
    {
        if (s.Oee is not { } o) return "Production and OEE are not in your plan.";
        var worst = o.Machines.Where(m => m.Oee.Value is not null).Take(3).Select(m => $"{m.Name} {Pct(m.Oee.Value)}");
        return $"Plant OEE over the last 24 h is {Pct(o.Plant.Value)} (availability {Pct(o.Plant.Availability)}, performance {Pct(o.Plant.Performance)}, quality {Pct(o.Plant.Quality)}). " +
               $"{o.Good:N0} good parts and {o.Scrap:N0} scrap. Lowest OEE: {string.Join(", ", worst)}.";
    }

    private static string Energy(FactorySnapshot s)
    {
        if (s.Energy is not { } e) return "Energy is not in your plan.";
        var top = string.Join(", ", e.Machines.Take(3).Select(m => $"{m.Name} {m.Kwh:N0} kWh"));
        return $"In the last 24 h the plant used {e.GridKwh:N0} kWh from the grid{(e.SolarKwh > 0 ? $" plus {e.SolarKwh:N0} kWh solar" : "")}, costing {e.Currency} {e.Cost:N0} " +
               $"(average {e.AvgRate} per kWh). Peak demand was {e.PeakKw:N0} kW{(e.ContractKw is { } c ? $" against a {c:N0} kW contract" : "")}. Biggest users: {top}." +
               (e.Insights.Count > 0 ? "\n" + string.Join("\n", e.Insights.Select(i => "• " + i)) : "");
    }

    private static string Alarms(FactorySnapshot s)
    {
        if (s.OpenAlarms is not { } a) return "Alarms are not in your plan.";
        if (a.Count == 0) return "There are no open alarms.";
        return $"{a.Count} open alarm(s):\n" + string.Join("\n", a.Take(8).Select(x => $"• [{x.Severity}] {x.Message} ({x.State})"));
    }

    private static string Maintenance(FactorySnapshot s)
    {
        if (s.Maintenance is not { } m) return "Maintenance is not in your plan.";
        var orders = (s.OpenOrders ?? []).Take(5).Select(o => $"• {o.Title} — {o.AssetName}{(o.Overdue ? " (overdue)" : "")}");
        return $"{m.Open} open and {m.InProgress} in-progress work orders, {m.Overdue} overdue. MTTR {m.MttrMinutes?.ToString() ?? "—"} min, MTBF {m.MtbfHours?.ToString() ?? "—"} h, " +
               $"PM compliance {Pct(m.PmCompliancePct)}.\n" + string.Join("\n", orders);
    }

    private static string Quality(FactorySnapshot s)
    {
        if (s.Quality is not { } q) return "Quality is not in your plan.";
        var rows = q.Rows.Take(5).Select(r => $"• {r.Name}: Cpk {r.Cpk?.ToString() ?? "—"} — {r.Verdict}");
        return $"{q.Characteristics} characteristics tracked: {q.OutOfControl} out of control, {q.NotCapable} not capable (Cpk < 1). {q.OpenNcrs} open non-conformances.\n" + string.Join("\n", rows);
    }

    private static string Stock(FactorySnapshot s)
    {
        if (s.LowStock is not { } low) return "Inventory is not in your plan.";
        return low.Count == 0 ? "Every item is above its minimum stock."
            : "Below minimum stock:\n" + string.Join("\n", low.Select(i => $"• {i.Name}: {i.OnHand:0.##} {i.Unit} (minimum {i.MinStock:0.##})"));
    }

    private static string Carbon(FactorySnapshot s) => s.Carbon is not { } c ? "Carbon reporting is not in your plan."
        : $"Over the last 7 days: {c.TotalKg / 1000:0.##} t CO₂e (scope 1 {c.Scope1Kg:N0} kg from fuel, scope 2 {c.Scope2Kg:N0} kg from electricity)" +
          (c.KgPerPart is { } k ? $", {k} kg per part." : ".");

    private static string Fleet(FactorySnapshot s)
    {
        if (s.Fleet is not { } f) return "Robotics is not in your plan.";
        var v = string.Join("\n", f.Vehicles.Select(x => $"• {x.Name}: {x.Status}, battery {x.Battery:0}%{(x.CurrentMission is null ? "" : $", {x.CurrentMission}")}"));
        var r = string.Join("\n", f.Robots.Where(x => x.Health != "ok").Select(x => $"• {x.Name}: {string.Join("; ", x.Warnings)}"));
        return $"{f.Vehicles.Count} vehicles, {f.Robots.Count} robots, {f.ActiveMissions.Count} missions in progress.\n{v}" + (r.Length > 0 ? $"\nRobots needing attention:\n{r}" : "");
    }
}
