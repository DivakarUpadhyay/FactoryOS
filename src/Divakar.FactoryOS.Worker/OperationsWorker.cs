using Divakar.FactoryOS.Application.Alarms;
using Divakar.FactoryOS.Application.Maintenance;
using Divakar.FactoryOS.Application.Notifications;
using Divakar.FactoryOS.Application.Production;
using Divakar.FactoryOS.Application.QualityControl;
using Divakar.FactoryOS.Application.Robotics;

namespace Divakar.FactoryOS.Worker;

/// <summary>
/// Shop-floor bookkeeping for every company, every 10 seconds: opens and closes downtime records from machine states.
/// Each job runs in its own scope and failures are logged without stopping the others.
/// </summary>
public sealed class OperationsWorker(IServiceScopeFactory scopes, ILogger<OperationsWorker> log) : BackgroundService
{
    private readonly List<(string Name, TimeSpan Every, Func<IServiceProvider, CancellationToken, Task> Run)> jobs =
    [
        ("downtime detection", TimeSpan.FromSeconds(10), (sp, ct) => sp.GetRequiredService<IProductionJobsStore>().DetectDowntimeAsync(ct)),
        ("fleet dispatch", TimeSpan.FromSeconds(2), (sp, ct) => sp.GetRequiredService<FleetDispatcher>().RunOnceAsync(DateTime.UtcNow, ct)),
        ("alarm rules", TimeSpan.FromSeconds(5), (sp, ct) => sp.GetRequiredService<AlarmEngine>().RunOnceAsync(DateTime.UtcNow, ct)),
        ("anomaly detection", TimeSpan.FromSeconds(60), (sp, ct) => sp.GetRequiredService<AnomalyDetector>().RunOnceAsync(DateTime.UtcNow, ct)),
        ("preventive maintenance", TimeSpan.FromSeconds(60), (sp, ct) => sp.GetRequiredService<PmScheduler>().RunOnceAsync(DateTime.UtcNow, ct)),
        ("load shedding", TimeSpan.FromSeconds(10), (sp, ct) => sp.GetRequiredService<Divakar.FactoryOS.Application.Energy.LoadShedEngine>().RunOnceAsync(DateTime.UtcNow, ct)),
        ("notifications", TimeSpan.FromSeconds(5), (sp, ct) => sp.GetRequiredService<NotificationSender>().RunOnceAsync(ct)),
        ("spc sampling", TimeSpan.FromSeconds(10), (sp, ct) => sp.GetRequiredService<SpcSampler>().RunOnceAsync(DateTime.UtcNow, ct)),
        ("report e-mails", TimeSpan.FromMinutes(5), (sp, ct) => sp.GetRequiredService<Divakar.FactoryOS.Application.Reports.ReportMailer>().RunOnceAsync(DateTime.UtcNow, ct)),
        ("monthly invoices", TimeSpan.FromHours(1), (sp, ct) => sp.GetRequiredService<Divakar.FactoryOS.Application.Platform.BillingService>()
            .GenerateAsync(DateOnly.FromDateTime(DateTime.UtcNow), ct)),
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);   // let ingestion fill the latest values first
        var due = jobs.ToDictionary(j => j.Name, _ => DateTime.UtcNow);
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var job in jobs)
            {
                if (DateTime.UtcNow < due[job.Name])
                {
                    continue;
                }
                due[job.Name] = DateTime.UtcNow + job.Every;
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await job.Run(scope.ServiceProvider, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    log.LogError(ex, "Job '{Job}' failed; it runs again on its next turn", job.Name);
                }
            }
            await Task.Delay(1000, stoppingToken);
        }
    }
}
