using System.Collections.Concurrent;
using Divakar.FactoryOS.Domain.Plant;
using Microsoft.Extensions.Logging;

namespace Divakar.FactoryOS.Application.Connectivity;

public sealed class IngestionOptions
{
    /// <summary>A value is stored at least this often even if it did not change, so charts show the device is alive.</summary>
    public int HeartbeatSeconds { get; set; } = 60;

    /// <summary>How often the list of assets and tags is reloaded, so configuration changes are picked up.</summary>
    public int RefreshBindingsSeconds { get; set; } = 30;

    public int MaxParallelReads { get; set; } = 16;
    public int ReadTimeoutMs { get; set; } = 10_000;

    /// <summary>Consecutive failed reads before an asset is shown as Offline.</summary>
    public int FailuresBeforeOffline { get; set; } = 3;
}

/// <summary>
/// The heart of data collection: keeps one connector per asset, reads each asset on its own interval,
/// scales values, stores them by exception (deadband) plus a heartbeat, and keeps each asset's health.
/// </summary>
public sealed class IngestionEngine(IIngestionStore store, ConnectorRegistry registry, IngestionOptions options, ILogger<IngestionEngine> log)
    : IAsyncDisposable
{
    private sealed class AssetRun(AssetBinding binding)
    {
        public AssetBinding Binding { get; set; } = binding;
        public IConnector? Connector { get; set; }
        public DateTime NextDue { get; set; }
        public int Failures { get; set; }
        public string? LastError { get; set; }
        public Dictionary<Guid, (double? Value, byte Quality, DateTime Ts)> LastStored { get; } = [];
    }

    private readonly ConcurrentDictionary<Guid, AssetRun> runs = new();
    private DateTime nextRefresh = DateTime.MinValue;

    public int AssetCount => runs.Count;

    /// <summary>One pass: reload configuration if due, read every asset that is due, write the results.</summary>
    public async Task<int> RunOnceAsync(DateTime now, CancellationToken ct)
    {
        if (now >= nextRefresh)
        {
            await RefreshAsync(ct);
            nextRefresh = now.AddSeconds(options.RefreshBindingsSeconds);
        }
        var due = runs.Values.Where(r => r.NextDue <= now).ToList();
        if (due.Count == 0)
        {
            return 0;
        }
        var rows = new ConcurrentBag<ReadingRow>();
        var health = new ConcurrentBag<AssetHealth>();
        await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelReads, CancellationToken = ct },
            async (run, token) =>
            {
                run.NextDue = now.AddMilliseconds(Math.Max(250, run.Binding.PollMs));
                var (r, h) = await ReadAssetAsync(run, now, token);
                foreach (var row in r)
                {
                    rows.Add(row);
                }
                health.Add(h);
            });
        if (!rows.IsEmpty)
        {
            await store.WriteAsync([.. rows], ct);
        }
        await store.UpdateHealthAsync([.. health], ct);
        return rows.Count;
    }

    private async Task<(List<ReadingRow>, AssetHealth)> ReadAssetAsync(AssetRun run, DateTime now, CancellationToken ct)
    {
        var b = run.Binding;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.ReadTimeoutMs);
            if (run.Connector is null)
            {
                run.Connector = registry.Create(b);
                await run.Connector.ConnectAsync(timeout.Token);
            }
            var raw = await run.Connector.ReadAsync(now, timeout.Token);
            run.Failures = 0;
            run.LastError = null;
            var rows = Process(run, raw, now);
            return (rows, new AssetHealth(b.AssetId, StatusOf(b, rows), now, null));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            run.Failures++;
            run.LastError = ex.Message.Length > 480 ? ex.Message[..480] : ex.Message;
            log.LogWarning("Read failed for asset {Asset} ({Protocol}): {Error}", b.AssetCode, b.ProtocolCode, run.LastError);
            if (run.Connector is not null)
            {
                await SafeDispose(run.Connector);
                run.Connector = null;   // reconnect on the next poll
            }
            var offline = run.Failures >= options.FailuresBeforeOffline;
            var rows = offline ? MarkOffline(run, now) : [];
            return (rows, new AssetHealth(b.AssetId, offline ? "Offline" : "Unchanged", null, run.LastError));
        }
    }

    /// <summary>Scale, check limits and decide which values go to the history.</summary>
    private List<ReadingRow> Process(AssetRun run, IReadOnlyList<RawValue> raw, DateTime now)
    {
        var tags = run.Binding.Tags.ToDictionary(t => t.TagId);
        var rows = new List<ReadingRow>(raw.Count);
        foreach (var r in raw)
        {
            if (!tags.TryGetValue(r.TagId, out var tag))
            {
                continue;
            }
            var value = r.Value is { } v ? v * tag.Scale + tag.Offset : (double?)null;
            var quality = r.Quality;
            if (value is { } x && quality == Quality.Good && ((tag.MinValue is { } min && x < min) || (tag.MaxValue is { } max && x > max)))
            {
                quality = Quality.Uncertain;   // outside the sensor's range: keep it, but flag it
            }
            var store = ShouldStore(run, tag, value, quality, r.Ts);
            if (store)
            {
                run.LastStored[tag.TagId] = (value, quality, r.Ts);
            }
            rows.Add(new ReadingRow(run.Binding.TenantId, tag.TagId, r.Ts, value, quality, store));
        }
        return rows;
    }

    private bool ShouldStore(AssetRun run, TagBinding tag, double? value, byte quality, DateTime ts)
    {
        if (!run.LastStored.TryGetValue(tag.TagId, out var last))
        {
            return true;
        }
        if (last.Quality != quality || (ts - last.Ts).TotalSeconds >= options.HeartbeatSeconds)
        {
            return true;
        }
        if (value is null || last.Value is null)
        {
            return value != last.Value;
        }
        var change = Math.Abs(value.Value - last.Value.Value);
        return tag.Deadband > 0 ? change > tag.Deadband : change > 0;
    }

    private static List<ReadingRow> MarkOffline(AssetRun run, DateTime now)
    {
        var rows = new List<ReadingRow>();
        foreach (var tag in run.Binding.Tags)
        {
            var already = run.LastStored.TryGetValue(tag.TagId, out var last) && last.Quality == Quality.Offline;
            rows.Add(new ReadingRow(run.Binding.TenantId, tag.TagId, now, null, Quality.Offline, !already));
            if (!already)
            {
                run.LastStored[tag.TagId] = (null, Quality.Offline, now);
            }
        }
        return rows;
    }

    private static string StatusOf(AssetBinding b, List<ReadingRow> rows)
    {
        if (b.DerivedState)
        {
            return "Unchanged";   // power sense sets this machine's state from its power readings
        }
        var stateTag = b.Tags.FirstOrDefault(t => t.SensorTypeCode == "machine_state");
        if (stateTag is not null)
        {
            var state = rows.FirstOrDefault(r => r.TagId == stateTag.TagId);
            return state is { Quality: Quality.Good or Quality.Uncertain } ? MachineStates.ToStatus(state.Value) : "Offline";
        }
        // no state tag: online means running (meters, sensors, utilities)
        return rows.Any(r => r.Quality is Quality.Good or Quality.Uncertain) ? "Running" : "Offline";
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var bindings = await store.ActiveBindingsAsync(registry.Protocols, ct);
        var wanted = bindings.ToDictionary(b => b.AssetId);
        foreach (var (id, run) in runs)
        {
            if (!wanted.TryGetValue(id, out var b))
            {
                runs.TryRemove(id, out _);
                if (run.Connector is not null)
                {
                    await SafeDispose(run.Connector);
                }
            }
            else if (!SameConnection(run.Binding, b) || !run.Binding.Tags.SequenceEqual(b.Tags))
            {
                if (run.Connector is not null)
                {
                    await SafeDispose(run.Connector);   // settings or tags changed: reconnect with the new ones
                }
                run.Connector = null;
                run.Binding = b;
            }
        }
        foreach (var b in bindings)
        {
            runs.TryAdd(b.AssetId, new AssetRun(b) { NextDue = DateTime.MinValue });
        }
    }

    private static bool SameConnection(AssetBinding a, AssetBinding b) =>
        a.ProtocolCode == b.ProtocolCode && a.Endpoint == b.Endpoint && a.Settings == b.Settings && a.PollMs == b.PollMs;

    private async Task SafeDispose(IConnector c)
    {
        try
        {
            await c.DisposeAsync();
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Connector dispose failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var run in runs.Values)
        {
            if (run.Connector is not null)
            {
                await SafeDispose(run.Connector);
            }
        }
        runs.Clear();
    }
}
