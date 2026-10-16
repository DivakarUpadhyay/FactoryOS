using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Alarms;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Settings;

namespace Divakar.FactoryOS.Application.QualityControl;

public sealed record Characteristic(Guid CharId, string Code, string Name, string Unit, Guid? ProductId, string? ProductName, Guid? AssetId,
    string? AssetName, Guid? TagId, string? TagName, double? Nominal, double? Lsl, double? Usl, byte SubgroupSize, int SampleSeconds, bool IsActive,
    int Measurements, DateTime? LastAt);

public sealed record SaveCharacteristicRequest(string Code, string Name, string? Unit, Guid? ProductId, Guid? AssetId, Guid? TagId, double? Nominal,
    double? Lsl, double? Usl, byte SubgroupSize = 1, int SampleSeconds = 60, bool IsActive = true);

public sealed record MeasurementInput(Guid CharId, IReadOnlyList<double> Values, string? LotNo, Guid? WorkOrderId, string? Note);

public sealed record Ncr(Guid NcId, string Code, string Title, string? Description, string Severity, string Status, Guid? ProductId, string? ProductName,
    Guid? AssetId, string? AssetName, string? LotNo, int? Quantity, string? Disposition, string? RootCause, string? CorrectiveAction, Guid? OwnerId,
    string? OwnerName, DateOnly? DueAt, DateTime CreatedAt, DateTime? ClosedAt);

public sealed record SaveNcrRequest(string Title, string? Description, string Severity, Guid? ProductId, Guid? AssetId, string? LotNo, int? Quantity,
    string? Disposition, string? RootCause, string? CorrectiveAction, Guid? OwnerId, DateOnly? DueAt, string? Status = null);

public sealed record CapabilityRow(Guid CharId, string Code, string Name, string? AssetName, int Samples, double? Cpk, double? Ppk, int Violations, string Verdict);

public sealed record QualitySummary(int OpenNcrs, int CriticalNcrs, int Characteristics, int OutOfControl, int NotCapable, IReadOnlyList<CapabilityRow> Rows);

/// <summary>A sensor-fed characteristic as the sampler sees it, with the tag's newest value.</summary>
public sealed record TagSample(Guid TenantId, Guid CharId, string Name, Guid AssetId, string? AssetName, Guid TagId, double? Lsl, double? Usl,
    int SampleSeconds, double? Value, DateTime? Ts, DateTime? LastSampleAt, long? OpenAlarmId);

public interface IQualityRepository
{
    Task<IReadOnlyList<Characteristic>> CharacteristicsAsync(Guid tenantId, CancellationToken ct);
    Task<Guid> SaveCharacteristicAsync(Guid tenantId, Guid? id, SaveCharacteristicRequest r, CancellationToken ct);
    Task<bool> CharacteristicCodeExistsAsync(Guid tenantId, string code, Guid? exceptId, CancellationToken ct);
    Task<IReadOnlyList<Spc.Sample>> SamplesAsync(Guid tenantId, Guid charId, DateTime from, DateTime to, int max, CancellationToken ct);
    Task AddMeasurementsAsync(Guid tenantId, Guid charId, IReadOnlyList<double> values, string? lotNo, Guid? workOrderId, string? note, Guid? userId, CancellationToken ct);
    Task<bool> TagOnAssetAsync(Guid tenantId, Guid tagId, Guid? assetId, CancellationToken ct);
    Task<IReadOnlyList<Ncr>> NcrsAsync(Guid tenantId, bool openOnly, CancellationToken ct);
    Task<Ncr?> NcrAsync(Guid tenantId, Guid id, CancellationToken ct);
    Task<Guid> CreateNcrAsync(Guid tenantId, string code, SaveNcrRequest r, Guid userId, CancellationToken ct);
    Task UpdateNcrAsync(Guid tenantId, Guid id, SaveNcrRequest r, string status, CancellationToken ct);
}

public interface IQualityJobsStore
{
    Task<IReadOnlyList<TagSample>> DueTagSamplesAsync(DateTime now, CancellationToken ct);
    Task AddSampleAsync(Guid tenantId, Guid charId, double value, DateTime ts, CancellationToken ct);
    Task<IReadOnlyList<Spc.Sample>> RecentAsync(Guid charId, int count, CancellationToken ct);
}

public sealed class QualityService(IQualityRepository repo, ModuleGate gate, ICurrentUser me, IAuditLog audit)
{
    public static readonly string[] Severities = ["Minor", "Major", "Critical"];
    public static readonly string[] Dispositions = ["Rework", "Scrap", "UseAsIs", "Return"];
    public static readonly string[] Statuses = ["Open", "Contained", "Investigating", "Closed"];

    private async Task<Guid> Tenant(CancellationToken ct)
    {
        await gate.EnsureAsync("quality", ct);
        return me.RequireTenant();
    }

    public async Task<IReadOnlyList<Characteristic>> CharacteristicsAsync(CancellationToken ct) => await repo.CharacteristicsAsync(await Tenant(ct), ct);

    public async Task<Guid> SaveCharacteristicAsync(Guid? id, SaveCharacteristicRequest r, CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        var code = Guard.Code(r.Code, "Code");
        if (r.Lsl is { } l && r.Usl is { } u && l >= u)
        {
            throw AppException.Validation("The lower specification limit must be below the upper one.");
        }
        if (r.SubgroupSize is < 1 or > 10)
        {
            throw AppException.Validation("The subgroup size is 1 to 10.");
        }
        if (r.SampleSeconds is < 10 or > 86_400)
        {
            throw AppException.Validation("The sampling interval is 10 seconds to 24 hours.");
        }
        if (r.TagId is { } tag)
        {
            if (r.SubgroupSize != 1)
            {
                throw AppException.Validation("A characteristic fed by a sensor uses single values (subgroup size 1).");
            }
            if (!await repo.TagOnAssetAsync(tenantId, tag, r.AssetId, ct))
            {
                throw AppException.NotFound("Tag");
            }
        }
        if (await repo.CharacteristicCodeExistsAsync(tenantId, code, id, ct))
        {
            throw AppException.Conflict($"Characteristic '{code}' already exists.");
        }
        if (id is { } existing && (await repo.CharacteristicsAsync(tenantId, ct)).All(c => c.CharId != existing))
        {
            throw AppException.NotFound("Characteristic");
        }
        var saved = await repo.SaveCharacteristicAsync(tenantId, id, r with { Code = code, Name = Guard.Required(r.Name, "Name", 150), Unit = Guard.Optional(r.Unit, "Unit", 20) ?? "" }, ct);
        await audit.WriteAsync(id is null ? "characteristic.create" : "characteristic.update", "characteristic", saved.ToString(), null, ct);
        return saved;
    }

    public async Task<SpcChart> ChartAsync(Guid charId, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        var c = (await repo.CharacteristicsAsync(tenantId, ct)).FirstOrDefault(x => x.CharId == charId) ?? throw AppException.NotFound("Characteristic");
        var t = (to ?? DateTime.UtcNow).ToUniversalTime();
        var f = (from ?? t.AddDays(-7)).ToUniversalTime();
        var samples = await repo.SamplesAsync(tenantId, charId, f, t, 2000, ct);
        return Spc.Build(samples, c.SubgroupSize, c.Lsl, c.Usl);
    }

    public async Task AddMeasurementsAsync(MeasurementInput m, CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        var c = (await repo.CharacteristicsAsync(tenantId, ct)).FirstOrDefault(x => x.CharId == m.CharId) ?? throw AppException.NotFound("Characteristic");
        if (m.Values.Count == 0 || m.Values.Count > 10 || m.Values.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
        {
            throw AppException.Validation("Enter 1 to 10 numeric values.");
        }
        if (c.SubgroupSize > 1 && m.Values.Count != c.SubgroupSize)
        {
            throw AppException.Validation($"This characteristic is measured in subgroups of {c.SubgroupSize}.");
        }
        await repo.AddMeasurementsAsync(tenantId, m.CharId, m.Values, Guard.Optional(m.LotNo, "Lot", 60), m.WorkOrderId, Guard.Optional(m.Note, "Note", 200), me.UserId, ct);
    }

    public async Task<QualitySummary> SummaryAsync(CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        var chars = (await repo.CharacteristicsAsync(tenantId, ct)).Where(c => c.IsActive).ToList();
        var rows = new List<CapabilityRow>();
        var to = DateTime.UtcNow;
        foreach (var c in chars)
        {
            var chart = Spc.Build(await repo.SamplesAsync(tenantId, c.CharId, to.AddDays(-7), to, 500, ct), c.SubgroupSize, c.Lsl, c.Usl);
            rows.Add(new CapabilityRow(c.CharId, c.Code, c.Name, c.AssetName, chart.Points.Sum(p => p.Size), chart.Capability?.Cpk, chart.Capability?.Ppk,
                chart.Violations, chart.Verdict));
        }
        var ncrs = await repo.NcrsAsync(tenantId, openOnly: true, ct);
        return new QualitySummary(ncrs.Count, ncrs.Count(n => n.Severity == "Critical"), chars.Count, rows.Count(r => r.Violations > 0),
            rows.Count(r => r.Cpk is < 1), [.. rows.OrderBy(r => r.Cpk ?? 99)]);
    }

    public async Task<IReadOnlyList<Ncr>> NcrsAsync(bool openOnly, CancellationToken ct) => await repo.NcrsAsync(await Tenant(ct), openOnly, ct);

    private static void Validate(SaveNcrRequest r)
    {
        if (!Severities.Contains(r.Severity))
        {
            throw AppException.Validation("Severity is Minor, Major or Critical.");
        }
        if (r.Disposition is not null && !Dispositions.Contains(r.Disposition))
        {
            throw AppException.Validation("Disposition is Rework, Scrap, UseAsIs or Return.");
        }
        if (r.Quantity is < 0)
        {
            throw AppException.Validation("The quantity cannot be negative.");
        }
    }

    public async Task<Guid> CreateNcrAsync(SaveNcrRequest r, CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        Validate(r);
        var id = await repo.CreateNcrAsync(tenantId, $"ncr-{DateTime.UtcNow:yyMMdd-HHmmss}-{Random.Shared.Next(100, 999)}",
            r with { Title = Guard.Required(r.Title, "Title", 200), Description = Guard.Optional(r.Description, "Description", 1000), LotNo = Guard.Optional(r.LotNo, "Lot", 60) },
            me.UserId, ct);
        await audit.WriteAsync("ncr.create", "ncr", id.ToString(), new { r.Severity }, ct);
        return id;
    }

    /// <summary>Closing needs a disposition, a root cause and a corrective action (the CAPA).</summary>
    public async Task UpdateNcrAsync(Guid id, SaveNcrRequest r, CancellationToken ct)
    {
        var tenantId = await Tenant(ct);
        Validate(r);
        var current = await repo.NcrAsync(tenantId, id, ct) ?? throw AppException.NotFound("Non-conformance");
        var status = r.Status ?? current.Status;
        if (!Statuses.Contains(status))
        {
            throw AppException.Validation("Status is Open, Contained, Investigating or Closed.");
        }
        if (current.Status == "Closed" && status == "Closed")
        {
            throw AppException.Validation("The non-conformance is closed; reopen it first.");
        }
        if (status == "Closed" && (r.Disposition is null || string.IsNullOrWhiteSpace(r.RootCause) || string.IsNullOrWhiteSpace(r.CorrectiveAction)))
        {
            throw AppException.Validation("To close, record the disposition, the root cause and the corrective action.");
        }
        await repo.UpdateNcrAsync(tenantId, id, r with
        {
            Title = Guard.Required(r.Title, "Title", 200), RootCause = Guard.Optional(r.RootCause, "Root cause", 1000),
            CorrectiveAction = Guard.Optional(r.CorrectiveAction, "Corrective action", 1000), Description = Guard.Optional(r.Description, "Description", 1000),
        }, status, ct);
        await audit.WriteAsync("ncr.update", "ncr", id.ToString(), new { status }, ct);
    }
}

/// <summary>
/// Worker job: samples sensor-fed characteristics at their interval and checks each new value: outside the specification
/// limits raises a Critical quality alarm, beyond the control limits (from the last 100 samples) a Warning; back inside clears it.
/// </summary>
public sealed class SpcSampler(IQualityJobsStore store, IAlarmStore alarms)
{
    public async Task<int> RunOnceAsync(DateTime now, CancellationToken ct)
    {
        var count = 0;
        foreach (var s in await store.DueTagSamplesAsync(now, ct))
        {
            if (s.Value is not { } v || s.Ts is not { } ts || now - ts > TimeSpan.FromMinutes(5))
            {
                continue;
            }
            await store.AddSampleAsync(s.TenantId, s.CharId, v, ts, ct);
            count++;

            var recent = await store.RecentAsync(s.CharId, 100, ct);
            var chart = Spc.Build(recent, 1, s.Lsl, s.Usl);
            var outOfSpec = v < (s.Lsl ?? double.MinValue) || v > (s.Usl ?? double.MaxValue);
            var outOfControl = recent.Count >= 20 && chart.Limits is { } l && (v > l.Ucl || v < l.Lcl);
            if ((outOfSpec || outOfControl) && s.OpenAlarmId is null)
            {
                var why = outOfSpec ? $"outside the specification ({s.Lsl}–{s.Usl})" : $"beyond the control limits ({chart.Limits!.Lcl:0.###}–{chart.Limits.Ucl:0.###})";
                await alarms.RaiseAsync(s.TenantId, "Quality", null, s.AssetId, s.TagId, outOfSpec ? "Critical" : "Warning",
                    $"{s.Name} on {s.AssetName}: {v:0.###} is {why}", v, ct);
            }
            else if (!outOfSpec && !outOfControl && s.OpenAlarmId is { } open)
            {
                await alarms.ClearAsync(open, ct);
            }
        }
        return count;
    }
}
