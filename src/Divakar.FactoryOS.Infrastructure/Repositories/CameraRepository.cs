using System.Net;
using Dapper;
using Divakar.FactoryOS.Application.Plant;
using Divakar.FactoryOS.Infrastructure.Database;
using Divakar.FactoryOS.Infrastructure.Security;

namespace Divakar.FactoryOS.Infrastructure.Repositories;

public sealed class CameraRepository(Db db, SecretProtector protector) : ICameraRepository
{
    private sealed record Row(Guid AssetId, Guid TenantId, string SnapshotUrl, string? Username, string? Password, bool SnapOnAlarm);

    private CameraConfig Map(Row r) =>
        new(r.AssetId, r.TenantId, r.SnapshotUrl, r.Username, r.Password is null ? null : protector.Decrypt(r.Password), r.SnapOnAlarm);

    public async Task<CameraConfig?> GetAsync(Guid tenantId, Guid assetId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var row = await con.QuerySingleOrDefaultAsync<Row>(new CommandDefinition("""
            SELECT AssetId, TenantId, SnapshotUrl, Username, Password, SnapOnAlarm FROM plant.Cameras WHERE TenantId = @tenantId AND AssetId = @assetId
            """, new { tenantId, assetId }, cancellationToken: ct));
        return row is null ? null : Map(row);
    }

    public async Task SaveAsync(Guid tenantId, Guid assetId, string url, string? username, string? password, bool keepPassword, bool snapOnAlarm, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            MERGE plant.Cameras AS x USING (SELECT @assetId AS AssetId) AS s ON x.AssetId = s.AssetId
            WHEN MATCHED THEN UPDATE SET SnapshotUrl = @url, Username = @username, Password = CASE WHEN @keepPassword = 1 THEN Password ELSE @password END,
                SnapOnAlarm = @snapOnAlarm, UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (AssetId, TenantId, SnapshotUrl, Username, Password, SnapOnAlarm) VALUES (@assetId, @tenantId, @url, @username, @password, @snapOnAlarm);
            """, new { tenantId, assetId, url, username, password = password is null ? null : protector.Encrypt(password), keepPassword, snapOnAlarm },
            cancellationToken: ct));
    }

    public async Task DeleteAsync(Guid tenantId, Guid assetId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("DELETE plant.Cameras WHERE TenantId = @tenantId AND AssetId = @assetId", new { tenantId, assetId }, cancellationToken: ct));
    }

    public async Task<bool> AssetExistsAsync(Guid tenantId, Guid assetId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        return await con.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM plant.Assets WHERE TenantId = @tenantId AND AssetId = @assetId",
            new { tenantId, assetId }, cancellationToken: ct)) > 0;
    }

    private sealed record PendingRow(long AlarmId, Guid AssetId, Guid TenantId, string SnapshotUrl, string? Username, string? Password, bool SnapOnAlarm);

    public async Task<IReadOnlyList<(long AlarmId, CameraConfig Camera)>> AlarmsWithoutSnapshotAsync(DateTime since, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        var rows = await con.QueryAsync<PendingRow>(new CommandDefinition("""
            SELECT TOP (50) al.AlarmId, c.AssetId, c.TenantId, c.SnapshotUrl, c.Username, c.Password, c.SnapOnAlarm
            FROM alarms.Alarms al
            JOIN plant.Cameras c ON c.AssetId = al.AssetId AND c.SnapOnAlarm = 1
            WHERE al.RaisedAt >= @since AND NOT EXISTS (SELECT 1 FROM alarms.Snapshots s WHERE s.AlarmId = al.AlarmId)
            ORDER BY al.AlarmId
            """, new { since }, cancellationToken: ct));
        return [.. rows.Select(r => (r.AlarmId, Map(new Row(r.AssetId, r.TenantId, r.SnapshotUrl, r.Username, r.Password, r.SnapOnAlarm))))];
    }

    public async Task SaveAlarmSnapshotAsync(long alarmId, Guid tenantId, Snapshot? image, string? error, CancellationToken ct)
    {
        await using var con = await db.OpenSystemAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            IF NOT EXISTS (SELECT 1 FROM alarms.Snapshots WHERE AlarmId = @alarmId)
                INSERT alarms.Snapshots (AlarmId, TenantId, Image, ContentType, Error) VALUES (@alarmId, @tenantId, @data, @type, @error)
            """, new { alarmId, tenantId, data = image?.Data, type = image?.ContentType, error }, cancellationToken: ct));
    }

    private sealed record SnapRow(byte[]? Image, string? ContentType);

    public async Task<Snapshot?> AlarmSnapshotAsync(Guid tenantId, long alarmId, CancellationToken ct)
    {
        await using var con = await db.OpenAsync(ct);
        var row = await con.QuerySingleOrDefaultAsync<SnapRow>(new CommandDefinition(
            "SELECT Image, ContentType FROM alarms.Snapshots WHERE TenantId = @tenantId AND AlarmId = @alarmId AND Image IS NOT NULL",
            new { tenantId, alarmId }, cancellationToken: ct));
        return row?.Image is { } img ? new Snapshot(img, row.ContentType ?? "image/jpeg") : null;
    }
}

/// <summary>HTTP snapshot reader. The handler answers Basic and Digest challenges itself; pictures are limited to 5 MB.</summary>
public sealed class HttpCameraClient : ICameraClient
{
    private const int MaxBytes = 5 * 1024 * 1024;

    public async Task<Snapshot> GrabAsync(CameraConfig camera, CancellationToken ct)
    {
        var uri = new Uri(camera.SnapshotUrl);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (camera.Username is not null)
        {
            var credential = new NetworkCredential(camera.Username, camera.Password ?? "");
            var cache = new CredentialCache { { uri, "Basic", credential }, { uri, "Digest", credential } };
            handler.Credentials = cache;
        }
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        using var res = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("the camera refused the user name or password");
        }
        if (!res.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"the camera answered {(int)res.StatusCode} {res.ReasonPhrase}");
        }
        var type = res.Content.Headers.ContentType?.MediaType ?? "";
        // raster pictures only: an SVG can carry script
        if (type.ToLowerInvariant() is not ("image/jpeg" or "image/jpg" or "image/png" or "image/gif" or "image/webp" or "image/bmp"))
        {
            throw new InvalidOperationException($"the address returned {(type.Length > 0 ? type : "something")} instead of a picture");
        }
        if (res.Content.Headers.ContentLength > MaxBytes)
        {
            throw new InvalidOperationException("the picture is larger than 5 MB");
        }
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + n > MaxBytes)
            {
                throw new InvalidOperationException("the picture is larger than 5 MB");
            }
            buffer.Write(chunk, 0, n);
        }
        return new Snapshot(buffer.ToArray(), type);
    }
}
