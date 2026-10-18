using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;

namespace Divakar.FactoryOS.Application.Plant;

/// <summary>A camera with its password decrypted; stays on the server.</summary>
public sealed record CameraConfig(Guid AssetId, Guid TenantId, string SnapshotUrl, string? Username, string? Password, bool SnapOnAlarm);

public sealed record CameraView(bool Configured, string? SnapshotUrl, string? Username, bool HasPassword, bool SnapOnAlarm);

/// <summary>Empty URL removes the camera; empty password keeps the stored one.</summary>
public sealed record SaveCameraRequest(string? SnapshotUrl, string? Username, string? Password, bool SnapOnAlarm = true);

public sealed record Snapshot(byte[] Data, string ContentType);

public interface ICameraRepository
{
    Task<CameraConfig?> GetAsync(Guid tenantId, Guid assetId, CancellationToken ct);
    Task SaveAsync(Guid tenantId, Guid assetId, string url, string? username, string? password, bool keepPassword, bool snapOnAlarm, CancellationToken ct);
    Task DeleteAsync(Guid tenantId, Guid assetId, CancellationToken ct);
    Task<bool> AssetExistsAsync(Guid tenantId, Guid assetId, CancellationToken ct);
    /// <summary>Alarms raised since <paramref name="since"/> on assets with a camera and no snapshot yet (all tenants).</summary>
    Task<IReadOnlyList<(long AlarmId, CameraConfig Camera)>> AlarmsWithoutSnapshotAsync(DateTime since, CancellationToken ct);
    Task SaveAlarmSnapshotAsync(long alarmId, Guid tenantId, Snapshot? image, string? error, CancellationToken ct);
    Task<Snapshot?> AlarmSnapshotAsync(Guid tenantId, long alarmId, CancellationToken ct);
}

/// <summary>Reads one image from a camera (Basic or Digest sign-in). Throws with a readable message when it cannot.</summary>
public interface ICameraClient
{
    Task<Snapshot> GrabAsync(CameraConfig camera, CancellationToken ct);
}

public sealed class CameraService(ICameraRepository repo, ICameraClient client, ICurrentUser me, IAuditLog audit)
{
    public async Task<CameraView> GetAsync(Guid assetId, CancellationToken ct)
    {
        var c = await repo.GetAsync(me.RequireTenant(), assetId, ct);
        return c is null ? new CameraView(false, null, null, false, true) : new CameraView(true, c.SnapshotUrl, c.Username, c.Password is not null, c.SnapOnAlarm);
    }

    public async Task SaveAsync(Guid assetId, SaveCameraRequest r, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        if (!await repo.AssetExistsAsync(tenantId, assetId, ct))
        {
            throw AppException.NotFound("Asset");
        }
        if (string.IsNullOrWhiteSpace(r.SnapshotUrl))
        {
            await repo.DeleteAsync(tenantId, assetId, ct);
            await audit.WriteAsync("camera.delete", "asset", assetId.ToString(), null, ct);
            return;
        }
        var url = r.SnapshotUrl.Trim();
        if (url.Length > 500 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw AppException.Validation("The snapshot URL is an http:// or https:// address of a JPEG image (put the user name and password in their own fields).");
        }
        var user = Guard.Optional(r.Username, "User name", 100);
        var password = string.IsNullOrEmpty(r.Password) ? null : r.Password;
        if (password is { Length: > 100 })
        {
            throw AppException.Validation("The camera password is too long.");
        }
        await repo.SaveAsync(tenantId, assetId, url, user, password, keepPassword: password is null && user is not null, r.SnapOnAlarm, ct);
        await audit.WriteAsync("camera.save", "asset", assetId.ToString(), new { host = uri.Host }, ct);
    }

    public async Task<Snapshot> SnapshotAsync(Guid assetId, CancellationToken ct)
    {
        var c = await repo.GetAsync(me.RequireTenant(), assetId, ct) ?? throw AppException.NotFound("Camera");
        try
        {
            return await client.GrabAsync(c, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw AppException.Validation($"The camera did not send a picture: {ex.Message}");
        }
    }

    public async Task<Snapshot> AlarmSnapshotAsync(long alarmId, CancellationToken ct) =>
        await repo.AlarmSnapshotAsync(me.RequireTenant(), alarmId, ct) ?? throw AppException.NotFound("Snapshot");
}

/// <summary>Worker job: one picture per new alarm on an asset with a camera, so the alarm shows what the machine looked like.</summary>
public sealed class CameraSnapshotJob(ICameraRepository repo, ICameraClient client)
{
    public async Task<int> RunOnceAsync(DateTime now, CancellationToken ct)
    {
        var taken = 0;
        foreach (var (alarmId, camera) in await repo.AlarmsWithoutSnapshotAsync(now.AddMinutes(-10), ct))
        {
            try
            {
                await repo.SaveAlarmSnapshotAsync(alarmId, camera.TenantId, await client.GrabAsync(camera, ct), null, ct);
                taken++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await repo.SaveAlarmSnapshotAsync(alarmId, camera.TenantId, null, ex.Message[..Math.Min(200, ex.Message.Length)], ct);
            }
        }
        return taken;
    }
}
