using System.Text.Json;
using Dapper;
using Divakar.FactoryOS.Application.Enms;
using Divakar.FactoryOS.Infrastructure.Database;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class EnmsRepository(Db db) : IEnmsRepository
{
    private const string FactorColumns = """
        FactorId, Source, Name, Scope, Method, Region, Year, Unit, HeatKcalPerUnit, Co2KgPerTj, Ch4KgPerTj, N2oKgPerTj, KgCo2ePerUnit, GwpSet,
        Reference, IsActive
        """;

    public async Task<IReadOnlyList<EmissionFactor>> FactorsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<EmissionFactor>(new CommandDefinition($"""
            SELECT {FactorColumns} FROM enms.EmissionFactors WHERE TenantId = @tenantId ORDER BY Source, Method, Year DESC
            """, new { tenantId }, cancellationToken: ct))];
    }

    public async Task<Guid> SaveFactorAsync(Guid tenantId, Guid? id, SaveFactorRequest r, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var args = new
        {
            tenantId, id, r.Source, r.Name, r.Scope, r.Method, r.Region, r.Year, r.Unit, r.HeatKcalPerUnit, r.Co2KgPerTj, r.Ch4KgPerTj, r.N2oKgPerTj,
            r.KgCo2ePerUnit, r.GwpSet, r.Reference, r.IsActive,
        };
        if (id is not null)
        {
            var n = await con.ExecuteAsync(new CommandDefinition("""
                UPDATE enms.EmissionFactors SET Source = @Source, Name = @Name, Scope = @Scope, Method = @Method, Region = @Region, Year = @Year, Unit = @Unit,
                       HeatKcalPerUnit = @HeatKcalPerUnit, Co2KgPerTj = @Co2KgPerTj, Ch4KgPerTj = @Ch4KgPerTj, N2oKgPerTj = @N2oKgPerTj,
                       KgCo2ePerUnit = @KgCo2ePerUnit, GwpSet = @GwpSet, Reference = @Reference, IsActive = @IsActive, UpdatedAt = SYSUTCDATETIME()
                WHERE TenantId = @tenantId AND FactorId = @id
                """, args, cancellationToken: ct));
            return n == 0 ? throw Application.Common.AppException.NotFound("Emission factor") : id.Value;
        }
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT enms.EmissionFactors (TenantId, Source, Name, Scope, Method, Region, Year, Unit, HeatKcalPerUnit, Co2KgPerTj, Ch4KgPerTj, N2oKgPerTj,
                   KgCo2ePerUnit, GwpSet, Reference, IsActive)
            OUTPUT inserted.FactorId
            VALUES (@tenantId, @Source, @Name, @Scope, @Method, @Region, @Year, @Unit, @HeatKcalPerUnit, @Co2KgPerTj, @Ch4KgPerTj, @N2oKgPerTj,
                   @KgCo2ePerUnit, @GwpSet, @Reference, @IsActive)
            """, args, cancellationToken: ct));
    }

    public async Task<int> AddMissingFactorsAsync(Guid tenantId, IReadOnlyList<SaveFactorRequest> factors, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var added = 0;
        foreach (var r in factors)
        {
            added += await con.ExecuteAsync(new CommandDefinition("""
                INSERT enms.EmissionFactors (TenantId, Source, Name, Scope, Method, Region, Year, Unit, HeatKcalPerUnit, Co2KgPerTj, Ch4KgPerTj, N2oKgPerTj,
                       KgCo2ePerUnit, GwpSet, Reference, IsActive)
                SELECT @tenantId, @Source, @Name, @Scope, @Method, @Region, @Year, @Unit, @HeatKcalPerUnit, @Co2KgPerTj, @Ch4KgPerTj, @N2oKgPerTj,
                       @KgCo2ePerUnit, @GwpSet, @Reference, @IsActive
                WHERE NOT EXISTS (SELECT 1 FROM enms.EmissionFactors WHERE TenantId = @tenantId AND Source = @Source AND Method = @Method
                                  AND Region = @Region AND Year = @Year)
                """, new
            {
                tenantId, r.Source, r.Name, r.Scope, r.Method, r.Region, r.Year, r.Unit, r.HeatKcalPerUnit, r.Co2KgPerTj, r.Ch4KgPerTj, r.N2oKgPerTj,
                r.KgCo2ePerUnit, r.GwpSet, r.Reference, r.IsActive,
            }, cancellationToken: ct));
        }
        return added;
    }

    public async Task<IReadOnlyList<EnergyEntry>> EntriesAsync(Guid tenantId, DateTime? from, DateTime? to, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<EnergyEntry>(new CommandDefinition("""
            SELECT EntryId, Source, CAST(PeriodFrom AS datetime2) AS PeriodFrom, CAST(PeriodTo AS datetime2) AS PeriodTo, Quantity, Unit, Cost, Instrument,
                   Reference, Note, CreatedAt
            FROM enms.EnergyEntries
            WHERE TenantId = @tenantId AND (@from IS NULL OR PeriodTo >= @from) AND (@to IS NULL OR PeriodFrom < @to)
            ORDER BY PeriodFrom DESC, CreatedAt DESC
            """, new { tenantId, from, to }, cancellationToken: ct))];
    }

    public async Task<Guid> AddEntryAsync(Guid tenantId, SaveEntryRequest r, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT enms.EnergyEntries (TenantId, Source, PeriodFrom, PeriodTo, Quantity, Unit, Cost, Instrument, Reference, Note)
            OUTPUT inserted.EntryId
            VALUES (@tenantId, @Source, @from, @to, @Quantity, @Unit, @Cost, @Instrument, @Reference, @Note)
            """, new { tenantId, r.Source, from = r.PeriodFrom.Date, to = r.PeriodTo.Date, r.Quantity, r.Unit, r.Cost, r.Instrument, r.Reference, r.Note },
            cancellationToken: ct));
    }

    public Task DeleteEntryAsync(Guid tenantId, Guid id, CancellationToken ct) => Delete("enms.EnergyEntries", "EntryId", tenantId, id, ct);

    public async Task<IReadOnlyList<Seu>> SeusAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Seu>(new CommandDefinition("""
            SELECT SeuId, Name, AssetIds, RelevantVariables, OperatingCriteria, Owner FROM enms.Seus WHERE TenantId = @tenantId ORDER BY Name
            """, new { tenantId }, cancellationToken: ct))];
    }

    public async Task<Guid> SaveSeuAsync(Guid tenantId, Guid? id, SaveSeuRequest r, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        // only this company's assets can be part of its significant energy uses
        var assets = (await con.QueryAsync<Guid>(new CommandDefinition("SELECT AssetId FROM plant.Assets WHERE TenantId = @tenantId AND AssetId IN @ids",
            new { tenantId, ids = r.AssetIds.Distinct().ToList() }, cancellationToken: ct))).ToList();
        if (assets.Count == 0)
        {
            throw Application.Common.AppException.Validation("None of the chosen assets belong to this company.");
        }
        var args = new { tenantId, id, r.Name, assetIds = JsonSerializer.Serialize(assets), r.RelevantVariables, r.OperatingCriteria, r.Owner };
        if (id is not null)
        {
            var n = await con.ExecuteAsync(new CommandDefinition("""
                UPDATE enms.Seus SET Name = @Name, AssetIds = @assetIds, RelevantVariables = @RelevantVariables, OperatingCriteria = @OperatingCriteria, Owner = @Owner
                WHERE TenantId = @tenantId AND SeuId = @id
                """, args, cancellationToken: ct));
            return n == 0 ? throw Application.Common.AppException.NotFound("Significant energy use") : id.Value;
        }
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT enms.Seus (TenantId, Name, AssetIds, RelevantVariables, OperatingCriteria, Owner) OUTPUT inserted.SeuId
            VALUES (@tenantId, @Name, @assetIds, @RelevantVariables, @OperatingCriteria, @Owner)
            """, args, cancellationToken: ct));
    }

    public Task DeleteSeuAsync(Guid tenantId, Guid id, CancellationToken ct) => Delete("enms.Seus", "SeuId", tenantId, id, ct);

    public async Task<IReadOnlyList<Enpi>> EnpisAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Enpi>(new CommandDefinition("""
            SELECT e.EnpiId, e.Name, e.SeuId, s.Name AS SeuName, e.Kind, CAST(e.BaselineFrom AS datetime2) AS BaselineFrom,
                   CAST(e.BaselineTo AS datetime2) AS BaselineTo, e.BaselineKwh, e.BaselineParts, e.BaselineDays, e.Intercept, e.Slope, e.R2, e.CreatedAt
            FROM enms.Enpis e LEFT JOIN enms.Seus s ON s.SeuId = e.SeuId
            WHERE e.TenantId = @tenantId ORDER BY e.CreatedAt
            """, new { tenantId }, cancellationToken: ct))];
    }

    public async Task<Guid> AddEnpiAsync(Guid tenantId, SaveEnpiRequest r, double kwh, double parts, int days, double? intercept, double? slope, double? r2,
        CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT enms.Enpis (TenantId, Name, SeuId, Kind, BaselineFrom, BaselineTo, BaselineKwh, BaselineParts, BaselineDays, Intercept, Slope, R2)
            OUTPUT inserted.EnpiId
            VALUES (@tenantId, @Name, @SeuId, @Kind, @from, @to, @kwh, @parts, @days, @intercept, @slope, @r2)
            """, new { tenantId, r.Name, r.SeuId, r.Kind, from = r.BaselineFrom.Date, to = r.BaselineTo.Date, kwh, parts, days, intercept, slope, r2 },
            cancellationToken: ct));
    }

    public Task DeleteEnpiAsync(Guid tenantId, Guid id, CancellationToken ct) => Delete("enms.Enpis", "EnpiId", tenantId, id, ct);

    public async Task<IReadOnlyList<Objective>> ObjectivesAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Objective>(new CommandDefinition("""
            SELECT o.ObjectiveId, o.Title, o.EnpiId, e.Name AS EnpiName, o.TargetPct, CAST(o.TargetDate AS datetime2) AS TargetDate, o.Owner
            FROM enms.Objectives o LEFT JOIN enms.Enpis e ON e.EnpiId = o.EnpiId
            WHERE o.TenantId = @tenantId ORDER BY o.TargetDate
            """, new { tenantId }, cancellationToken: ct))];
    }

    public async Task<Guid> SaveObjectiveAsync(Guid tenantId, Guid? id, SaveObjectiveRequest r, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var args = new { tenantId, id, r.Title, r.EnpiId, r.TargetPct, date = r.TargetDate.Date, r.Owner };
        if (id is not null)
        {
            var n = await con.ExecuteAsync(new CommandDefinition("""
                UPDATE enms.Objectives SET Title = @Title, EnpiId = @EnpiId, TargetPct = @TargetPct, TargetDate = @date, Owner = @Owner
                WHERE TenantId = @tenantId AND ObjectiveId = @id
                """, args, cancellationToken: ct));
            return n == 0 ? throw Application.Common.AppException.NotFound("Objective") : id.Value;
        }
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT enms.Objectives (TenantId, Title, EnpiId, TargetPct, TargetDate, Owner) OUTPUT inserted.ObjectiveId
            VALUES (@tenantId, @Title, @EnpiId, @TargetPct, @date, @Owner)
            """, args, cancellationToken: ct));
    }

    public Task DeleteObjectiveAsync(Guid tenantId, Guid id, CancellationToken ct) => Delete("enms.Objectives", "ObjectiveId", tenantId, id, ct);

    public async Task<IReadOnlyList<EnmsAction>> ActionsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<EnmsAction>(new CommandDefinition("""
            SELECT a.ActionId, a.ObjectiveId, o.Title AS ObjectiveTitle, a.Title, a.Owner, CAST(a.DueDate AS datetime2) AS DueDate, a.ExpectedKwhYear,
                   a.Investment, a.Verification, a.VerifiedKwhYear, a.Status
            FROM enms.Actions a LEFT JOIN enms.Objectives o ON o.ObjectiveId = a.ObjectiveId
            WHERE a.TenantId = @tenantId
            ORDER BY CASE a.Status WHEN 'InProgress' THEN 0 WHEN 'Planned' THEN 1 WHEN 'Done' THEN 2 ELSE 3 END, a.DueDate
            """, new { tenantId }, cancellationToken: ct))];
    }

    public async Task<Guid> SaveActionAsync(Guid tenantId, Guid? id, SaveActionRequest r, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var args = new { tenantId, id, r.ObjectiveId, r.Title, r.Owner, due = r.DueDate?.Date, r.ExpectedKwhYear, r.Investment, r.Verification, r.VerifiedKwhYear, r.Status };
        if (id is not null)
        {
            var n = await con.ExecuteAsync(new CommandDefinition("""
                UPDATE enms.Actions SET ObjectiveId = @ObjectiveId, Title = @Title, Owner = @Owner, DueDate = @due, ExpectedKwhYear = @ExpectedKwhYear,
                       Investment = @Investment, Verification = @Verification, VerifiedKwhYear = @VerifiedKwhYear, Status = @Status
                WHERE TenantId = @tenantId AND ActionId = @id
                """, args, cancellationToken: ct));
            return n == 0 ? throw Application.Common.AppException.NotFound("Action") : id.Value;
        }
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT enms.Actions (TenantId, ObjectiveId, Title, Owner, DueDate, ExpectedKwhYear, Investment, Verification, VerifiedKwhYear, Status)
            OUTPUT inserted.ActionId
            VALUES (@tenantId, @ObjectiveId, @Title, @Owner, @due, @ExpectedKwhYear, @Investment, @Verification, @VerifiedKwhYear, @Status)
            """, args, cancellationToken: ct));
    }

    public Task DeleteActionAsync(Guid tenantId, Guid id, CancellationToken ct) => Delete("enms.Actions", "ActionId", tenantId, id, ct);

    public async Task<IReadOnlyList<EnmsDocument>> DocumentsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<EnmsDocument>(new CommandDefinition("""
            SELECT DocumentId, Kind, Title, Body, Owner, ApprovedBy, CAST(ApprovedOn AS datetime2) AS ApprovedOn, CAST(ReviewDue AS datetime2) AS ReviewDue, UpdatedAt
            FROM enms.Documents WHERE TenantId = @tenantId ORDER BY Kind, Title
            """, new { tenantId }, cancellationToken: ct))];
    }

    public async Task<Guid> SaveDocumentAsync(Guid tenantId, Guid? id, SaveDocumentRequest r, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var args = new { tenantId, id, r.Kind, r.Title, r.Body, r.Owner, r.ApprovedBy, approved = r.ApprovedOn?.Date, review = r.ReviewDue?.Date };
        if (id is not null)
        {
            var n = await con.ExecuteAsync(new CommandDefinition("""
                UPDATE enms.Documents SET Kind = @Kind, Title = @Title, Body = @Body, Owner = @Owner, ApprovedBy = @ApprovedBy, ApprovedOn = @approved,
                       ReviewDue = @review, UpdatedAt = SYSUTCDATETIME()
                WHERE TenantId = @tenantId AND DocumentId = @id
                """, args, cancellationToken: ct));
            return n == 0 ? throw Application.Common.AppException.NotFound("Document") : id.Value;
        }
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT enms.Documents (TenantId, Kind, Title, Body, Owner, ApprovedBy, ApprovedOn, ReviewDue) OUTPUT inserted.DocumentId
            VALUES (@tenantId, @Kind, @Title, @Body, @Owner, @ApprovedBy, @approved, @review)
            """, args, cancellationToken: ct));
    }

    public Task DeleteDocumentAsync(Guid tenantId, Guid id, CancellationToken ct) => Delete("enms.Documents", "DocumentId", tenantId, id, ct);

    public async Task<IReadOnlyList<Finding>> FindingsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Finding>(new CommandDefinition("""
            SELECT FindingId, Kind, Clause, Title, Detail, RootCause, CorrectiveAction, Owner, CAST(HeldOn AS datetime2) AS HeldOn,
                   CAST(DueDate AS datetime2) AS DueDate, CAST(ClosedOn AS datetime2) AS ClosedOn
            FROM enms.Findings WHERE TenantId = @tenantId ORDER BY HeldOn DESC
            """, new { tenantId }, cancellationToken: ct))];
    }

    public async Task<Guid> SaveFindingAsync(Guid tenantId, Guid? id, SaveFindingRequest r, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var args = new
        {
            tenantId, id, r.Kind, r.Clause, r.Title, r.Detail, r.RootCause, r.CorrectiveAction, r.Owner, held = r.HeldOn.Date, due = r.DueDate?.Date,
            closed = r.ClosedOn?.Date,
        };
        if (id is not null)
        {
            var n = await con.ExecuteAsync(new CommandDefinition("""
                UPDATE enms.Findings SET Kind = @Kind, Clause = @Clause, Title = @Title, Detail = @Detail, RootCause = @RootCause,
                       CorrectiveAction = @CorrectiveAction, Owner = @Owner, HeldOn = @held, DueDate = @due, ClosedOn = @closed
                WHERE TenantId = @tenantId AND FindingId = @id
                """, args, cancellationToken: ct));
            return n == 0 ? throw Application.Common.AppException.NotFound("Record") : id.Value;
        }
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT enms.Findings (TenantId, Kind, Clause, Title, Detail, RootCause, CorrectiveAction, Owner, HeldOn, DueDate, ClosedOn)
            OUTPUT inserted.FindingId
            VALUES (@tenantId, @Kind, @Clause, @Title, @Detail, @RootCause, @CorrectiveAction, @Owner, @held, @due, @closed)
            """, args, cancellationToken: ct));
    }

    public Task DeleteFindingAsync(Guid tenantId, Guid id, CancellationToken ct) => Delete("enms.Findings", "FindingId", tenantId, id, ct);

    private async Task Delete(string table, string key, Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var n = await con.ExecuteAsync(new CommandDefinition($"DELETE {table} WHERE TenantId = @tenantId AND {key} = @id", new { tenantId, id }, cancellationToken: ct));
        if (n == 0)
        {
            throw Application.Common.AppException.NotFound("Record");
        }
    }
}
