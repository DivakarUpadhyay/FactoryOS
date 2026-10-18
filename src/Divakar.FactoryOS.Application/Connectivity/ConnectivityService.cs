using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Domain.Plant;

namespace Divakar.FactoryOS.Application.Connectivity;

public sealed record SaveTagRequest(string Code, string Name, string SensorTypeCode, string? Unit, string? Address,
    double? Scale, double? Offset, double? Deadband, double? MinValue, double? MaxValue, bool? IsActive);

public sealed record ConnectionSettingsRequest(int? PollMs, string? ConnectorSettings);

public sealed record ConnectionSettings(int PollMs, string? ConnectorSettings);

public sealed record LiveValue(Guid TagId, string Code, string Name, string SensorTypeCode, string Unit, DateTime? Ts, double? Value, byte? Quality);

public sealed record AssetLive(Guid AssetId, string Status, DateTime? LastSeenAt, string? LastError, IReadOnlyList<LiveValue> Values);

public sealed record HistoryPoint(DateTime Ts, double? Value, byte Quality);

public sealed record ConnectionRow(Guid AssetId, string Code, string Name, string AssetTypeCode, string? ProtocolCode, string? SupportLevel,
    string? Endpoint, int PollMs, string Status, DateTime? LastSeenAt, string? LastError, int Tags);

public sealed record DeviceKeyInfo(string Prefix, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? RevokedAt);

public sealed record NewDeviceKey(string Key, string Prefix);

public sealed record TestResult(bool Connected, string? Error, IReadOnlyList<LiveValue> Values);

public sealed record PushReading(string Tag, double? Value, DateTime? Ts);
public sealed record PushRequest(IReadOnlyList<PushReading> Readings);
public sealed record PushResult(int Accepted, IReadOnlyList<string> UnknownTags);

public interface IConnectivityRepository
{
    Task<IReadOnlyList<SensorType>> SensorTypesAsync(CancellationToken ct);
    Task<IReadOnlyList<TagTemplate>> TemplatesAsync(string assetTypeCode, CancellationToken ct);
    Task<IReadOnlyList<Tag>> TagsAsync(Guid tenantId, Guid assetId, CancellationToken ct);
    Task<Guid> SaveTagAsync(Guid tenantId, Guid assetId, Guid? tagId, SaveTagRequest r, string unit, CancellationToken ct);
    Task DeleteTagAsync(Guid tenantId, Guid tagId, CancellationToken ct);
    Task SaveConnectionAsync(Guid tenantId, Guid assetId, int pollMs, string? settings, CancellationToken ct);
    Task<(int PollMs, string? Settings)?> ConnectionAsync(Guid tenantId, Guid assetId, CancellationToken ct);
    Task<AssetLive?> LiveAsync(Guid tenantId, Guid assetId, CancellationToken ct);
    Task<IReadOnlyList<HistoryPoint>> HistoryAsync(Guid tenantId, Guid tagId, DateTime from, DateTime to, int max, CancellationToken ct);
    Task<IReadOnlyList<ConnectionRow>> ConnectionsAsync(Guid tenantId, CancellationToken ct);
    Task<AssetBinding?> BindingAsync(Guid tenantId, Guid assetId, CancellationToken ct);
    Task<IReadOnlyList<DeviceKeyInfo>> DeviceKeysAsync(Guid tenantId, Guid assetId, CancellationToken ct);
    Task AddDeviceKeyAsync(Guid tenantId, Guid assetId, string hash, string prefix, CancellationToken ct);
    Task RevokeDeviceKeysAsync(Guid tenantId, Guid assetId, CancellationToken ct);

    /// <summary>Tags of an asset for a device posting readings (system connection: there is no signed-in user).</summary>
    Task<IReadOnlyList<Tag>> DeviceTagsAsync(Guid tenantId, Guid assetId, CancellationToken ct);

    /// <summary>The asset a device key belongs to (system connection: the caller is a device, not a user).</summary>
    Task<(Guid TenantId, Guid AssetId)?> AssetForKeyAsync(string hash, CancellationToken ct);
}

public sealed class ConnectivityService(
    IConnectivityRepository repo, Plant.IPlantRepository plant, ConnectorRegistry registry, IIngestionStore ingest,
    ITokenService tokens, ICurrentUser me, IClock clock, IAuditLog audit)
{
    public Task<IReadOnlyList<SensorType>> SensorTypesAsync(CancellationToken ct) => repo.SensorTypesAsync(ct);

    public async Task<IReadOnlyList<Tag>> TagsAsync(Guid assetId, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        await RequireAsset(tenantId, assetId, ct);
        return await repo.TagsAsync(tenantId, assetId, ct);
    }

    public async Task<Guid> SaveTagAsync(Guid assetId, Guid? tagId, SaveTagRequest r, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        await RequireAsset(tenantId, assetId, ct);
        var code = Guard.Code(r.Code, "Code");
        Guard.Required(r.Name, "Name", 100);
        Guard.Optional(r.Address, "Address", 300);
        var sensor = (await repo.SensorTypesAsync(ct)).FirstOrDefault(s => s.SensorTypeCode == r.SensorTypeCode)
                     ?? throw AppException.Validation("Unknown sensor type.");
        if (r.Scale == 0)
        {
            throw AppException.Validation("Scale cannot be zero.");
        }
        if (r.Deadband < 0)
        {
            throw AppException.Validation("Deadband cannot be negative.");
        }
        if (r.MinValue is { } min && r.MaxValue is { } max && min >= max)
        {
            throw AppException.Validation("Minimum must be below maximum.");
        }
        var existing = await repo.TagsAsync(tenantId, assetId, ct);
        if (tagId is { } id && existing.All(t => t.TagId != id))
        {
            throw AppException.NotFound("Tag");
        }
        if (existing.Any(t => t.Code == code && t.TagId != tagId))
        {
            throw AppException.Conflict($"Tag code '{code}' is already used on this asset.");
        }
        var saved = await repo.SaveTagAsync(tenantId, assetId, tagId, r with { Code = code, Name = r.Name.Trim() },
            Guard.Optional(r.Unit, "Unit", 20) ?? sensor.Unit, ct);
        await audit.WriteAsync(tagId is null ? "tag.create" : "tag.update", "tag", saved.ToString(), new { assetId, code, r.SensorTypeCode, r.Address }, ct);
        return saved;
    }

    public async Task DeleteTagAsync(Guid assetId, Guid tagId, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        if ((await repo.TagsAsync(tenantId, assetId, ct)).All(t => t.TagId != tagId))
        {
            throw AppException.NotFound("Tag");
        }
        await repo.DeleteTagAsync(tenantId, tagId, ct);
        await audit.WriteAsync("tag.delete", "tag", tagId.ToString(), null, ct);
    }

    /// <summary>Adds the standard tags for the asset's type; tags that already exist are left alone.</summary>
    public async Task<int> ApplyTemplateAsync(Guid assetId, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        var asset = await RequireAsset(tenantId, assetId, ct);
        var sensors = (await repo.SensorTypesAsync(ct)).ToDictionary(s => s.SensorTypeCode);
        var have = (await repo.TagsAsync(tenantId, assetId, ct)).Select(t => t.Code).ToHashSet();
        var added = 0;
        foreach (var t in await repo.TemplatesAsync(asset.AssetTypeCode, ct))
        {
            if (have.Contains(t.TagCode))
            {
                continue;
            }
            var s = sensors[t.SensorTypeCode];
            await repo.SaveTagAsync(tenantId, assetId, null,
                new SaveTagRequest(t.TagCode, t.Name, s.SensorTypeCode, s.Unit, null, 1, 0, DefaultDeadband(s), s.MinValue, s.MaxValue, true),
                s.Unit, ct);
            added++;
        }
        await audit.WriteAsync("tag.template", "asset", assetId.ToString(), new { added }, ct);
        return added;
    }

    /// <summary>Small deadbands keep history compact without losing meaningful changes.</summary>
    public static double DefaultDeadband(SensorType s) => s.SimProfile switch
    {
        "temp" => 0.2,
        "vibration" => 0.05,
        "power" or "reactive" or "solar_power" => 0.1,
        "current" => 0.2,
        "voltage" => 0.5,
        "pressure" => 0.02,
        "flow" => 0.1,
        "env_humidity" or "env_slow" or "noise" or "co2" => 1,
        "speed" or "load" => 0.5,
        _ => 0,
    };

    public async Task SaveConnectionAsync(Guid assetId, ConnectionSettingsRequest r, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        await RequireAsset(tenantId, assetId, ct);
        var poll = r.PollMs ?? 5000;
        if (poll is < 250 or > 3_600_000)
        {
            throw AppException.Validation("Poll interval must be between 250 ms and one hour.");
        }
        var settings = string.IsNullOrWhiteSpace(r.ConnectorSettings) ? null : r.ConnectorSettings.Trim();
        if (settings is not null)
        {
            var old = (await repo.ConnectionAsync(tenantId, assetId, ct))?.Settings;
            settings = SecretMask.Restore(settings, old) ?? throw AppException.Validation("Connector settings must be a JSON object.");
        }
        await repo.SaveConnectionAsync(tenantId, assetId, poll, settings, ct);
        await audit.WriteAsync("asset.connection", "asset", assetId.ToString(), new { poll }, ct);
    }

    /// <summary>Poll interval and settings; secrets (password, token, key) come back masked.</summary>
    public async Task<ConnectionSettings> ConnectionAsync(Guid assetId, CancellationToken ct)
    {
        var c = await repo.ConnectionAsync(me.RequireTenant(), assetId, ct) ?? throw AppException.NotFound("Asset");
        return new ConnectionSettings(c.PollMs, SecretMask.Mask(c.Settings));
    }

    public async Task<AssetLive> LiveAsync(Guid assetId, CancellationToken ct) =>
        await repo.LiveAsync(me.RequireTenant(), assetId, ct) ?? throw AppException.NotFound("Asset");

    public Task<IReadOnlyList<HistoryPoint>> HistoryAsync(Guid tagId, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var end = to ?? clock.UtcNow;
        var start = from ?? end.AddHours(-1);
        if (start >= end || end - start > TimeSpan.FromDays(31))
        {
            throw AppException.Validation("Choose a range of at most 31 days, with the start before the end.");
        }
        return repo.HistoryAsync(me.RequireTenant(), tagId, start, end, 5000, ct);
    }

    public Task<IReadOnlyList<ConnectionRow>> ConnectionsAsync(CancellationToken ct) => repo.ConnectionsAsync(me.RequireTenant(), ct);

    /// <summary>Connects with the asset's current settings, reads every tag once, and reports what happened. Nothing is stored.</summary>
    public async Task<TestResult> TestAsync(Guid assetId, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        var b = await repo.BindingAsync(tenantId, assetId, ct) ?? throw AppException.Validation("Choose a protocol for this asset first.");
        if (!registry.Supports(b.ProtocolCode))
        {
            return new TestResult(false, $"'{b.ProtocolCode}' has no built-in driver. Connect it through an OPC UA or MQTT gateway, or push data over HTTP.", []);
        }
        var tags = b.Tags.ToDictionary(t => t.TagId);
        var all = await repo.TagsAsync(tenantId, assetId, ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await using var c = registry.Create(b);
            await c.ConnectAsync(timeout.Token);
            if (b.ProtocolCode == "mqtt")
            {
                await Task.Delay(1500, timeout.Token);   // give subscriptions a moment to receive retained or fresh messages
            }
            var raw = await c.ReadAsync(clock.UtcNow, timeout.Token);
            var values = raw.Where(r => tags.ContainsKey(r.TagId)).Select(r =>
            {
                var t = all.First(x => x.TagId == r.TagId);
                return new LiveValue(t.TagId, t.Code, t.Name, t.SensorTypeCode, t.Unit, r.Ts,
                    r.Value is { } v ? v * t.Scale + t.Offset : null, r.Quality);
            }).ToList();
            return new TestResult(true, null, values);
        }
        catch (Exception ex) when (ex is not AppException)
        {
            return new TestResult(false, ex.Message, []);
        }
    }

    public async Task<IReadOnlyList<DeviceKeyInfo>> DeviceKeysAsync(Guid assetId, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        await RequireAsset(tenantId, assetId, ct);
        return await repo.DeviceKeysAsync(tenantId, assetId, ct);
    }

    /// <summary>A new key for HTTP push. Earlier keys of the asset are revoked. The key is shown only now.</summary>
    public async Task<NewDeviceKey> NewDeviceKeyAsync(Guid assetId, CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        await RequireAsset(tenantId, assetId, ct);
        var (token, hash) = tokens.CreateRefreshToken();
        var key = "fos_" + token;
        hash = tokens.HashRefreshToken(key);
        await repo.RevokeDeviceKeysAsync(tenantId, assetId, ct);
        await repo.AddDeviceKeyAsync(tenantId, assetId, hash, key[..8], ct);
        await audit.WriteAsync("devicekey.create", "asset", assetId.ToString(), new { prefix = key[..8] }, ct);
        return new NewDeviceKey(key, key[..8]);
    }

    /// <summary>Readings posted by a device with its key. Tags are matched by code; values are scaled like any other.</summary>
    public async Task<PushResult> PushAsync(string? deviceKey, PushRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
        {
            throw AppException.Unauthorized("Missing device key.");
        }
        var owner = await repo.AssetForKeyAsync(tokens.HashRefreshToken(deviceKey.Trim()), ct)
                    ?? throw AppException.Unauthorized("Unknown or revoked device key.");
        if (request.Readings is null || request.Readings.Count is 0 or > 1000)
        {
            throw AppException.Validation("Send between 1 and 1000 readings.");
        }
        var now = clock.UtcNow;
        var all = (await repo.DeviceTagsAsync(owner.TenantId, owner.AssetId, ct)).Where(t => t.IsActive).ToList();
        var derived = all.Any(t => t.Address?.StartsWith(PowerSenseTags.Prefix, StringComparison.Ordinal) == true);   // state comes from power sense
        var tags = all.Where(t => t.Address?.StartsWith(PowerSenseTags.Prefix, StringComparison.Ordinal) != true).ToDictionary(t => t.Code);
        var rows = new List<ReadingRow>();
        var unknown = new List<string>();
        foreach (var r in request.Readings)
        {
            if (!tags.TryGetValue(r.Tag ?? "", out var t))
            {
                unknown.Add(r.Tag ?? "");
                continue;
            }
            var ts = r.Ts?.ToUniversalTime() ?? now;
            if (ts > now.AddMinutes(5) || ts < now.AddDays(-7))
            {
                throw AppException.Validation("Timestamps must be within the last 7 days and not in the future.");
            }
            var value = r.Value is { } v ? v * t.Scale + t.Offset : (double?)null;
            rows.Add(new ReadingRow(owner.TenantId, t.TagId, ts, value, value is null ? Quality.Bad : Quality.Good, true));
        }
        if (rows.Count > 0)
        {
            await ingest.WriteAsync(rows, ct);
            // a pushed machine state sets the status; power-sense machines get theirs from the power job; anything else is running
            var stateTag = tags.Values.FirstOrDefault(t => t.SensorTypeCode == "machine_state");
            var state = stateTag is null ? null : rows.Where(r => r.TagId == stateTag.TagId && r.Value is not null).MaxBy(r => r.Ts);
            var status = state is not null ? MachineStates.ToStatus(state.Value) : derived ? "Unchanged" : "Running";
            await ingest.UpdateHealthAsync([new AssetHealth(owner.AssetId, status, now, null)], ct);
        }
        return new PushResult(rows.Count, unknown.Distinct().ToList());
    }

    private async Task<Asset> RequireAsset(Guid tenantId, Guid assetId, CancellationToken ct) =>
        await plant.AssetAsync(tenantId, assetId, ct) ?? throw AppException.NotFound("Asset");
}

/// <summary>Keeps device secrets out of API responses: they are shown masked and a masked value on save keeps the stored one.</summary>
public static class SecretMask
{
    public const string Masked = "********";

    private static bool IsSecret(string key) =>
        key.Contains("password", StringComparison.OrdinalIgnoreCase) || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("token", StringComparison.OrdinalIgnoreCase) || key.Equals("key", StringComparison.OrdinalIgnoreCase);

    public static string? Mask(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonObject o)
        {
            return json;
        }
        foreach (var key in o.Select(p => p.Key).Where(IsSecret).ToList())
        {
            o[key] = Masked;
        }
        return o.ToJsonString();
    }

    /// <summary>The new settings with masked secrets replaced by the stored values; null when the input is not a JSON object.</summary>
    public static string? Restore(string json, string? stored)
    {
        System.Text.Json.Nodes.JsonObject? incoming;
        try
        {
            incoming = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        if (incoming is null)
        {
            return null;
        }
        var old = string.IsNullOrWhiteSpace(stored) ? null : System.Text.Json.Nodes.JsonNode.Parse(stored) as System.Text.Json.Nodes.JsonObject;
        foreach (var key in incoming.Select(p => p.Key).Where(IsSecret).ToList())
        {
            if (incoming[key]?.GetValue<string>() == Masked)
            {
                incoming[key] = old?[key]?.DeepClone();
            }
        }
        return incoming.ToJsonString();
    }
}
