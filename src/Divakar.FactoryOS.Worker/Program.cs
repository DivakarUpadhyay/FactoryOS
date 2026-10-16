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
builder.Services.AddHostedService<IngestionWorker>();
builder.Services.AddHostedService<OperationsWorker>();

await builder.Build().RunAsync();
