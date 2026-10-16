using System.Globalization;
using System.Text;
using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Alarms;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Energy;
using Divakar.FactoryOS.Application.Maintenance;
using Divakar.FactoryOS.Application.Notifications;
using Divakar.FactoryOS.Application.Production;
using Divakar.FactoryOS.Application.QualityControl;
using Divakar.FactoryOS.Application.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Divakar.FactoryOS.Application.Reports;

public sealed record Figure(string Label, string Value);

/// <summary>A report: headline figures and one table, ready to show, print or export as CSV.</summary>
public sealed record Report(string Type, string Title, DateTime From, DateTime To, IReadOnlyList<Figure> Figures, IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows, IReadOnlyList<string> Notes);

public sealed record ReportSchedule(Guid ScheduleId, string ReportType, string Recipients, byte LocalHour, DateOnly? LastSentOn, bool IsActive);

public sealed record SaveScheduleRequest(string ReportType, string Recipients, byte LocalHour, bool IsActive = true);

public sealed record DueSchedule(Guid TenantId, Guid ScheduleId, string ReportType, string Recipients, byte LocalHour, DateOnly? LastSentOn, string TimeZone);

public interface IReportScheduleRepository
{
    Task<IReadOnlyList<ReportSchedule>> ListAsync(Guid tenantId, CancellationToken ct);
    Task<Guid> SaveAsync(Guid tenantId, Guid? id, SaveScheduleRequest r, CancellationToken ct);
    Task DeleteAsync(Guid tenantId, Guid id, CancellationToken ct);
    Task<IReadOnlyList<DueSchedule>> ActiveAsync(CancellationToken ct);
    Task MarkSentAsync(Guid scheduleId, DateOnly day, CancellationToken ct);
}

public sealed class ReportService(ProductionService production, EnergyService energy, MaintenanceService maintenance, QualityService quality,
    AlarmService alarms, IReportScheduleRepository schedules, ICurrentUser me, IAuditLog audit)
{
    public static readonly string[] Types = ["production", "energy", "maintenance", "quality", "alarms"];
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string N(double? v, int digits = 1) => v is { } x ? Math.Round(x, digits).ToString(Inv) : "";
    private static string P(double? v) => v is { } x ? $"{x.ToString("0.0", Inv)}%" : "—";

    public async Task<Report> BuildAsync(string type, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var t = (to ?? DateTime.UtcNow).ToUniversalTime();
        var f = (from ?? t.AddDays(-1)).ToUniversalTime();
        switch (type)
        {
            case "production":
            {
                var r = await production.OeeAsync(f, t, ct);
                var pareto = await production.ParetoAsync(f, t, ct);
                return new Report(type, "Production and OEE", f, t,
                    [new("Plant OEE", P(r.Plant.Value)), new("Availability", P(r.Plant.Availability)), new("Performance", P(r.Plant.Performance)),
                     new("Quality", P(r.Plant.Quality)), new("Good parts", r.Good.ToString("N0", Inv)), new("Scrap", r.Scrap.ToString("N0", Inv))],
                    ["Machine", "Area", "Line", "OEE %", "Availability %", "Performance %", "Quality %", "Good", "Scrap", "Run h", "Down h", "Parts/h"],
                    [.. r.Machines.Select(m => (IReadOnlyList<string>)[m.Name, m.AreaName ?? "", m.LineName ?? "", N(m.Oee.Value), N(m.Oee.Availability), N(m.Oee.Performance),
                        N(m.Oee.Quality), N(m.Good, 0), N(m.Scrap, 0), N(m.RunSec / 3600, 2), N(m.DownSec / 3600, 2), N(m.PartsPerHour)])],
                    [.. pareto.Take(5).Select(p => $"Downtime: {p.Reason} {Math.Round(p.Seconds / 60)} min ({p.Count}×)")]);
            }
            case "energy":
            {
                var e = await energy.SummaryAsync(f, t, ct);
                return new Report(type, "Energy", f, t,
                    [new("Grid energy", $"{N(e.GridKwh)} kWh"), new("Solar", $"{N(e.SolarKwh)} kWh"), new("Cost", $"{e.Currency} {e.Cost.ToString("N0", Inv)}"),
                     new("Peak demand", $"{N(e.PeakKw)} kW"), new("Idle waste", $"{N(e.IdleKwh)} kWh"), new("CO₂e", $"{N(e.Co2Kg)} kg")],
                    ["Machine / meter", "Category", "kWh", $"Cost {e.Currency}", "Share %", "Parts", "kWh/part"],
                    [.. e.Meters.Concat(e.Machines).Select(m => (IReadOnlyList<string>)[m.Name, m.Category, N(m.Kwh, 2), m.Cost.ToString(Inv), N(m.SharePct), N(m.Parts, 0), N(m.KwhPerPart, 3)])],
                    e.Insights);
            }
            case "maintenance":
            {
                var k = await maintenance.KpisAsync(ct);
                var orders = await maintenance.WorkOrdersAsync(null, null, ct);
                return new Report(type, "Maintenance (last 30 days)", DateTime.UtcNow.AddDays(-30), DateTime.UtcNow,
                    [new("Open orders", (k.Open + k.InProgress).ToString(Inv)), new("Overdue", k.Overdue.ToString(Inv)), new("Done (30 days)", k.DoneLast30.ToString(Inv)),
                     new("PM compliance", P(k.PmCompliancePct)), new("MTTR", k.MttrMinutes is { } r ? $"{r} min" : "—"), new("MTBF", k.MtbfHours is { } b ? $"{b} h" : "—")],
                    ["Order", "Machine", "Type", "Priority", "Status", "Due", "Completed", "Checklist"],
                    [.. orders.Select(o => (IReadOnlyList<string>)[o.Title, o.AssetName, o.Type, o.Priority.ToString(Inv), o.Status, o.DueAt?.ToString("yyyy-MM-dd HH:mm", Inv) ?? "",
                        o.CompletedAt?.ToString("yyyy-MM-dd HH:mm", Inv) ?? "", o.Checklist.Count == 0 ? "" : $"{o.Checklist.Count(c => c.Done)}/{o.Checklist.Count}"])],
                    [.. k.Assets.Where(a => a.Failures > 0).Take(5).Select(a => $"{a.AssetName}: {a.Failures} failures, MTTR {a.MttrMinutes} min")]);
            }
            case "quality":
            {
                var q = await quality.SummaryAsync(ct);
                var ncrs = await quality.NcrsAsync(openOnly: false, ct);
                return new Report(type, "Quality", DateTime.UtcNow.AddDays(-7), DateTime.UtcNow,
                    [new("Characteristics", q.Characteristics.ToString(Inv)), new("Out of control", q.OutOfControl.ToString(Inv)), new("Not capable", q.NotCapable.ToString(Inv)),
                     new("Open NCRs", q.OpenNcrs.ToString(Inv)), new("Critical NCRs", q.CriticalNcrs.ToString(Inv))],
                    ["Characteristic", "Machine", "Samples", "Cpk", "Ppk", "Signals", "Verdict"],
                    [.. q.Rows.Select(r => (IReadOnlyList<string>)[r.Name, r.AssetName ?? "", r.Samples.ToString(Inv), N(r.Cpk, 2), N(r.Ppk, 2), r.Violations.ToString(Inv), r.Verdict])],
                    [.. ncrs.Where(n => n.Status != "Closed").Take(10).Select(n => $"{n.Code} {n.Severity}: {n.Title} ({n.Status})")]);
            }
            case "alarms":
            {
                var list = await alarms.AlarmsAsync(null, f, t, null, ct);
                return new Report(type, "Alarms", f, t,
                    [new("Raised", list.Count.ToString(Inv)), new("Critical", list.Count(a => a.Severity == "Critical").ToString(Inv)),
                     new("Still open", list.Count(a => a.ClearedAt is null).ToString(Inv)), new("Predictive", list.Count(a => a.Kind == "Anomaly").ToString(Inv))],
                    ["Raised (UTC)", "Severity", "Kind", "Machine", "Message", "State", "Acknowledged by", "Cleared (UTC)"],
                    [.. list.Select(a => (IReadOnlyList<string>)[a.RaisedAt.ToString("yyyy-MM-dd HH:mm", Inv), a.Severity, a.Kind, a.AssetName, a.Message, a.State, a.AckByName ?? "",
                        a.ClearedAt?.ToString("yyyy-MM-dd HH:mm", Inv) ?? ""])],
                    [.. list.GroupBy(a => a.AssetName).OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Key}: {g.Count()} alarms")]);
            }
            default:
                throw AppException.Validation($"Unknown report '{type}'. Choose one of: {string.Join(", ", Types)}.");
        }
    }

    /// <summary>CSV with a header block of figures, then the table; UTF-8 with BOM so Excel shows Chinese and symbols correctly.</summary>
    public static byte[] ToCsv(Report r)
    {
        static string Q(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        var sb = new StringBuilder();
        sb.AppendLine(Q($"Divakar FactoryOS — {r.Title}"));
        sb.AppendLine(Q($"{r.From:yyyy-MM-dd HH:mm} to {r.To:yyyy-MM-dd HH:mm} UTC"));
        foreach (var f in r.Figures)
        {
            sb.AppendLine($"{Q(f.Label)},{Q(f.Value)}");
        }
        sb.AppendLine();
        sb.AppendLine(string.Join(',', r.Columns.Select(Q)));
        foreach (var row in r.Rows)
        {
            sb.AppendLine(string.Join(',', row.Select(Q)));
        }
        if (r.Notes.Count > 0)
        {
            sb.AppendLine();
            foreach (var n in r.Notes)
            {
                sb.AppendLine(Q(n));
            }
        }
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    public static string ToText(Report r) =>
        $"{r.Title}, {r.From:yyyy-MM-dd HH:mm} – {r.To:yyyy-MM-dd HH:mm} UTC\n\n" +
        string.Join('\n', r.Figures.Select(f => $"• {f.Label}: {f.Value}")) +
        (r.Notes.Count > 0 ? "\n\n" + string.Join('\n', r.Notes.Take(8)) : "") +
        "\n\nOpen Divakar FactoryOS → Reports for the full table and CSV.";

    public Task<IReadOnlyList<ReportSchedule>> SchedulesAsync(CancellationToken ct) => schedules.ListAsync(me.RequireTenant(), ct);

    public async Task<Guid> SaveScheduleAsync(Guid? id, SaveScheduleRequest r, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        if (!Types.Contains(r.ReportType))
        {
            throw AppException.Validation("Unknown report type.");
        }
        if (r.LocalHour > 23)
        {
            throw AppException.Validation("The hour is 0–23.");
        }
        var emails = r.Recipients.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Select(Guard.Email).Distinct().ToList();
        if (emails.Count is 0 or > 20)
        {
            throw AppException.Validation("Give 1 to 20 e-mail addresses.");
        }
        if (id is { } existing && (await schedules.ListAsync(tenantId, ct)).All(s => s.ScheduleId != existing))
        {
            throw AppException.NotFound("Schedule");
        }
        var saved = await schedules.SaveAsync(tenantId, id, r with { Recipients = string.Join(", ", emails) }, ct);
        await audit.WriteAsync("report.schedule", "schedule", saved.ToString(), new { r.ReportType }, ct);
        return saved;
    }

    public async Task DeleteScheduleAsync(Guid id, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        if ((await schedules.ListAsync(tenantId, ct)).All(s => s.ScheduleId != id))
        {
            throw AppException.NotFound("Schedule");
        }
        await schedules.DeleteAsync(tenantId, id, ct);
    }
}

/// <summary>Lets a background job act inside one company (the worker has no signed-in user).</summary>
public interface ITenantRunner
{
    Task RunAsAsync(Guid tenantId, Func<IServiceProvider, Task> work, CancellationToken ct);
}

/// <summary>Worker job: at each schedule's local hour, e-mails yesterday's report figures once a day.</summary>
public sealed class ReportMailer(IReportScheduleRepository schedules, ITenantRunner runner, INotificationRepository outbox)
{
    public async Task<int> RunOnceAsync(DateTime utcNow, CancellationToken ct)
    {
        var sent = 0;
        foreach (var s in await schedules.ActiveAsync(ct))
        {
            TimeZoneInfo zone;
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(s.TimeZone);
            }
            catch (TimeZoneNotFoundException)
            {
                zone = TimeZoneInfo.Utc;
            }
            var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone);
            var today = DateOnly.FromDateTime(local);
            if (local.Hour < s.LocalHour || s.LastSentOn >= today)
            {
                continue;
            }
            var dayStart = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(today.AddDays(-1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), zone);
            Report? report = null;
            await runner.RunAsAsync(s.TenantId, async sp =>
                report = await sp.GetRequiredService<ReportService>().BuildAsync(s.ReportType, dayStart, dayStart.AddDays(1), ct), ct);
            foreach (var to in s.Recipients.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                await outbox.EnqueueAsync(s.TenantId, "Email", to, $"Daily {report!.Title} report — {today.AddDays(-1):yyyy-MM-dd}", ReportService.ToText(report), ct);
            }
            await schedules.MarkSentAsync(s.ScheduleId, today, ct);
            sent++;
        }
        return sent;
    }
}
