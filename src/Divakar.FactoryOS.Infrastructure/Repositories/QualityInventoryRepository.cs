using Dapper;
using Divakar.FactoryOS.Application.Inventory;
using Divakar.FactoryOS.Application.QualityControl;
using Divakar.FactoryOS.Infrastructure.Database;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class QualityRepository(Db db) : IQualityRepository
{
    public async Task<IReadOnlyList<Characteristic>> CharacteristicsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Characteristic>(new CommandDefinition("""
            SELECT c.CharId, c.Code, c.Name, c.Unit, c.ProductId, p.Name AS ProductName, c.AssetId, a.Name AS AssetName, c.TagId, t.Name AS TagName,
                   c.Nominal, c.Lsl, c.Usl, c.SubgroupSize, c.SampleSeconds, c.IsActive,
                   (SELECT COUNT(*) FROM quality.Measurements m WHERE m.CharId = c.CharId) AS Measurements,
                   (SELECT MAX(m.Ts) FROM quality.Measurements m WHERE m.CharId = c.CharId) AS LastAt
            FROM quality.Characteristics c
            LEFT JOIN production.Products p ON p.ProductId = c.ProductId
            LEFT JOIN plant.Assets a ON a.AssetId = c.AssetId
            LEFT JOIN plant.Tags t ON t.TagId = c.TagId
            WHERE c.TenantId = @tenantId
            ORDER BY c.Code
            """, new { tenantId }, cancellationToken: ct))];
    }

    public async Task<Guid> SaveCharacteristicAsync(Guid tenantId, Guid? id, SaveCharacteristicRequest r, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var args = new { tenantId, id, r.Code, r.Name, r.Unit, r.ProductId, r.AssetId, r.TagId, r.Nominal, r.Lsl, r.Usl, r.SubgroupSize, r.SampleSeconds, r.IsActive };
        if (id is not null)
        {
            await con.ExecuteAsync(new CommandDefinition("""
                UPDATE quality.Characteristics SET Code = @Code, Name = @Name, Unit = @Unit, ProductId = @ProductId, AssetId = @AssetId, TagId = @TagId,
                       Nominal = @Nominal, Lsl = @Lsl, Usl = @Usl, SubgroupSize = @SubgroupSize, SampleSeconds = @SampleSeconds, IsActive = @IsActive
                WHERE TenantId = @tenantId AND CharId = @id
                """, args, cancellationToken: ct));
            return id.Value;
        }
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT quality.Characteristics (TenantId, Code, Name, Unit, ProductId, AssetId, TagId, Nominal, Lsl, Usl, SubgroupSize, SampleSeconds, IsActive)
            OUTPUT inserted.CharId
            VALUES (@tenantId, @Code, @Name, @Unit, @ProductId, @AssetId, @TagId, @Nominal, @Lsl, @Usl, @SubgroupSize, @SampleSeconds, @IsActive)
            """, args, cancellationToken: ct));
    }

    public async Task<bool> CharacteristicCodeExistsAsync(Guid tenantId, string code, Guid? exceptId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM quality.Characteristics WHERE TenantId = @tenantId AND Code = @code AND (@exceptId IS NULL OR CharId <> @exceptId)",
            new { tenantId, code, exceptId }, cancellationToken: ct)) > 0;
    }

    public async Task<IReadOnlyList<Spc.Sample>> SamplesAsync(Guid tenantId, Guid charId, DateTime from, DateTime to, int max, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var rows = await con.QueryAsync<Spc.Sample>(new CommandDefinition($"""
            SELECT SubgroupNo, Ts, Value, LotNo FROM (
                SELECT TOP ({max}) SubgroupNo, Ts, Value, LotNo FROM quality.Measurements
                WHERE TenantId = @tenantId AND CharId = @charId AND Ts >= @from AND Ts <= @to ORDER BY Ts DESC) x
            ORDER BY Ts
            """, new { tenantId, charId, from, to }, cancellationToken: ct));
        return [.. rows];
    }

    public async Task AddMeasurementsAsync(Guid tenantId, Guid charId, IReadOnlyList<double> values, string? lotNo, Guid? workOrderId, string? note, Guid? userId,
        CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var subgroup = DateTime.UtcNow.Ticks;
        await con.ExecuteAsync(new CommandDefinition("""
            INSERT quality.Measurements (TenantId, CharId, SubgroupNo, Value, LotNo, WorkOrderId, MeasuredBy, Note)
            VALUES (@tenantId, @charId, @subgroup, @Value, @lotNo, @workOrderId, @userId, @note)
            """, values.Select(v => new { tenantId, charId, subgroup, Value = v, lotNo, workOrderId, userId, note }), cancellationToken: ct));
    }

    public async Task<bool> TagOnAssetAsync(Guid tenantId, Guid tagId, Guid? assetId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM plant.Tags WHERE TenantId = @tenantId AND TagId = @tagId AND (@assetId IS NULL OR AssetId = @assetId)",
            new { tenantId, tagId, assetId }, cancellationToken: ct)) > 0;
    }

    private const string NcrSelect = """
        SELECT n.NcId, n.Code, n.Title, n.Description, n.Severity, n.Status, n.ProductId, p.Name AS ProductName, n.AssetId, a.Name AS AssetName, n.LotNo,
               n.Quantity, n.Disposition, n.RootCause, n.CorrectiveAction, n.OwnerId, u.DisplayName AS OwnerName, n.DueAt, n.CreatedAt, n.ClosedAt
        FROM quality.NonConformances n
        LEFT JOIN production.Products p ON p.ProductId = n.ProductId
        LEFT JOIN plant.Assets a ON a.AssetId = n.AssetId
        LEFT JOIN core.Users u ON u.UserId = n.OwnerId
        """;

    public async Task<IReadOnlyList<Ncr>> NcrsAsync(Guid tenantId, bool openOnly, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Ncr>(new CommandDefinition($"""
            {NcrSelect}
            WHERE n.TenantId = @tenantId AND (@openOnly = 0 OR n.Status <> 'Closed')
            ORDER BY CASE WHEN n.Status = 'Closed' THEN 1 ELSE 0 END, CASE n.Severity WHEN 'Critical' THEN 0 WHEN 'Major' THEN 1 ELSE 2 END, n.CreatedAt DESC
            """, new { tenantId, openOnly }, cancellationToken: ct))];
    }

    public async Task<Ncr?> NcrAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.QuerySingleOrDefaultAsync<Ncr>(new CommandDefinition($"{NcrSelect} WHERE n.TenantId = @tenantId AND n.NcId = @id",
            new { tenantId, id }, cancellationToken: ct));
    }

    public async Task<Guid> CreateNcrAsync(Guid tenantId, string code, SaveNcrRequest r, Guid userId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT quality.NonConformances (TenantId, Code, Title, Description, Severity, ProductId, AssetId, LotNo, Quantity, Disposition, RootCause,
                                            CorrectiveAction, OwnerId, DueAt, CreatedBy)
            OUTPUT inserted.NcId
            VALUES (@tenantId, @code, @Title, @Description, @Severity, @ProductId, @AssetId, @LotNo, @Quantity, @Disposition, @RootCause,
                    @CorrectiveAction, @OwnerId, @DueAt, @userId)
            """, new
        {
            tenantId, code, r.Title, r.Description, r.Severity, r.ProductId, r.AssetId, r.LotNo, r.Quantity, r.Disposition, r.RootCause, r.CorrectiveAction,
            r.OwnerId, DueAt = r.DueAt?.ToDateTime(TimeOnly.MinValue), userId,
        }, cancellationToken: ct));
    }

    public async Task UpdateNcrAsync(Guid tenantId, Guid id, SaveNcrRequest r, string status, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            UPDATE quality.NonConformances
            SET Title = @Title, Description = @Description, Severity = @Severity, ProductId = @ProductId, AssetId = @AssetId, LotNo = @LotNo,
                Quantity = @Quantity, Disposition = @Disposition, RootCause = @RootCause, CorrectiveAction = @CorrectiveAction, OwnerId = @OwnerId,
                DueAt = @DueAt, Status = @status, ClosedAt = CASE WHEN @status = 'Closed' THEN SYSUTCDATETIME() ELSE NULL END
            WHERE TenantId = @tenantId AND NcId = @id
            """, new
        {
            tenantId, id, r.Title, r.Description, r.Severity, r.ProductId, r.AssetId, r.LotNo, r.Quantity, r.Disposition, r.RootCause, r.CorrectiveAction,
            r.OwnerId, DueAt = r.DueAt?.ToDateTime(TimeOnly.MinValue), status,
        }, cancellationToken: ct));
    }
}

public sealed class QualityJobsStore(Db db) : IQualityJobsStore
{
    public async Task<IReadOnlyList<TagSample>> DueTagSamplesAsync(DateTime now, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<TagSample>(new CommandDefinition("""
            SELECT c.TenantId, c.CharId, c.Name, t.AssetId, a.Name AS AssetName, c.TagId, c.Lsl, c.Usl, c.SampleSeconds, l.Value, l.Ts, m.LastAt AS LastSampleAt,
                   (SELECT TOP 1 x.AlarmId FROM alarms.Alarms x WHERE x.TagId = c.TagId AND x.Kind = 'Quality' AND x.ClearedAt IS NULL) AS OpenAlarmId
            FROM quality.Characteristics c
            JOIN plant.Tags t ON t.TagId = c.TagId
            JOIN plant.Assets a ON a.AssetId = t.AssetId
            LEFT JOIN telemetry.Latest l ON l.TagId = c.TagId AND l.Quality <= 1
            OUTER APPLY (SELECT MAX(Ts) AS LastAt FROM quality.Measurements WHERE CharId = c.CharId) m
            WHERE c.IsActive = 1 AND c.TagId IS NOT NULL
              AND (m.LastAt IS NULL OR DATEADD(SECOND, c.SampleSeconds, m.LastAt) <= @now)
              AND (m.LastAt IS NULL OR l.Ts > m.LastAt)
            """, new { now }, cancellationToken: ct))];
    }

    public async Task AddSampleAsync(Guid tenantId, Guid charId, double value, DateTime ts, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            INSERT quality.Measurements (TenantId, CharId, SubgroupNo, Value, Ts) VALUES (@tenantId, @charId, @subgroup, @value, @ts)
            """, new { tenantId, charId, subgroup = ts.Ticks, value, ts }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Spc.Sample>> RecentAsync(Guid charId, int count, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<Spc.Sample>(new CommandDefinition($"""
            SELECT SubgroupNo, Ts, Value, LotNo FROM (SELECT TOP ({count}) SubgroupNo, Ts, Value, LotNo FROM quality.Measurements
                                                     WHERE CharId = @charId ORDER BY Ts DESC) x ORDER BY Ts
            """, new { charId }, cancellationToken: ct))];
    }
}

public sealed class InventoryRepository(Db db) : IInventoryRepository
{
    public async Task<IReadOnlyList<Item>> ItemsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Item>(new CommandDefinition("""
            SELECT i.ItemId, i.Code, i.Name, i.Category, i.Unit, i.MinStock, i.ReorderQty, i.Location, i.Barcode, i.ProductId, i.IsActive,
                   ISNULL(s.OnHand, 0) AS OnHand,
                   (SELECT COUNT(*) FROM inventory.Lots l WHERE l.ItemId = i.ItemId AND l.Status IN ('Available', 'Quarantine')) AS Lots,
                   CAST(CASE WHEN ISNULL(s.OnHand, 0) <= i.MinStock AND i.MinStock > 0 THEN 1 ELSE 0 END AS bit) AS Low
            FROM inventory.Items i
            OUTER APPLY (SELECT SUM(m.Quantity) AS OnHand FROM inventory.Movements m WHERE m.ItemId = i.ItemId) s
            WHERE i.TenantId = @tenantId
            ORDER BY i.Category, i.Code
            """, new { tenantId }, cancellationToken: ct))];
    }

    public async Task<Guid> SaveItemAsync(Guid tenantId, Guid? id, SaveItemRequest r, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var args = new { tenantId, id, r.Code, r.Name, r.Category, r.Unit, r.MinStock, r.ReorderQty, r.Location, r.Barcode, r.ProductId, r.IsActive };
        if (id is not null)
        {
            await con.ExecuteAsync(new CommandDefinition("""
                UPDATE inventory.Items SET Code = @Code, Name = @Name, Category = @Category, Unit = @Unit, MinStock = @MinStock, ReorderQty = @ReorderQty,
                       Location = @Location, Barcode = @Barcode, ProductId = @ProductId, IsActive = @IsActive
                WHERE TenantId = @tenantId AND ItemId = @id
                """, args, cancellationToken: ct));
            return id.Value;
        }
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT inventory.Items (TenantId, Code, Name, Category, Unit, MinStock, ReorderQty, Location, Barcode, ProductId, IsActive) OUTPUT inserted.ItemId
            VALUES (@tenantId, @Code, @Name, @Category, @Unit, @MinStock, @ReorderQty, @Location, @Barcode, @ProductId, @IsActive)
            """, args, cancellationToken: ct));
    }

    public async Task<bool> ItemCodeOrBarcodeTakenAsync(Guid tenantId, string code, string? barcode, Guid? exceptId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM inventory.Items
            WHERE TenantId = @tenantId AND (Code = @code OR (@barcode IS NOT NULL AND Barcode = @barcode)) AND (@exceptId IS NULL OR ItemId <> @exceptId)
            """, new { tenantId, code, barcode, exceptId }, cancellationToken: ct)) > 0;
    }

    public async Task<IReadOnlyList<Lot>> LotsAsync(Guid tenantId, Guid? itemId, string? lotNo, Guid? workOrderId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Lot>(new CommandDefinition("""
            SELECT l.LotId, l.ItemId, i.Code AS ItemCode, i.Name AS ItemName, l.LotNo, l.Status,
                   ISNULL((SELECT SUM(m.Quantity) FROM inventory.Movements m WHERE m.LotId = l.LotId), 0) AS OnHand,
                   l.Supplier, l.SupplierLot, l.ExpiresOn, l.WorkOrderId, w.Code AS WorkOrderCode, l.CreatedAt
            FROM inventory.Lots l
            JOIN inventory.Items i ON i.ItemId = l.ItemId
            LEFT JOIN production.WorkOrders w ON w.WorkOrderId = l.WorkOrderId
            WHERE l.TenantId = @tenantId AND (@itemId IS NULL OR l.ItemId = @itemId) AND (@lotNo IS NULL OR l.LotNo = @lotNo)
              AND (@workOrderId IS NULL OR l.WorkOrderId = @workOrderId)
            ORDER BY CASE WHEN l.Status IN ('Available', 'Quarantine') THEN 0 ELSE 1 END, l.ExpiresOn, l.CreatedAt DESC
            """, new { tenantId, itemId, lotNo, workOrderId }, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<Movement>> MovementsAsync(Guid tenantId, Guid? itemId, Guid? lotId, Guid? workOrderId, int take, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Movement>(new CommandDefinition($"""
            SELECT TOP ({take}) m.MovementId, m.ItemId, i.Name AS ItemName, l.LotNo, m.Type, m.Quantity, m.WorkOrderId, w.Code AS WorkOrderCode,
                   mw.Code AS MaintWoCode, m.Reference, u.DisplayName AS UserName, m.Ts
            FROM inventory.Movements m
            JOIN inventory.Items i ON i.ItemId = m.ItemId
            LEFT JOIN inventory.Lots l ON l.LotId = m.LotId
            LEFT JOIN production.WorkOrders w ON w.WorkOrderId = m.WorkOrderId
            LEFT JOIN maintenance.WorkOrders mw ON mw.MaintWoId = m.MaintWoId
            LEFT JOIN core.Users u ON u.UserId = m.UserId
            WHERE m.TenantId = @tenantId AND (@itemId IS NULL OR m.ItemId = @itemId) AND (@lotId IS NULL OR m.LotId = @lotId)
              AND (@workOrderId IS NULL OR m.WorkOrderId = @workOrderId)
            ORDER BY m.MovementId DESC
            """, new { tenantId, itemId, lotId, workOrderId }, cancellationToken: ct))];
    }

    public async Task<Guid> CreateLotAsync(Guid tenantId, Guid itemId, string lotNo, string? supplier, string? supplierLot, DateOnly? expiresOn, Guid? workOrderId,
        CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT inventory.Lots (TenantId, ItemId, LotNo, Supplier, SupplierLot, ExpiresOn, WorkOrderId) OUTPUT inserted.LotId
            VALUES (@tenantId, @itemId, @lotNo, @supplier, @supplierLot, @expires, @workOrderId)
            """, new { tenantId, itemId, lotNo, supplier, supplierLot, expires = expiresOn?.ToDateTime(TimeOnly.MinValue), workOrderId }, cancellationToken: ct));
    }

    public async Task<bool> LotNoExistsAsync(Guid tenantId, string lotNo, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM inventory.Lots WHERE TenantId = @tenantId AND LotNo = @lotNo",
            new { tenantId, lotNo }, cancellationToken: ct)) > 0;
    }

    public async Task AddMovementAsync(Guid tenantId, Guid itemId, Guid? lotId, string type, decimal quantity, Guid? workOrderId, Guid? maintWoId, string? reference,
        Guid userId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            INSERT inventory.Movements (TenantId, ItemId, LotId, Type, Quantity, WorkOrderId, MaintWoId, Reference, UserId)
            VALUES (@tenantId, @itemId, @lotId, @type, @quantity, @workOrderId, @maintWoId, @reference, @userId)
            """, new { tenantId, itemId, lotId, type, quantity, workOrderId, maintWoId, reference, userId }, cancellationToken: ct));
    }

    public async Task SetLotStatusFromStockAsync(Guid tenantId, Guid lotId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            UPDATE inventory.Lots SET Status = CASE WHEN ISNULL((SELECT SUM(Quantity) FROM inventory.Movements WHERE LotId = @lotId), 0) <= 0 THEN 'Consumed' ELSE Status END
            WHERE TenantId = @tenantId AND LotId = @lotId AND Status = 'Available'
            """, new { tenantId, lotId }, cancellationToken: ct));
    }

    public async Task<WorkOrderRef?> WorkOrderAsync(Guid tenantId, Guid workOrderId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.QuerySingleOrDefaultAsync<WorkOrderRef>(new CommandDefinition("""
            SELECT w.WorkOrderId, w.Code, w.ProductId, p.Name AS ProductName, w.AssetId, a.Name AS AssetName, w.Status, w.ActualStart, w.ActualEnd,
                   ISNULL(w.GoodQty, 0) AS Produced
            FROM production.WorkOrders w JOIN production.Products p ON p.ProductId = w.ProductId JOIN plant.Assets a ON a.AssetId = w.AssetId
            WHERE w.TenantId = @tenantId AND w.WorkOrderId = @workOrderId
            """, new { tenantId, workOrderId }, cancellationToken: ct));
    }

    public async Task<bool> MaintWoExistsAsync(Guid tenantId, Guid maintWoId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM maintenance.WorkOrders WHERE TenantId = @tenantId AND MaintWoId = @maintWoId",
            new { tenantId, maintWoId }, cancellationToken: ct)) > 0;
    }

    public async Task<IReadOnlyList<TraceStep>> QualityForLotAsync(Guid tenantId, string lotNo, Guid? workOrderId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<TraceStep>(new CommandDefinition("""
            SELECT 'measurement' AS Kind, c.Name AS Title,
                   CONCAT(COUNT(*), ' values, ', FORMAT(MIN(m.Value), 'G6'), ' to ', FORMAT(MAX(m.Value), 'G6'), ' ', c.Unit,
                          CASE WHEN SUM(CASE WHEN m.Value < ISNULL(c.Lsl, -1e308) OR m.Value > ISNULL(c.Usl, 1e308) THEN 1 ELSE 0 END) > 0
                               THEN CONCAT(' — ', SUM(CASE WHEN m.Value < ISNULL(c.Lsl, -1e308) OR m.Value > ISNULL(c.Usl, 1e308) THEN 1 ELSE 0 END), ' out of spec')
                               ELSE ' — all in spec' END) AS Detail,
                   MAX(m.Ts) AS At, '/app/quality' AS Link
            FROM quality.Measurements m JOIN quality.Characteristics c ON c.CharId = m.CharId
            WHERE m.TenantId = @tenantId AND (m.LotNo = @lotNo OR (@workOrderId IS NOT NULL AND m.WorkOrderId = @workOrderId))
            GROUP BY c.Name, c.Unit
            UNION ALL
            SELECT 'ncr', CONCAT('Non-conformance ', n.Code, ': ', n.Title), CONCAT(n.Severity, ' · ', n.Status, ISNULL(' · ' + n.Disposition, '')), n.CreatedAt, '/app/quality'
            FROM quality.NonConformances n WHERE n.TenantId = @tenantId AND n.LotNo = @lotNo
            """, new { tenantId, lotNo, workOrderId }, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<TraceStep>> EventsAsync(Guid tenantId, Guid assetId, DateTime from, DateTime to, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<TraceStep>(new CommandDefinition("""
            SELECT 'alarm' AS Kind, CONCAT(al.Severity, ' alarm') AS Title, al.Message AS Detail, al.RaisedAt AS At, '/app/alarms' AS Link
            FROM alarms.Alarms al WHERE al.TenantId = @tenantId AND al.AssetId = @assetId AND al.RaisedAt BETWEEN @from AND @to
            UNION ALL
            SELECT 'downtime', CONCAT('Stopped (', d.State, ')'),
                   CONCAT(DATEDIFF(MINUTE, d.StartTs, ISNULL(d.EndTs, SYSUTCDATETIME())), ' min', ISNULL(' · ' + r.Name, '')), d.StartTs, '/app/production'
            FROM production.Downtimes d LEFT JOIN production.Reasons r ON r.ReasonCode = d.ReasonCode
            WHERE d.TenantId = @tenantId AND d.AssetId = @assetId AND d.StartTs < @to AND ISNULL(d.EndTs, SYSUTCDATETIME()) > @from
            ORDER BY At
            """, new { tenantId, assetId, from, to }, cancellationToken: ct))];
    }
}
