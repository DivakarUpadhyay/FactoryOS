using Dapper;
using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Infrastructure.Database;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class PowerSenseRepository(Db db) : IPowerSenseRepository
{
    private const string Select = """
        SELECT p.AssetId, p.TenantId, p.SourceTagId, s.Name AS SourceTagName, s.Unit AS SourceUnit, p.StateTagId, p.CountTagId, p.OffBelow, p.RunAbove,
               p.CountCycles, p.MinCycleSec, p.AutoLearn, p.LearnedAt, p.LearnedFrom, p.LastTs, p.LastAbove, p.LastCycleAt, p.CycleCount
        FROM plant.PowerProfiles p JOIN plant.Tags s ON s.TagId = p.SourceTagId
        """;

    public async Task<PowerProfile?> ProfileAsync(Guid tenantId, Guid assetId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.QuerySingleOrDefaultAsync<PowerProfile>(new CommandDefinition($"{Select} WHERE p.TenantId = @tenantId AND p.AssetId = @assetId",
            new { tenantId, assetId }, cancellationToken: ct));
    }

    private sealed record TagRow(Guid TagId, string Code, string Name, string SensorTypeCode, string Unit);

    public async Task<IReadOnlyList<(Guid TagId, string Code, string Name, string SensorTypeCode, string Unit)>> PowerTagsAsync(Guid tenantId, Guid assetId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var rows = await con.QueryAsync<TagRow>(new CommandDefinition("""
            SELECT TagId, Code, Name, SensorTypeCode, Unit FROM plant.Tags
            WHERE TenantId = @tenantId AND AssetId = @assetId AND IsActive = 1 AND SensorTypeCode IN ('active_power', 'current', 'motor_current', 'dc_power')
            ORDER BY CASE SensorTypeCode WHEN 'active_power' THEN 0 ELSE 1 END, Code
            """, new { tenantId, assetId }, cancellationToken: ct));
        return [.. rows.Select(r => (r.TagId, r.Code, r.Name, r.SensorTypeCode, r.Unit))];
    }

    /// <summary>Creates the virtual state and cycle tags the first time, then saves the profile.</summary>
    public async Task SaveAsync(Guid tenantId, Guid assetId, Guid sourceTagId, double offBelow, double runAbove, bool countCycles, int minCycleSec, bool autoLearn,
        CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);
        async Task<Guid> Virtual(string code, string name, string sensor, string unit, string address) =>
            await con.ExecuteScalarAsync<Guid?>(new CommandDefinition("SELECT TagId FROM plant.Tags WHERE AssetId = @assetId AND Address = @address",
                new { assetId, address }, tx, cancellationToken: ct))
            ?? await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
                INSERT plant.Tags (TenantId, AssetId, Code, Name, SensorTypeCode, Unit, Address) OUTPUT inserted.TagId
                VALUES (@tenantId, @assetId, @code, @name, @sensor, @unit, @address)
                """, new { tenantId, assetId, code, name, sensor, unit, address }, tx, cancellationToken: ct));
        var state = await Virtual("ps_state", "Machine state (power sense)", "machine_state", "", PowerSenseTags.State);
        var count = await Virtual("ps_cycles", "Cycles (power sense)", "part_count", "pcs", PowerSenseTags.Count);
        await con.ExecuteAsync(new CommandDefinition("""
            MERGE plant.PowerProfiles AS x USING (SELECT @assetId AS AssetId) AS s ON x.AssetId = s.AssetId
            WHEN MATCHED THEN UPDATE SET SourceTagId = @sourceTagId, StateTagId = @state, CountTagId = @count, OffBelow = @offBelow, RunAbove = @runAbove,
                CountCycles = @countCycles, MinCycleSec = @minCycleSec, AutoLearn = @autoLearn
            WHEN NOT MATCHED THEN INSERT (AssetId, TenantId, SourceTagId, StateTagId, CountTagId, OffBelow, RunAbove, CountCycles, MinCycleSec, AutoLearn)
                VALUES (@assetId, @tenantId, @sourceTagId, @state, @count, @offBelow, @runAbove, @countCycles, @minCycleSec, @autoLearn);
            """, new { tenantId, assetId, sourceTagId, state, count, offBelow, runAbove, countCycles, minCycleSec, autoLearn }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task DisableAsync(Guid tenantId, Guid assetId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("DELETE plant.PowerProfiles WHERE TenantId = @tenantId AND AssetId = @assetId", new { tenantId, assetId }, cancellationToken: ct));
    }

    public async Task<decimal?> RatedKwAsync(Guid tenantId, Guid assetId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<decimal?>(new CommandDefinition("SELECT RatedPowerKw FROM plant.Assets WHERE TenantId = @tenantId AND AssetId = @assetId",
            new { tenantId, assetId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PowerProfile>> AllAsync(CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<PowerProfile>(new CommandDefinition($"{Select} JOIN plant.Assets a ON a.AssetId = p.AssetId WHERE a.IsActive = 1", cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<SourceReading>> ReadingsAsync(Guid tagId, DateTime? after, DateTime? from, int max, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<SourceReading>(new CommandDefinition($"""
            SELECT TOP ({max}) Ts, Value FROM telemetry.Readings
            WHERE TagId = @tagId AND Value IS NOT NULL AND Quality <= 1 AND (@after IS NULL OR Ts > @after) AND (@from IS NULL OR Ts >= @from)
            ORDER BY Ts
            """, new { tagId, after, from }, cancellationToken: ct))];
    }

    public async Task SaveStateAsync(Guid assetId, DateTime lastTs, bool lastAbove, DateTime? lastCycleAt, double cycleCount, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            UPDATE plant.PowerProfiles SET LastTs = @lastTs, LastAbove = @lastAbove, LastCycleAt = @lastCycleAt, CycleCount = @cycleCount WHERE AssetId = @assetId
            """, new { assetId, lastTs, lastAbove, lastCycleAt, cycleCount }, cancellationToken: ct));
    }

    public async Task SaveLearnedAsync(Guid assetId, double offBelow, double runAbove, int readings, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            UPDATE plant.PowerProfiles SET OffBelow = @offBelow, RunAbove = @runAbove, LearnedAt = SYSUTCDATETIME(), LearnedFrom = @readings WHERE AssetId = @assetId
            """, new { assetId, offBelow, runAbove, readings }, cancellationToken: ct));
    }
}
