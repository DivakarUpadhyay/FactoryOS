namespace Divakar.FactoryOS.Application.Enms;

public sealed record Meter(Guid MeterId, string Name, Guid? AssetId, string? AssetName, string Measures, string Kind, string? SerialNo, string? Make,
    string? AccuracyClass, decimal? UncertaintyPct, int? IntervalMonths, DateTime? LastCalibratedOn, string? Location, bool IsActive, string? LastResult);

/// <summary>A meter with its calibration state: ok, due (within 30 days), overdue, never (no record) or untracked (no interval set).</summary>
public sealed record MeterStatus(Meter Meter, DateTime? NextDueOn, string State);

public sealed record SaveMeterRequest(string Name, Guid? AssetId, string Measures, string Kind, string? SerialNo, string? Make, string? AccuracyClass,
    decimal? UncertaintyPct, int? IntervalMonths, string? Location, bool IsActive = true);

public sealed record Calibration(Guid CalibrationId, Guid MeterId, DateTime CalibratedOn, string Result, decimal? AsFoundErrorPct, decimal? AsLeftErrorPct,
    string? Lab, string? CertificateNo, string? Note);

public sealed record SaveCalibrationRequest(DateTime CalibratedOn, string Result, decimal? AsFoundErrorPct, decimal? AsLeftErrorPct, string? Lab,
    string? CertificateNo, string? Note);

public sealed record MvPlan(Guid PlanId, string Name, Guid? ActionId, string? ActionTitle, Guid? EnpiId, string? EnpiName, string MvOption, string? Boundary,
    DateTime ReportingFrom, DateTime ReportingTo, double NonRoutineKwh, string? StaticFactors, byte ConfidencePct, double? ManualSavingsKwh,
    double? ManualUncertaintyPct);

public sealed record SaveMvPlanRequest(string Name, Guid? ActionId, Guid? EnpiId, string MvOption, string? Boundary, DateTime ReportingFrom, DateTime ReportingTo,
    double NonRoutineKwh, string? StaticFactors, byte ConfidencePct, double? ManualSavingsKwh, double? ManualUncertaintyPct);

public sealed record MvCheck(string Name, string State, string Detail);

public sealed record MvResult(Guid PlanId, string Name, string MvOption, string Method, string? Model, ModelFit? Fit, SavingsUncertainty? Savings,
    double? AnnualisedKwh, double? AnnualisedUncertaintyKwh, double MeterUncertaintyPct, IReadOnlyList<MvCheck> Checks, IReadOnlyList<EnpiDay> Days,
    IReadOnlyList<string> Notes);

public interface IEnmsMeasurementRepository
{
    Task<IReadOnlyList<Meter>> MetersAsync(Guid tenantId, CancellationToken ct);
    Task<Guid> SaveMeterAsync(Guid tenantId, Guid? id, SaveMeterRequest r, CancellationToken ct);
    Task DeleteMeterAsync(Guid tenantId, Guid id, CancellationToken ct);

    /// <summary>Adds a meter for every connected asset that measures energy, gas, water or air and has none; returns how many.</summary>
    Task<int> RegisterConnectedMetersAsync(Guid tenantId, Guid? mainMeterAssetId, CancellationToken ct);

    Task<IReadOnlyList<Calibration>> CalibrationsAsync(Guid tenantId, Guid meterId, CancellationToken ct);
    /// <summary>Adds the record and moves the meter's last calibration date forward when this one is newer.</summary>
    Task<Guid> AddCalibrationAsync(Guid tenantId, Guid meterId, SaveCalibrationRequest r, CancellationToken ct);

    Task<IReadOnlyList<MvPlan>> MvPlansAsync(Guid tenantId, CancellationToken ct);
    Task<Guid> SaveMvPlanAsync(Guid tenantId, Guid? id, SaveMvPlanRequest r, CancellationToken ct);
    Task DeleteMvPlanAsync(Guid tenantId, Guid id, CancellationToken ct);
}
