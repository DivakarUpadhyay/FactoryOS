using Dapper;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Enms;
using Divakar.FactoryOS.Infrastructure.Database;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class EnmsMeasurementRepository(Db db) : IEnmsMeasurementRepository
{
    public async Task<IReadOnlyList<Meter>> MetersAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Meter>(new CommandDefinition("""
            SELECT m.MeterId, m.Name, m.AssetId, a.Name AS AssetName, m.Measures, m.Kind, m.SerialNo, m.Make, m.AccuracyClass, m.UncertaintyPct,
                   m.IntervalMonths, CAST(m.LastCalibratedOn AS datetime2) AS LastCalibratedOn, m.Location, m.IsActive,
                   (SELECT TOP 1 c.Result FROM enms.Calibrations c WHERE c.MeterId = m.MeterId ORDER BY c.CalibratedOn DESC, c.CreatedAt DESC) AS LastResult
            FROM enms.Meters m
            LEFT JOIN plant.Assets a ON a.AssetId = m.AssetId
            WHERE m.TenantId = @tenantId
            ORDER BY CASE m.Kind WHEN 'Main' THEN 0 WHEN 'Sub' THEN 1 ELSE 2 END, m.Name
            """, new { tenantId }, cancellationToken: ct))];
    }

    public async Task<Guid> SaveMeterAsync(Guid tenantId, Guid? id, SaveMeterRequest r, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var args = new { tenantId, id, r.Name, r.AssetId, r.Measures, r.Kind, r.SerialNo, r.Make, r.AccuracyClass, r.UncertaintyPct, r.IntervalMonths, r.Location, r.IsActive };
        if (id is not null)
        {
            var n = await con.ExecuteAsync(new CommandDefinition("""
                UPDATE enms.Meters SET Name = @Name, AssetId = @AssetId, Measures = @Measures, Kind = @Kind, SerialNo = @SerialNo, Make = @Make,
                       AccuracyClass = @AccuracyClass, UncertaintyPct = @UncertaintyPct, IntervalMonths = @IntervalMonths, Location = @Location, IsActive = @IsActive
                WHERE TenantId = @tenantId AND MeterId = @id
                """, args, cancellationToken: ct));
            return n == 0 ? throw AppException.NotFound("Meter") : id.Value;
        }
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT enms.Meters (TenantId, Name, AssetId, Measures, Kind, SerialNo, Make, AccuracyClass, UncertaintyPct, IntervalMonths, Location, IsActive)
            OUTPUT inserted.MeterId
            VALUES (@tenantId, @Name, @AssetId, @Measures, @Kind, @SerialNo, @Make, @AccuracyClass, @UncertaintyPct, @IntervalMonths, @Location, @IsActive)
            """, args, cancellationToken: ct));
    }

    public async Task DeleteMeterAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var n = await con.ExecuteAsync(new CommandDefinition("DELETE FROM enms.Meters WHERE TenantId = @tenantId AND MeterId = @id",
            new { tenantId, id }, cancellationToken: ct));
        if (n == 0)
        {
            throw AppException.NotFound("Meter");
        }
    }

    public async Task<int> RegisterConnectedMetersAsync(Guid tenantId, Guid? mainMeterAssetId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteAsync(new CommandDefinition("""
            WITH measured AS (
                SELECT DISTINCT a.AssetId, a.Name,
                       CASE WHEN t.SensorTypeCode = 'energy_kwh' THEN 'electricity'
                            WHEN t.SensorTypeCode IN ('volume_gas', 'flow_gas') THEN 'gas'
                            WHEN t.SensorTypeCode IN ('volume_water', 'flow_water') THEN 'water'
                            ELSE 'air' END AS Measures,
                       a.AssetTypeCode
                FROM plant.Tags t
                JOIN plant.Assets a ON a.AssetId = t.AssetId AND a.IsActive = 1
                WHERE t.TenantId = @tenantId AND t.IsActive = 1
                  AND t.SensorTypeCode IN ('energy_kwh', 'volume_gas', 'flow_gas', 'volume_water', 'flow_water', 'flow_air'))
            INSERT enms.Meters (TenantId, Name, AssetId, Measures, Kind, IntervalMonths)
            SELECT @tenantId, LEFT(m.Name + N' — ' + m.Measures, 150), m.AssetId, m.Measures,
                   CASE WHEN m.AssetId = @mainMeterAssetId THEN 'Main' ELSE 'Sub' END,
                   CASE WHEN m.AssetTypeCode IN ('energy-meter', 'power-analyzer') THEN 24 ELSE NULL END
            FROM measured m
            WHERE NOT EXISTS (SELECT 1 FROM enms.Meters x WHERE x.TenantId = @tenantId AND x.AssetId = m.AssetId AND x.Measures = m.Measures)
            """, new { tenantId, mainMeterAssetId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Calibration>> CalibrationsAsync(Guid tenantId, Guid meterId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Calibration>(new CommandDefinition("""
            SELECT CalibrationId, MeterId, CAST(CalibratedOn AS datetime2) AS CalibratedOn, Result, AsFoundErrorPct, AsLeftErrorPct, Lab, CertificateNo, Note
            FROM enms.Calibrations WHERE TenantId = @tenantId AND MeterId = @meterId ORDER BY CalibratedOn DESC, CreatedAt DESC
            """, new { tenantId, meterId }, cancellationToken: ct))];
    }

    public async Task<Guid> AddCalibrationAsync(Guid tenantId, Guid meterId, SaveCalibrationRequest r, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);
        var id = await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT enms.Calibrations (TenantId, MeterId, CalibratedOn, Result, AsFoundErrorPct, AsLeftErrorPct, Lab, CertificateNo, Note)
            OUTPUT inserted.CalibrationId
            VALUES (@tenantId, @meterId, @CalibratedOn, @Result, @AsFoundErrorPct, @AsLeftErrorPct, @Lab, @CertificateNo, @Note)
            """, new { tenantId, meterId, CalibratedOn = r.CalibratedOn.Date, r.Result, r.AsFoundErrorPct, r.AsLeftErrorPct, r.Lab, r.CertificateNo, r.Note },
            tx, cancellationToken: ct));
        await con.ExecuteAsync(new CommandDefinition("""
            UPDATE enms.Meters SET LastCalibratedOn = @on
            WHERE TenantId = @tenantId AND MeterId = @meterId AND (LastCalibratedOn IS NULL OR LastCalibratedOn <= @on)
            """, new { tenantId, meterId, on = r.CalibratedOn.Date }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<IReadOnlyList<MvPlan>> MvPlansAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<MvPlan>(new CommandDefinition("""
            SELECT p.PlanId, p.Name, p.ActionId, a.Title AS ActionTitle, p.EnpiId, e.Name AS EnpiName, p.MvOption, p.Boundary,
                   CAST(p.ReportingFrom AS datetime2) AS ReportingFrom, CAST(p.ReportingTo AS datetime2) AS ReportingTo, p.NonRoutineKwh, p.StaticFactors,
                   p.ConfidencePct, p.ManualSavingsKwh, p.ManualUncertaintyPct
            FROM enms.MvPlans p
            LEFT JOIN enms.Actions a ON a.ActionId = p.ActionId
            LEFT JOIN enms.Enpis e ON e.EnpiId = p.EnpiId
            WHERE p.TenantId = @tenantId
            ORDER BY p.ReportingFrom DESC, p.Name
            """, new { tenantId }, cancellationToken: ct))];
    }

    public async Task<Guid> SaveMvPlanAsync(Guid tenantId, Guid? id, SaveMvPlanRequest r, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var args = new
        {
            tenantId, id, r.Name, r.ActionId, r.EnpiId, r.MvOption, r.Boundary, ReportingFrom = r.ReportingFrom.Date, ReportingTo = r.ReportingTo.Date,
            r.NonRoutineKwh, r.StaticFactors, r.ConfidencePct, r.ManualSavingsKwh, r.ManualUncertaintyPct,
        };
        if (id is not null)
        {
            var n = await con.ExecuteAsync(new CommandDefinition("""
                UPDATE enms.MvPlans SET Name = @Name, ActionId = @ActionId, EnpiId = @EnpiId, MvOption = @MvOption, Boundary = @Boundary,
                       ReportingFrom = @ReportingFrom, ReportingTo = @ReportingTo, NonRoutineKwh = @NonRoutineKwh, StaticFactors = @StaticFactors,
                       ConfidencePct = @ConfidencePct, ManualSavingsKwh = @ManualSavingsKwh, ManualUncertaintyPct = @ManualUncertaintyPct
                WHERE TenantId = @tenantId AND PlanId = @id
                """, args, cancellationToken: ct));
            return n == 0 ? throw AppException.NotFound("M&V plan") : id.Value;
        }
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT enms.MvPlans (TenantId, Name, ActionId, EnpiId, MvOption, Boundary, ReportingFrom, ReportingTo, NonRoutineKwh, StaticFactors, ConfidencePct,
                   ManualSavingsKwh, ManualUncertaintyPct)
            OUTPUT inserted.PlanId
            VALUES (@tenantId, @Name, @ActionId, @EnpiId, @MvOption, @Boundary, @ReportingFrom, @ReportingTo, @NonRoutineKwh, @StaticFactors, @ConfidencePct,
                   @ManualSavingsKwh, @ManualUncertaintyPct)
            """, args, cancellationToken: ct));
    }

    public async Task DeleteMvPlanAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var n = await con.ExecuteAsync(new CommandDefinition("DELETE FROM enms.MvPlans WHERE TenantId = @tenantId AND PlanId = @id",
            new { tenantId, id }, cancellationToken: ct));
        if (n == 0)
        {
            throw AppException.NotFound("M&V plan");
        }
    }
}
