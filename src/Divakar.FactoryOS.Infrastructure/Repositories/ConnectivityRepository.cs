using System.Data;
using Dapper;
using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Domain.Plant;
using Divakar.FactoryOS.Infrastructure.Database;
using Divakar.FactoryOS.Infrastructure.Security;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

internal static class BindingSql
{
    public const string Select = """
        SELECT a.TenantId, a.AssetId, a.Code AS AssetCode, a.AssetTypeCode, a.ProtocolCode, a.Endpoint, a.ConnectorSettings AS Settings,
               a.PollMs, a.RatedPowerKw,
               t.TagId, t.Code, t.SensorTypeCode, s.SimProfile AS Profile, t.Address, t.Scale, t.Offset, t.Deadband, t.MinValue, t.MaxValue
        FROM plant.Assets a
        JOIN plant.Tags t ON t.AssetId = a.AssetId AND t.IsActive = 1
        JOIN plant.SensorTypes s ON s.SensorTypeCode = t.SensorTypeCode
        """;

    private sealed record Row(Guid TenantId, Guid AssetId, string AssetCode, string AssetTypeCode, string ProtocolCode, string? Endpoint,
        string? Settings, int PollMs, decimal? RatedPowerKw, Guid TagId, string Code, string SensorTypeCode, string Profile, string? Address,
        double Scale, double Offset, double Deadband, double? MinValue, double? MaxValue);

    /// <summary>Reads bindings; connection secrets are decrypted here, just before a connector uses them.</summary>
    public static async Task<List<AssetBinding>> ReadAsync(IDbConnection con, string sql, object args, SecretProtector secrets, CancellationToken ct)
    {
        var rows = await con.QueryAsync<Row>(new CommandDefinition(sql, args, cancellationToken: ct));
        return [.. rows.GroupBy(r => r.AssetId).Select(g =>
        {
            var a = g.First();
            return new AssetBinding(a.TenantId, a.AssetId, a.AssetCode, a.AssetTypeCode, a.ProtocolCode, a.Endpoint, secrets.Unprotect(a.Settings), a.PollMs,
                a.RatedPowerKw, [.. g.OrderBy(r => r.Code).Select(r => new TagBinding(r.TagId, r.Code, r.SensorTypeCode, r.Profile, r.Address,
                    r.Scale, r.Offset, r.Deadband, r.MinValue, r.MaxValue))]);
        })];
    }
}

/// <summary>Used by the ingestion worker and HTTP push: works across all tenants with the system connection.</summary>
public sealed class IngestionStore(Db db, SecretProtector secrets) : IIngestionStore
{
    public async Task<IReadOnlyList<AssetBinding>> ActiveBindingsAsync(IReadOnlyCollection<string> protocols, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return await BindingSql.ReadAsync(con, BindingSql.Select + """
             JOIN core.Tenants tn ON tn.TenantId = a.TenantId
            WHERE a.IsActive = 1 AND a.ProtocolCode IN @protocols AND tn.Status IN ('Trial','Active')
            """, new { protocols }, secrets, ct);
    }

    public async Task WriteAsync(IReadOnlyList<ReadingRow> rows, CancellationToken ct)
    {
        var table = new DataTable();
        table.Columns.Add("TenantId", typeof(Guid));
        table.Columns.Add("TagId", typeof(Guid));
        table.Columns.Add("Ts", typeof(DateTime));
        table.Columns.Add("Value", typeof(double));
        table.Columns.Add("Quality", typeof(byte));
        table.Columns.Add("Store", typeof(bool));
        foreach (var r in rows)
        {
            table.Rows.Add(r.TenantId, r.TagId, r.Ts, r.Value is { } v && double.IsFinite(v) ? v : DBNull.Value, r.Quality, r.Store);
        }
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            INSERT telemetry.Readings (TenantId, TagId, Ts, Value, Quality)
            SELECT TenantId, TagId, Ts, Value, Quality FROM @rows WHERE Store = 1;

            WITH newest AS (
                SELECT TenantId, TagId, Ts, Value, Quality,
                       ROW_NUMBER() OVER (PARTITION BY TagId ORDER BY Ts DESC) AS rn
                FROM @rows)
            MERGE telemetry.Latest AS l
            USING (SELECT * FROM newest WHERE rn = 1) AS n ON l.TagId = n.TagId
            WHEN MATCHED AND n.Ts >= l.Ts THEN UPDATE SET Ts = n.Ts, Value = n.Value, Quality = n.Quality, UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (TagId, TenantId, Ts, Value, Quality) VALUES (n.TagId, n.TenantId, n.Ts, n.Value, n.Quality);
            """, new { rows = table.AsTableValuedParameter("telemetry.ReadingRows") }, cancellationToken: ct));
    }

    public async Task UpdateHealthAsync(IReadOnlyList<AssetHealth> health, CancellationToken ct)
    {
        if (health.Count == 0)
        {
            return;
        }
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            UPDATE plant.Assets
            SET Status = CASE WHEN @Status = 'Unchanged' THEN Status ELSE @Status END,
                LastSeenAt = COALESCE(@LastSeenAt, LastSeenAt),
                LastError = @LastError
            WHERE AssetId = @AssetId
            """, health, cancellationToken: ct));
    }
}

public sealed class ConnectivityRepository(Db db, SecretProtector secrets) : IConnectivityRepository
{
    private const string TagSelect = """
        SELECT TagId, AssetId, Code, Name, SensorTypeCode, Unit, Address, Scale, Offset, Deadband, MinValue, MaxValue, IsActive
        FROM plant.Tags WHERE TenantId = @tenantId
        """;

    public async Task<IReadOnlyList<SensorType>> SensorTypesAsync(CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<SensorType>(new CommandDefinition(
            "SELECT SensorTypeCode, Category, Name, Unit, MinValue, MaxValue, SimProfile FROM plant.SensorTypes ORDER BY Category, Name",
            cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<TagTemplate>> TemplatesAsync(string assetTypeCode, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<TagTemplate>(new CommandDefinition(
            "SELECT AssetTypeCode, TagCode, Name, SensorTypeCode FROM plant.TagTemplates WHERE AssetTypeCode = @assetTypeCode",
            new { assetTypeCode }, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<Tag>> TagsAsync(Guid tenantId, Guid assetId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<Tag>(new CommandDefinition(TagSelect + " AND AssetId = @assetId ORDER BY Code",
            new { tenantId, assetId }, cancellationToken: ct))];
    }

    public async Task<IReadOnlyList<Tag>> DeviceTagsAsync(Guid tenantId, Guid assetId, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return [.. await con.QueryAsync<Tag>(new CommandDefinition(TagSelect + " AND AssetId = @assetId",
            new { tenantId, assetId }, cancellationToken: ct))];
    }

    public async Task<Guid> SaveTagAsync(Guid tenantId, Guid assetId, Guid? tagId, SaveTagRequest r, string unit, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var id = tagId ?? Guid.NewGuid();
        await con.ExecuteAsync(new CommandDefinition("""
            IF EXISTS (SELECT 1 FROM plant.Tags WHERE TagId = @id AND TenantId = @tenantId)
                UPDATE plant.Tags SET Code = @Code, Name = @Name, SensorTypeCode = @SensorTypeCode, Unit = @unit, Address = @Address,
                       Scale = @scale, Offset = @offset, Deadband = @deadband, MinValue = @MinValue, MaxValue = @MaxValue, IsActive = @active
                WHERE TagId = @id AND TenantId = @tenantId;
            ELSE
                INSERT plant.Tags (TagId, TenantId, AssetId, Code, Name, SensorTypeCode, Unit, Address, Scale, Offset, Deadband, MinValue, MaxValue, IsActive)
                VALUES (@id, @tenantId, @assetId, @Code, @Name, @SensorTypeCode, @unit, @Address, @scale, @offset, @deadband, @MinValue, @MaxValue, @active);
            """, new
        {
            id, tenantId, assetId, r.Code, r.Name, r.SensorTypeCode, unit, r.Address, scale = r.Scale ?? 1, offset = r.Offset ?? 0,
            deadband = r.Deadband ?? 0, r.MinValue, r.MaxValue, active = r.IsActive ?? true,
        }, cancellationToken: ct));
        return id;
    }

    public async Task DeleteTagAsync(Guid tenantId, Guid tagId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            DELETE telemetry.Latest WHERE TagId = @tagId AND TenantId = @tenantId;
            DELETE plant.Tags WHERE TagId = @tagId AND TenantId = @tenantId;
            """, new { tenantId, tagId }, cancellationToken: ct));
    }

    public async Task SaveConnectionAsync(Guid tenantId, Guid assetId, int pollMs, string? settings, CancellationToken ct)
    {
        settings = secrets.Protect(settings);   // secrets are stored encrypted
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition(
            "UPDATE plant.Assets SET PollMs = @pollMs, ConnectorSettings = @settings WHERE AssetId = @assetId AND TenantId = @tenantId",
            new { tenantId, assetId, pollMs, settings }, cancellationToken: ct));
    }

    public async Task<(int PollMs, string? Settings)?> ConnectionAsync(Guid tenantId, Guid assetId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.QuerySingleOrDefaultAsync<(int, string?)?>(new CommandDefinition(
            "SELECT PollMs, ConnectorSettings FROM plant.Assets WHERE AssetId = @assetId AND TenantId = @tenantId",
            new { tenantId, assetId }, cancellationToken: ct));
    }

    private sealed record LiveRow(Guid TagId, string Code, string Name, string SensorTypeCode, string Unit, DateTime? Ts, double? Value, byte? Quality);

    public async Task<AssetLive?> LiveAsync(Guid tenantId, Guid assetId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var head = await con.QuerySingleOrDefaultAsync<(Guid AssetId, string Status, DateTime? LastSeenAt, string? LastError)?>(new CommandDefinition(
            "SELECT AssetId, Status, LastSeenAt, LastError FROM plant.Assets WHERE AssetId = @assetId AND TenantId = @tenantId",
            new { tenantId, assetId }, cancellationToken: ct));
        if (head is null)
        {
            return null;
        }
        var rows = await con.QueryAsync<LiveRow>(new CommandDefinition("""
            SELECT t.TagId, t.Code, t.Name, t.SensorTypeCode, t.Unit, l.Ts, l.Value, l.Quality
            FROM plant.Tags t LEFT JOIN telemetry.Latest l ON l.TagId = t.TagId
            WHERE t.TenantId = @tenantId AND t.AssetId = @assetId
            ORDER BY t.Code
            """, new { tenantId, assetId }, cancellationToken: ct));
        var h = head.Value;
        return new AssetLive(h.AssetId, h.Status, h.LastSeenAt, h.LastError,
            [.. rows.Select(r => new LiveValue(r.TagId, r.Code, r.Name, r.SensorTypeCode, r.Unit, r.Ts, r.Value, r.Quality))]);
    }

    public async Task<IReadOnlyList<HistoryPoint>> HistoryAsync(Guid tenantId, Guid tagId, DateTime from, DateTime to, int max, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var rows = await con.QueryAsync<HistoryPoint>(new CommandDefinition("""
            SELECT TOP (@max) Ts, Value, Quality FROM telemetry.Readings
            WHERE TenantId = @tenantId AND TagId = @tagId AND Ts >= @from AND Ts <= @to
            ORDER BY Ts DESC
            """, new { tenantId, tagId, from, to, max }, cancellationToken: ct));
        return [.. rows.Reverse()];
    }

    public async Task<IReadOnlyList<ConnectionRow>> ConnectionsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<ConnectionRow>(new CommandDefinition("""
            SELECT a.AssetId, a.Code, a.Name, a.AssetTypeCode, a.ProtocolCode, p.SupportLevel, a.Endpoint, a.PollMs, a.Status,
                   a.LastSeenAt, a.LastError, (SELECT COUNT(*) FROM plant.Tags t WHERE t.AssetId = a.AssetId) AS Tags
            FROM plant.Assets a LEFT JOIN plant.Protocols p ON p.ProtocolCode = a.ProtocolCode
            WHERE a.TenantId = @tenantId
            ORDER BY a.Code
            """, new { tenantId }, cancellationToken: ct))];
    }

    public async Task<AssetBinding?> BindingAsync(Guid tenantId, Guid assetId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var list = await BindingSql.ReadAsync(con, BindingSql.Select + " WHERE a.TenantId = @tenantId AND a.AssetId = @assetId AND a.ProtocolCode IS NOT NULL",
            new { tenantId, assetId }, secrets, ct);
        return list.FirstOrDefault();
    }

    public async Task<IReadOnlyList<DeviceKeyInfo>> DeviceKeysAsync(Guid tenantId, Guid assetId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return [.. await con.QueryAsync<DeviceKeyInfo>(new CommandDefinition("""
            SELECT Prefix, CreatedAt, LastUsedAt, RevokedAt FROM plant.DeviceKeys
            WHERE TenantId = @tenantId AND AssetId = @assetId ORDER BY CreatedAt DESC
            """, new { tenantId, assetId }, cancellationToken: ct))];
    }

    public async Task AddDeviceKeyAsync(Guid tenantId, Guid assetId, string hash, string prefix, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition(
            "INSERT plant.DeviceKeys (KeyHash, TenantId, AssetId, Prefix) VALUES (@hash, @tenantId, @assetId, @prefix)",
            new { hash, tenantId, assetId, prefix }, cancellationToken: ct));
    }

    public async Task RevokeDeviceKeysAsync(Guid tenantId, Guid assetId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition(
            "UPDATE plant.DeviceKeys SET RevokedAt = SYSUTCDATETIME() WHERE TenantId = @tenantId AND AssetId = @assetId AND RevokedAt IS NULL",
            new { tenantId, assetId }, cancellationToken: ct));
    }

    public async Task<(Guid TenantId, Guid AssetId)?> AssetForKeyAsync(string hash, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        return await con.QuerySingleOrDefaultAsync<(Guid, Guid)?>(new CommandDefinition("""
            UPDATE k SET LastUsedAt = SYSUTCDATETIME()
            OUTPUT inserted.TenantId, inserted.AssetId
            FROM plant.DeviceKeys k
            JOIN plant.Assets a ON a.AssetId = k.AssetId AND a.IsActive = 1
            JOIN core.Tenants t ON t.TenantId = k.TenantId AND t.Status IN ('Trial','Active')
            WHERE k.KeyHash = @hash AND k.RevokedAt IS NULL
            """, new { hash }, cancellationToken: ct));
    }
}
