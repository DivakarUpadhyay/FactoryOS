using Divakar.FactoryOS.Application;
using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Connectors;
using Divakar.FactoryOS.Infrastructure;
using Divakar.FactoryOS.Worker;

// Divakar FactoryOS ingestion worker: reads every connected asset and stores its readings.
var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddEnvironmentVariables("FACTORYOS_");
builder.Services.AddWindowsService(o => o.ServiceName = "Divakar FactoryOS Worker");   // no effect unless installed as a Windows service
builder.Services.AddScoped<WorkerTenantContext>();
builder.Services.AddScoped<ICurrentUser, SystemUser>();
builder.Services.AddSingleton<Divakar.FactoryOS.Application.Reports.ITenantRunner, TenantRunner>();
builder.Services.AddScoped<Divakar.FactoryOS.Application.Reports.ReportMailer>();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddConnectors();
builder.Services.AddSingleton(builder.Configuration.GetSection("Ingestion").Get<IngestionOptions>() ?? new IngestionOptions());
builder.Services.AddSingleton<ScopedIngestionStore>();
builder.Services.AddSingleton(sp => new IngestionEngine(sp.GetRequiredService<ScopedIngestionStore>(), sp.GetRequiredService<ConnectorRegistry>(),
    sp.GetRequiredService<IngestionOptions>(), sp.GetRequiredService<ILogger<IngestionEngine>>()));

// Demo history: `--backfill <fromUtc> [toUtc] [stepSeconds]` replays the built-in simulator over that time (default: until now,
// one reading every 5 minutes), so a demo factory has continuous history for baselines and reports, then exits. Only simulated
// assets are read; real devices are never polled for the past.
if (args is ["--backfill", var fromText, ..])
{
    using var host = builder.Build();
    var from = DateTime.SpecifyKind(DateTime.Parse(fromText, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
    var end = args.Length > 2 ? DateTime.SpecifyKind(DateTime.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc) : DateTime.UtcNow;
    var step = args.Length > 3 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 300;
    var simulator = new ConnectorRegistry(host.Services.GetServices<IConnectorFactory>().OfType<Divakar.FactoryOS.Connectors.Simulation.SimulatorConnectorFactory>());
    await using var engine = new IngestionEngine(host.Services.GetRequiredService<ScopedIngestionStore>(), simulator,
        new IngestionOptions { RefreshBindingsSeconds = int.MaxValue / 2 }, host.Services.GetRequiredService<ILogger<IngestionEngine>>());
    long rows = 0;
    for (var t = from; t < end; t = t.AddSeconds(step))
    {
        // one run must cover the whole gap (a restart would restart the simulated counters), so a slow write is retried
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                rows += await engine.RunOnceAsync(t, default);
                break;
            }
            catch (Microsoft.Data.SqlClient.SqlException) when (attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
            }
        }
        if (t.Minute == 0 && t.Hour % 12 == 0 && t.Second < step)
        {
            Console.WriteLine($"{t:yyyy-MM-dd HH:mm} UTC, {rows:N0} readings");
        }
    }
    Console.WriteLine($"Backfill done: {rows:N0} readings from {from:yyyy-MM-dd HH:mm} to {end:yyyy-MM-dd HH:mm} UTC.");
    return;
}

builder.Services.AddHostedService<IngestionWorker>();
builder.Services.AddHostedService<OperationsWorker>();

await builder.Build().RunAsync();
