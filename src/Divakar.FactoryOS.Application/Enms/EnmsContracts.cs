namespace Divakar.FactoryOS.Application.Enms;

public sealed record EnergyEntry(Guid EntryId, string Source, DateTime PeriodFrom, DateTime PeriodTo, decimal Quantity, string Unit, decimal? Cost,
    string? Instrument, string? Reference, string? Note, DateTime CreatedAt);

public sealed record SaveEntryRequest(string Source, DateTime PeriodFrom, DateTime PeriodTo, decimal Quantity, string? Unit, decimal? Cost,
    string? Instrument, string? Reference, string? Note);

public sealed record Seu(Guid SeuId, string Name, string AssetIds, string? RelevantVariables, string? OperatingCriteria, string? Owner);

public sealed record SaveSeuRequest(string Name, IReadOnlyList<Guid> AssetIds, string? RelevantVariables, string? OperatingCriteria, string? Owner);

public sealed record Enpi(Guid EnpiId, string Name, Guid? SeuId, string? SeuName, string Kind, DateTime BaselineFrom, DateTime BaselineTo,
    double? BaselineKwh, double? BaselineParts, int? BaselineDays, double? Intercept, double? Slope, double? R2, DateTime CreatedAt);

public sealed record SaveEnpiRequest(string Name, Guid? SeuId, string Kind, DateTime BaselineFrom, DateTime BaselineTo);

public sealed record Objective(Guid ObjectiveId, string Title, Guid? EnpiId, string? EnpiName, decimal TargetPct, DateTime TargetDate, string? Owner);

public sealed record SaveObjectiveRequest(string Title, Guid? EnpiId, decimal TargetPct, DateTime TargetDate, string? Owner);

public sealed record EnmsAction(Guid ActionId, Guid? ObjectiveId, string? ObjectiveTitle, string Title, string? Owner, DateTime? DueDate,
    double? ExpectedKwhYear, decimal? Investment, string? Verification, double? VerifiedKwhYear, string Status);

public sealed record SaveActionRequest(Guid? ObjectiveId, string Title, string? Owner, DateTime? DueDate, double? ExpectedKwhYear, decimal? Investment,
    string? Verification, double? VerifiedKwhYear, string Status);

public sealed record EnmsDocument(Guid DocumentId, string Kind, string Title, string? Body, string? Owner, string? ApprovedBy, DateTime? ApprovedOn,
    DateTime? ReviewDue, DateTime UpdatedAt);

public sealed record SaveDocumentRequest(string Kind, string Title, string? Body, string? Owner, string? ApprovedBy, DateTime? ApprovedOn, DateTime? ReviewDue);

public sealed record Finding(Guid FindingId, string Kind, string? Clause, string Title, string? Detail, string? RootCause, string? CorrectiveAction,
    string? Owner, DateTime HeldOn, DateTime? DueDate, DateTime? ClosedOn);

public sealed record SaveFindingRequest(string Kind, string? Clause, string Title, string? Detail, string? RootCause, string? CorrectiveAction,
    string? Owner, DateTime HeldOn, DateTime? DueDate, DateTime? ClosedOn);

/// <summary>An energy-using asset in the energy review, with its share and whether the Pareto rule marks it significant.</summary>
public sealed record ReviewUse(Guid AssetId, string Name, string Category, double Kwh, double SharePct, double CumulativePct, bool Suggested, string? Seu);

public sealed record ReviewSource(string Source, string Unit, double Quantity, double Kwh, double Co2eKg, string? Factor);

public sealed record EnergyReview(DateTime From, DateTime To, double TotalKwh, double MeteredMachinesKwh, double UnmeteredKwh,
    IReadOnlyList<ReviewSource> Sources, IReadOnlyList<ReviewUse> Uses, IReadOnlyList<string> Notes);

public sealed record EnpiDay(DateTime Day, double Kwh, double Parts, double Expected, double Savings, double Cusum);

public sealed record EnpiResult(Guid EnpiId, string Name, string Kind, DateTime From, DateTime To, double ActualKwh, double ExpectedKwh,
    double SavingsKwh, double ImprovementPct, double? BaselineIntensity, double? ReportingIntensity, double? R2, string Model,
    IReadOnlyList<EnpiDay> Days, IReadOnlyList<string> Notes);

public sealed record ClauseCheck(string Clause, string Title, string State, string Evidence);

public sealed record ObjectiveProgress(Guid ObjectiveId, string Title, decimal TargetPct, DateTime TargetDate, double? ActualPct, string State);

public sealed record EnmsOverview(double ReadinessPct, IReadOnlyList<ClauseCheck> Clauses, IReadOnlyList<EnpiResult> Enpis,
    IReadOnlyList<ObjectiveProgress> Objectives, int OpenActions, double ExpectedKwhYear, double VerifiedKwhYear, int OpenNonconformities,
    DateTime? LastAudit, DateTime? LastManagementReview);

public interface IEnmsRepository
{
    Task<IReadOnlyList<EmissionFactor>> FactorsAsync(Guid tenantId, CancellationToken ct);
    Task<Guid> SaveFactorAsync(Guid tenantId, Guid? id, SaveFactorRequest r, CancellationToken ct);
    /// <summary>Adds the factors in the list that the company does not have yet (same source, method, region and year).</summary>
    Task<int> AddMissingFactorsAsync(Guid tenantId, IReadOnlyList<SaveFactorRequest> factors, CancellationToken ct);

    Task<IReadOnlyList<EnergyEntry>> EntriesAsync(Guid tenantId, DateTime? from, DateTime? to, CancellationToken ct);
    Task<Guid> AddEntryAsync(Guid tenantId, SaveEntryRequest r, CancellationToken ct);
    Task DeleteEntryAsync(Guid tenantId, Guid id, CancellationToken ct);

    Task<IReadOnlyList<Seu>> SeusAsync(Guid tenantId, CancellationToken ct);
    Task<Guid> SaveSeuAsync(Guid tenantId, Guid? id, SaveSeuRequest r, CancellationToken ct);
    Task DeleteSeuAsync(Guid tenantId, Guid id, CancellationToken ct);

    Task<IReadOnlyList<Enpi>> EnpisAsync(Guid tenantId, CancellationToken ct);
    Task<Guid> AddEnpiAsync(Guid tenantId, SaveEnpiRequest r, double kwh, double parts, int days, double? intercept, double? slope, double? r2, CancellationToken ct);
    Task DeleteEnpiAsync(Guid tenantId, Guid id, CancellationToken ct);

    Task<IReadOnlyList<Objective>> ObjectivesAsync(Guid tenantId, CancellationToken ct);
    Task<Guid> SaveObjectiveAsync(Guid tenantId, Guid? id, SaveObjectiveRequest r, CancellationToken ct);
    Task DeleteObjectiveAsync(Guid tenantId, Guid id, CancellationToken ct);

    Task<IReadOnlyList<EnmsAction>> ActionsAsync(Guid tenantId, CancellationToken ct);
    Task<Guid> SaveActionAsync(Guid tenantId, Guid? id, SaveActionRequest r, CancellationToken ct);
    Task DeleteActionAsync(Guid tenantId, Guid id, CancellationToken ct);

    Task<IReadOnlyList<EnmsDocument>> DocumentsAsync(Guid tenantId, CancellationToken ct);
    Task<Guid> SaveDocumentAsync(Guid tenantId, Guid? id, SaveDocumentRequest r, CancellationToken ct);
    Task DeleteDocumentAsync(Guid tenantId, Guid id, CancellationToken ct);

    Task<IReadOnlyList<Finding>> FindingsAsync(Guid tenantId, CancellationToken ct);
    Task<Guid> SaveFindingAsync(Guid tenantId, Guid? id, SaveFindingRequest r, CancellationToken ct);
    Task DeleteFindingAsync(Guid tenantId, Guid id, CancellationToken ct);
}
