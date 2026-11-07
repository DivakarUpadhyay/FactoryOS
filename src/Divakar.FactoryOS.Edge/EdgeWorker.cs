using Divakar.FactoryOS.Application.Connectivity;

namespace Divakar.FactoryOS.Edge;

/// <summary>
/// The agent's loops: keep the configuration (cloud, else cache), read the machines, forward the buffer, send findings and a
/// heartbeat. Reading never waits for the network: the machines are polled whether or not the cloud answers.
/// </summary>
public sealed class EdgeWorker(EdgeOptions options, EdgeState state, CloudClient cloud, ConfigCache cache, DiskBuffer buffer, EdgeStore store,
    EdgeAi ai, IngestionEngine engine, ILogger<EdgeWorker> log) : BackgroundService
{
    public static string Version => typeof(EdgeWorker).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        state.Config = cache.Load();
        if (state.Config is not null)
        {
            log.LogInformation("Starting from the cached configuration: {Count} asset(s)", state.Config.Assets.Count);
        }
        await Task.WhenAll(ConfigLoop(stop), ReadLoop(stop), ForwardLoop(stop), StatusLoop(stop));
    }

    private async Task ConfigLoop(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var wait = TimeSpan.FromSeconds(options.ConfigRefreshSeconds);
            try
            {
                var config = await cloud.ConfigAsync(stop);
                var changed = state.Config is null || state.Config.Assets.Count != config.Assets.Count
                    || state.Config.Assets.SelectMany(a => a.Tags).Count() != config.Assets.SelectMany(a => a.Tags).Count();
                state.Config = config;
                cache.Save(config);
                if (changed)
                {
                    log.LogInformation("Configuration from the cloud: agent '{Name}', {Count} asset(s)", config.Name, config.Assets.Count);
                }
                var skew = Math.Abs((config.ServerTime - DateTime.UtcNow).TotalSeconds);
                if (skew > 60)
                {
                    log.LogWarning("This computer's clock is {Skew:N0} s off the server's; readings are time-stamped here, so fix the clock (NTP)", skew);
                }
            }
            catch (Exception e) when (!stop.IsCancellationRequested)
            {
                state.LastError = $"config: {e.Message}";
                log.LogWarning("Could not fetch the configuration ({Error}); {Mode}", e.Message,
                    state.Config is null ? "no cached configuration yet, retrying in 30 s" : "working from the cached one");
                if (state.Config is null)
                {
                    wait = TimeSpan.FromSeconds(30);
                }
            }
            await Task.Delay(wait, stop);
        }
    }

    private async Task ReadLoop(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (state.Config is not null)
                {
                    await engine.RunOnceAsync(DateTime.UtcNow, stop);
                }
            }
            catch (Exception e) when (!stop.IsCancellationRequested)
            {
                state.LastError = $"read: {e.Message}";
                log.LogError(e, "Reading failed; continuing");
            }
            await Task.Delay(250, stop);
        }
    }

    private async Task ForwardLoop(CancellationToken stop)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                buffer.Roll();
                var sent = await buffer.DrainOneAsync(async batch =>
                {
                    var result = await cloud.UploadAsync(new EdgeUpload(batch, store.TakeHealth()), stop);
                    if (result.Rejected > 0)
                    {
                        log.LogWarning("The cloud rejected {Count} reading(s) (unknown tag or out-of-range time)", result.Rejected);
                    }
                }, options.BatchSize, stop);
                if (sent == 0)
                {
                    var health = store.TakeHealth();
                    if (health.Count > 0)
                    {
                        await cloud.UploadAsync(new EdgeUpload([], health), stop);
                    }
                }
                backoff = TimeSpan.FromSeconds(2);
                await Task.Delay(sent > 0 ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(2), stop);
            }
            catch (Exception e) when (!stop.IsCancellationRequested)
            {
                state.LastError = $"upload: {e.Message}";
                log.LogWarning("Upload failed ({Error}); readings stay on disk, retrying in {Wait:N0} s", e.Message, backoff.TotalSeconds);
                await Task.Delay(backoff, stop);
                backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
            }
        }
    }

    private async Task StatusLoop(CancellationToken stop)
    {
        var nextBeat = DateTime.MinValue;
        while (!stop.IsCancellationRequested)
        {
            var found = ai.Take();
            try
            {
                if (found.Count > 0)
                {
                    await cloud.InsightsAsync(found, stop);
                    log.LogInformation("Sent {Count} local finding(s)", found.Count);
                }
                if (DateTime.UtcNow >= nextBeat)
                {
                    await cloud.HeartbeatAsync(new EdgeHeartbeat(Version, Environment.MachineName, buffer.Pending(), buffer.Dropped, state.LastError), stop);
                    nextBeat = DateTime.UtcNow.AddSeconds(options.HeartbeatSeconds);
                    state.LastError = null;
                }
            }
            catch (Exception e) when (!stop.IsCancellationRequested)
            {
                ai.Requeue(found);
                log.LogDebug("Status call failed: {Error}", e.Message);
            }
            await Task.Delay(TimeSpan.FromSeconds(10), stop);
        }
    }
}
