using Dapper;
using Divakar.FactoryOS.Application.Production;
using Divakar.FactoryOS.Infrastructure.Database;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class ProductionRepository(Db db) : IProductionRepository
{
    /// <summary>The newest value of an asset's first tag of a sensor type (part or scrap counter).</summary>
    private const string LatestOf = """
        (SELECT TOP 1 l.Value FROM plant.Tags g JOIN telemetry.Latest l ON l.TagId = g.TagId
         WHERE g.AssetId = {0} AND g.SensorTypeCode = '{1}' ORDER BY g.Code)
        """;

    public async Task<IReadOnlyList<Machine>> MachinesAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Machine>(new CommandDefinition("""
            WITH up AS (
                SELECT a.AssetId, n.ParentId, n.Level, n.Name, 0 AS Depth
                FROM plant.Assets a JOIN plant.Nodes n ON n.NodeId = a.NodeId
                WHERE a.TenantId = @tenantId AND a.IsActive = 1
                UNION ALL
                SELECT up.AssetId, p.ParentId, p.Level, p.Name, up.Depth + 1
                FROM up JOIN plant.Nodes p ON p.NodeId = up.ParentId
            )
            SELECT a.AssetId, a.Code, a.Name, a.AssetTypeCode, t.Category,
                   (SELECT TOP 1 Name FROM up WHERE up.AssetId = a.AssetId AND up.Level = 'Area' ORDER BY Depth) AS AreaName,
                   (SELECT TOP 1 Name FROM up WHERE up.AssetId = a.AssetId AND up.Level = 'Line' ORDER BY Depth) AS LineName,
                   a.Status, a.IdealCycleSec
            FROM plant.Assets a JOIN plant.AssetTypes t ON t.AssetTypeCode = a.AssetTypeCode
            WHERE a.TenantId = @tenantId AND a.IsActive = 1
            ORDER BY a.Code
            """, new { tenantId }, cancellationToken: ct))];
    }

    /// <summary>
    /// Seconds spent in each machine state: the state readings in the window plus the state in force at its start,
    /// each lasting until the next reading. A machine silent for more than 3 minutes is counted only 60 s past its last reading.
    /// </summary>
    public async Task<IReadOnlyList<StateSeconds>> StateSecondsAsync(Guid tenantId, DateTime from, DateTime to, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<StateSeconds>(new CommandDefinition("""
            WITH s AS (
                SELECT t.AssetId, r.Ts, r.Value
                FROM plant.Tags t JOIN telemetry.Readings r ON r.TagId = t.TagId
                WHERE t.TenantId = @tenantId AND t.SensorTypeCode = 'machine_state' AND t.IsActive = 1
                  AND r.Ts >= @from AND r.Ts < @to AND r.Quality <= 1 AND r.Value IS NOT NULL
                UNION ALL
                SELECT t.AssetId, @from, x.Value
                FROM plant.Tags t
                CROSS APPLY (SELECT TOP 1 r.Value, r.Ts FROM telemetry.Readings r
                             WHERE r.TagId = t.TagId AND r.Ts < @from AND r.Ts > DATEADD(MINUTE, -3, @from) AND r.Quality <= 1
                             ORDER BY r.Ts DESC) x
                WHERE t.TenantId = @tenantId AND t.SensorTypeCode = 'machine_state' AND t.IsActive = 1
            ), d AS (
                SELECT AssetId, CAST(Value AS int) AS State, Ts, LEAD(Ts) OVER (PARTITION BY AssetId ORDER BY Ts) AS NextTs FROM s
            )
            SELECT AssetId, State,
                   CAST(SUM(DATEDIFF_BIG(MILLISECOND, Ts,
                       CASE WHEN NextTs IS NOT NULL AND DATEDIFF(SECOND, Ts, NextTs) <= 180 THEN NextTs
                            WHEN NextTs IS NOT NULL THEN DATEADD(SECOND, 60, Ts)
                            WHEN DATEDIFF(SECOND, Ts, @to) <= 180 THEN @to
                            ELSE DATEADD(SECOND, 60, Ts) END)) / 1000.0 AS float) AS Seconds
            FROM d
            GROUP BY AssetId, State
            """, new { tenantId, from, to }, commandTimeout: 120, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<PlannedDown>> PlannedDownAsync(Guid tenantId, DateTime from, DateTime to, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<PlannedDown>(new CommandDefinition("""
            SELECT d.AssetId,
                   CAST(SUM(DATEDIFF_BIG(MILLISECOND, IIF(d.StartTs > @from, d.StartTs, @from),
                        IIF(ISNULL(d.EndTs, SYSUTCDATETIME()) < @to, ISNULL(d.EndTs, SYSUTCDATETIME()), @to))) / 1000.0 AS float) AS Seconds
            FROM production.Downtimes d JOIN production.Reasons r ON r.ReasonCode = d.ReasonCode
            WHERE d.TenantId = @tenantId AND r.Category = 'Planned' AND d.State <> 'Maintenance'
              AND d.StartTs < @to AND ISNULL(d.EndTs, SYSUTCDATETIME()) > @from
            GROUP BY d.AssetId
            """, new { tenantId, from, to }, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<Reason>> ReasonsAsync(CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Reason>(new CommandDefinition(
            "SELECT ReasonCode, Name, Category, SortOrder FROM production.Reasons ORDER BY SortOrder", cancellationToken: ct))];
    }

    private const string DowntimeSelect = """
        SELECT d.DowntimeId, d.AssetId, a.Name AS AssetName, d.StartTs, d.EndTs,
               CAST(DATEDIFF_BIG(MILLISECOND, d.StartTs, ISNULL(d.EndTs, SYSUTCDATETIME())) / 1000.0 AS float) AS Seconds,
               d.State, d.ReasonCode, r.Name AS ReasonName, r.Category AS ReasonCategory, d.Comment
        FROM production.Downtimes d
        JOIN plant.Assets a ON a.AssetId = d.AssetId
        LEFT JOIN production.Reasons r ON r.ReasonCode = d.ReasonCode
        """;

    public async Task<IReadOnlyList<Downtime>> DowntimesAsync(Guid tenantId, DateTime from, DateTime to, Guid? assetId, bool onlyOpenOrUnexplained,
        CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Downtime>(new CommandDefinition($"""
            {DowntimeSelect}
            WHERE d.TenantId = @tenantId AND d.StartTs < @to AND ISNULL(d.EndTs, SYSUTCDATETIME()) > @from
              AND (@assetId IS NULL OR d.AssetId = @assetId)
              AND (@pending = 0 OR d.EndTs IS NULL OR d.ReasonCode IS NULL)
            ORDER BY d.StartTs DESC
            """, new { tenantId, from, to, assetId, pending = onlyOpenOrUnexplained }, cancellationToken: ct))];
    }

    public async Task<Downtime?> DowntimeAsync(Guid tenantId, long id, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.QuerySingleOrDefaultAsync<Downtime>(new CommandDefinition(
            $"{DowntimeSelect} WHERE d.TenantId = @tenantId AND d.DowntimeId = @id", new { tenantId, id }, cancellationToken: ct));
    }

    public async Task SetReasonAsync(Guid tenantId, long id, string reasonCode, string? comment, Guid userId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            UPDATE production.Downtimes SET ReasonCode = @reasonCode, Comment = @comment, ReasonBy = @userId, ReasonAt = SYSUTCDATETIME()
            WHERE TenantId = @tenantId AND DowntimeId = @id
            """, new { tenantId, id, reasonCode, comment, userId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ParetoRow>> ParetoAsync(Guid tenantId, DateTime from, DateTime to, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<ParetoRow>(new CommandDefinition("""
            SELECT d.ReasonCode, ISNULL(r.Name, N'Not yet explained') AS Reason, ISNULL(r.Category, 'Unexplained') AS Category, COUNT(*) AS Count,
                   CAST(SUM(DATEDIFF_BIG(MILLISECOND, IIF(d.StartTs > @from, d.StartTs, @from),
                        IIF(ISNULL(d.EndTs, SYSUTCDATETIME()) < @to, ISNULL(d.EndTs, SYSUTCDATETIME()), @to))) / 1000.0 AS float) AS Seconds
            FROM production.Downtimes d LEFT JOIN production.Reasons r ON r.ReasonCode = d.ReasonCode
            WHERE d.TenantId = @tenantId AND d.StartTs < @to AND ISNULL(d.EndTs, SYSUTCDATETIME()) > @from
            GROUP BY d.ReasonCode, r.Name, r.Category
            ORDER BY Seconds DESC
            """, new { tenantId, from, to }, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<Product>> ProductsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Product>(new CommandDefinition(
            "SELECT ProductId, Code, Name, IdealCycleSec, Unit, IsActive FROM production.Products WHERE TenantId = @tenantId ORDER BY Code",
            new { tenantId }, cancellationToken: ct))];
    }

    public async Task<Guid> SaveProductAsync(Guid tenantId, Guid? id, SaveProductRequest r, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        if (id is { } existing)
        {
            await con.ExecuteAsync(new CommandDefinition("""
                UPDATE production.Products SET Code = @Code, Name = @Name, IdealCycleSec = @IdealCycleSec, Unit = @Unit, IsActive = @IsActive
                WHERE TenantId = @tenantId AND ProductId = @existing
                """, new { tenantId, existing, r.Code, r.Name, r.IdealCycleSec, r.Unit, r.IsActive }, cancellationToken: ct));
            return existing;
        }
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT production.Products (TenantId, Code, Name, IdealCycleSec, Unit, IsActive) OUTPUT inserted.ProductId
            VALUES (@tenantId, @Code, @Name, @IdealCycleSec, @Unit, @IsActive)
            """, new { tenantId, r.Code, r.Name, r.IdealCycleSec, r.Unit, r.IsActive }, cancellationToken: ct));
    }

    public async Task<bool> ProductCodeExistsAsync(Guid tenantId, string code, Guid? exceptId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM production.Products WHERE TenantId = @tenantId AND Code = @code AND (@exceptId IS NULL OR ProductId <> @exceptId)",
            new { tenantId, code, exceptId }, cancellationToken: ct)) > 0;
    }

    private static readonly string WorkOrderSelect = $"""
        SELECT w.WorkOrderId, w.Code, w.ProductId, p.Code AS ProductCode, p.Name AS ProductName, w.AssetId, a.Name AS AssetName,
               w.Quantity, w.Status, w.DueDate, w.LotNo,
               CAST(CASE w.Status WHEN 'Running' THEN ISNULL({string.Format(LatestOf, "w.AssetId", "part_count")} - w.StartCount, 0)
                                  ELSE ISNULL(w.GoodQty, 0) END AS int) AS Produced,
               CAST(CASE w.Status WHEN 'Running' THEN ISNULL({string.Format(LatestOf, "w.AssetId", "scrap_count")} - w.StartScrap, 0)
                                  ELSE ISNULL(w.ScrapQty, 0) END AS int) AS Scrap,
               w.ActualStart, w.ActualEnd, w.Notes, w.CreatedAt
        FROM production.WorkOrders w
        JOIN production.Products p ON p.ProductId = w.ProductId
        JOIN plant.Assets a ON a.AssetId = w.AssetId
        """;

    public async Task<IReadOnlyList<WorkOrder>> WorkOrdersAsync(Guid tenantId, string? status, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<WorkOrder>(new CommandDefinition($"""
            {WorkOrderSelect}
            WHERE w.TenantId = @tenantId AND (@status IS NULL OR w.Status = @status)
            ORDER BY CASE w.Status WHEN 'Running' THEN 0 WHEN 'Planned' THEN 1 ELSE 2 END, w.DueDate, w.CreatedAt DESC
            """, new { tenantId, status }, cancellationToken: ct))];
    }

    public async Task<WorkOrder?> WorkOrderAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.QuerySingleOrDefaultAsync<WorkOrder>(new CommandDefinition(
            $"{WorkOrderSelect} WHERE w.TenantId = @tenantId AND w.WorkOrderId = @id", new { tenantId, id }, cancellationToken: ct));
    }

    public async Task<Guid> CreateWorkOrderAsync(Guid tenantId, SaveWorkOrderRequest r, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT production.WorkOrders (TenantId, Code, ProductId, AssetId, Quantity, DueDate, LotNo, Notes) OUTPUT inserted.WorkOrderId
            VALUES (@tenantId, @Code, @ProductId, @AssetId, @Quantity, @DueDate, @LotNo, @Notes)
            """, new { tenantId, r.Code, r.ProductId, r.AssetId, r.Quantity, r.DueDate, r.LotNo, r.Notes }, cancellationToken: ct));
    }

    public async Task<bool> WorkOrderCodeExistsAsync(Guid tenantId, string code, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM production.WorkOrders WHERE TenantId = @tenantId AND Code = @code", new { tenantId, code }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> AssetHasRunningOrderAsync(Guid tenantId, Guid assetId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM production.WorkOrders WHERE TenantId = @tenantId AND AssetId = @assetId AND Status = 'Running'",
            new { tenantId, assetId }, cancellationToken: ct)) > 0;
    }

    public async Task StartWorkOrderAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition($"""
            UPDATE w SET Status = 'Running', ActualStart = SYSUTCDATETIME(),
                   StartCount = {string.Format(LatestOf, "w.AssetId", "part_count")},
                   StartScrap = {string.Format(LatestOf, "w.AssetId", "scrap_count")}
            FROM production.WorkOrders w WHERE w.TenantId = @tenantId AND w.WorkOrderId = @id AND w.Status = 'Planned'
            """, new { tenantId, id }, cancellationToken: ct));
    }

    public async Task FinishWorkOrderAsync(Guid tenantId, Guid id, string status, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition($"""
            UPDATE w SET Status = @status, ActualEnd = SYSUTCDATETIME(),
                   GoodQty = CASE WHEN w.StartCount IS NULL THEN 0 ELSE CAST({string.Format(LatestOf, "w.AssetId", "part_count")} - w.StartCount AS int) END,
                   ScrapQty = CASE WHEN w.StartScrap IS NULL THEN 0 ELSE CAST({string.Format(LatestOf, "w.AssetId", "scrap_count")} - w.StartScrap AS int) END
            FROM production.WorkOrders w WHERE w.TenantId = @tenantId AND w.WorkOrderId = @id
            """, new { tenantId, id, status }, cancellationToken: ct));
    }
}

/// <summary>Downtime detection for every company at once (system connection), run by the worker every few seconds.</summary>
public sealed class ProductionJobsStore(Db db) : IProductionJobsStore
{
    public async Task<int> DetectDowntimeAsync(CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return await con.ExecuteAsync(new CommandDefinition("""
            DECLARE @cur TABLE (TenantId uniqueidentifier, AssetId uniqueidentifier PRIMARY KEY, TagId uniqueidentifier, State int, Ts datetime2(3));
            INSERT @cur
            SELECT t.TenantId, t.AssetId, MIN(t.TagId), MIN(CAST(l.Value AS int)), MAX(l.Ts)
            FROM plant.Tags t
            JOIN telemetry.Latest l ON l.TagId = t.TagId
            JOIN plant.Assets a ON a.AssetId = t.AssetId AND a.IsActive = 1
            JOIN plant.AssetTypes at ON at.AssetTypeCode = a.AssetTypeCode AND at.Category IN ('Production', 'Robotics', 'Utility')
            WHERE t.SensorTypeCode = 'machine_state' AND t.IsActive = 1 AND l.Quality <= 1 AND l.Value IS NOT NULL
              AND l.Ts > DATEADD(MINUTE, -5, SYSUTCDATETIME())
            GROUP BY t.TenantId, t.AssetId;

            -- running again: close at the first Running reading after the stop
            UPDATE d SET EndTs = ISNULL((SELECT MIN(r.Ts) FROM telemetry.Readings r WHERE r.TagId = c.TagId AND r.Ts > d.StartTs AND r.Value = 2), c.Ts)
            FROM production.Downtimes d JOIN @cur c ON c.AssetId = d.AssetId
            WHERE d.EndTs IS NULL AND c.State = 2;

            -- stops shorter than 30 s without a reason are micro-stops: they show in performance, not as downtime
            DELETE production.Downtimes
            WHERE EndTs IS NOT NULL AND ReasonCode IS NULL AND DATEDIFF(SECOND, StartTs, EndTs) < 30
              AND EndTs > DATEADD(MINUTE, -10, SYSUTCDATETIME());

            -- keep the worst state seen during an open downtime
            UPDATE d SET State = CASE c.State WHEN 4 THEN 'Fault' WHEN 3 THEN 'Stopped' WHEN 5 THEN 'Maintenance' ELSE d.State END
            FROM production.Downtimes d JOIN @cur c ON c.AssetId = d.AssetId
            WHERE d.EndTs IS NULL AND d.State <> 'Fault' AND (c.State = 4 OR (c.State IN (3, 5) AND d.State = 'Idle'));

            -- newly stopped: open a downtime from the first non-running reading
            INSERT production.Downtimes (TenantId, AssetId, StartTs, State, ReasonCode)
            SELECT c.TenantId, c.AssetId,
                   ISNULL((SELECT MIN(r.Ts) FROM telemetry.Readings r
                           WHERE r.TagId = c.TagId AND r.Ts > ISNULL((SELECT MAX(r2.Ts) FROM telemetry.Readings r2
                                                                       WHERE r2.TagId = c.TagId AND r2.Value = 2 AND r2.Ts <= c.Ts
                                                                         AND r2.Ts > DATEADD(DAY, -1, c.Ts)), DATEADD(DAY, -1, c.Ts))), c.Ts),
                   CASE c.State WHEN 4 THEN 'Fault' WHEN 3 THEN 'Stopped' WHEN 5 THEN 'Maintenance' ELSE 'Idle' END,
                   CASE WHEN c.State = 5 THEN 'planned-maintenance' END
            FROM @cur c
            WHERE c.State IN (1, 3, 4, 5) AND NOT EXISTS (SELECT 1 FROM production.Downtimes d WHERE d.AssetId = c.AssetId AND d.EndTs IS NULL);
            """, commandTimeout: 60, cancellationToken: ct));
    }
}
