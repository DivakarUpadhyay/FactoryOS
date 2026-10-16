using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Infrastructure.Database;
using Dapper;

namespace Divakar.FactoryOS.Worker;

/// <summary>Which company a background job is working for, when it acts inside one (reports); empty otherwise.</summary>
public sealed class WorkerTenantContext
{
    public Guid? TenantId { get; set; }
}

/// <summary>
/// The worker has no signed-in user: it serves every tenant through the system connection, or acts as one company
/// (its connections then carry that tenant, so row-level security applies) when a job sets the tenant context.
/// </summary>
public sealed class SystemUser(WorkerTenantContext context) : ICurrentUser
{
    public bool IsAuthenticated => context.TenantId is not null;
    public Guid UserId => Guid.Empty;
    public Guid? TenantId => context.TenantId;
    public bool IsSuperAdmin => false;
    public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
    public string? IpAddress => null;
    public Guid RequireTenant() => context.TenantId ?? throw new InvalidOperationException("This worker job has no tenant.");
}

/// <summary>Runs work in a fresh scope as one company.</summary>
public sealed class TenantRunner(IServiceScopeFactory scopes) : Divakar.FactoryOS.Application.Reports.ITenantRunner
{
    public async Task RunAsAsync(Guid tenantId, Func<IServiceProvider, Task> work, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<WorkerTenantContext>().TenantId = tenantId;
        await work(scope.ServiceProvider);
    }
}

/// <summary>Gives the long-lived engine a fresh database scope for every call.</summary>
public sealed class ScopedIngestionStore(IServiceScopeFactory scopes) : IIngestionStore
{
    private async Task<T> Run<T>(Func<IIngestionStore, Task<T>> work)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<IIngestionStore>());
    }

    public Task<IReadOnlyList<AssetBinding>> ActiveBindingsAsync(IReadOnlyCollection<string> protocols, CancellationToken ct) =>
        Run(s => s.ActiveBindingsAsync(protocols, ct));

    public Task WriteAsync(IReadOnlyList<ReadingRow> rows, CancellationToken ct) => Run(async s => { await s.WriteAsync(rows, ct); return 0; });

    public Task UpdateHealthAsync(IReadOnlyList<AssetHealth> health, CancellationToken ct) =>
        Run(async s => { await s.UpdateHealthAsync(health, ct); return 0; });
}

/// <summary>Runs the ingestion engine every 250 ms and removes raw readings older than the retention period once an hour.</summary>
public sealed class IngestionWorker(IngestionEngine engine, IServiceScopeFactory scopes, IConfiguration config, ILogger<IngestionWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retentionDays = config.GetValue("Telemetry:RetentionDays", 90);
        var nextStats = DateTime.UtcNow.AddMinutes(1);
        var nextCleanup = DateTime.UtcNow.AddMinutes(5);
        long rows = 0;
        log.LogInformation("Divakar FactoryOS ingestion started; raw readings kept for {Days} days", retentionDays);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                rows += await engine.RunOnceAsync(DateTime.UtcNow, stoppingToken);
                if (DateTime.UtcNow >= nextStats)
                {
                    log.LogInformation("{Assets} assets connected, {Rows} values processed in the last minute", engine.AssetCount, rows);
                    rows = 0;
                    nextStats = DateTime.UtcNow.AddMinutes(1);
                }
                if (DateTime.UtcNow >= nextCleanup)
                {
                    await CleanupAsync(retentionDays, stoppingToken);
                    nextCleanup = DateTime.UtcNow.AddHours(1);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "Ingestion pass failed; retrying");
                await Task.Delay(5000, stoppingToken);
            }
            await Task.Delay(250, stoppingToken);
        }
        await engine.DisposeAsync();
    }

    private async Task CleanupAsync(int days, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await using var con = await scope.ServiceProvider.GetRequiredService<Db>().OpenSystemAsync(ct);
        var removed = await con.ExecuteAsync(new CommandDefinition(
            "DELETE TOP (100000) FROM telemetry.Readings WHERE Ts < DATEADD(DAY, -@days, SYSUTCDATETIME())",
            new { days }, commandTimeout: 300, cancellationToken: ct));
        if (removed > 0)
        {
            log.LogInformation("Removed {Count} readings older than {Days} days", removed, days);
        }
    }
}
