using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Connectors;
using Divakar.FactoryOS.Edge;

// Divakar FactoryOS edge agent: reads the machines in the plant with the same drivers as the cloud worker, buffers on disk while the
// network is down, forwards to FactoryOS, and runs the OT detectors locally. Runs as a console app, a Windows service or a systemd unit.
var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddEnvironmentVariables("FACTORYOS_EDGE_");
builder.Services.AddWindowsService(o => o.ServiceName = "Divakar FactoryOS Edge");
builder.Services.AddSystemd();

var options = builder.Configuration.GetSection("Edge").Get<EdgeOptions>() ?? new EdgeOptions();
if (string.IsNullOrWhiteSpace(options.Key))
{
    Console.Error.WriteLine("Set Edge:Key (appsettings.json or FACTORYOS_EDGE_Edge__Key) to the key shown when the agent was created in FactoryOS.");
    return 2;
}
var data = Path.GetFullPath(options.DataPath, AppContext.BaseDirectory);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<EdgeState>();
builder.Services.AddSingleton(new DiskBuffer(Path.Combine(data, "buffer"), options.BufferMb * 1024L * 1024L));
builder.Services.AddSingleton(new ConfigCache(Path.Combine(data, "config.bin"), options.Key));
builder.Services.AddSingleton(sp => new CloudClient(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, options));
builder.Services.AddSingleton<EdgeAi>();
builder.Services.AddSingleton<UnsPublisher>();
builder.Services.AddSingleton<EdgeStore>();
builder.Services.AddConnectors();
builder.Services.AddSingleton<ConnectorRegistry>();
builder.Services.AddSingleton(new IngestionOptions());
builder.Services.AddSingleton(sp => new IngestionEngine(sp.GetRequiredService<EdgeStore>(), sp.GetRequiredService<ConnectorRegistry>(),
    sp.GetRequiredService<IngestionOptions>(), sp.GetRequiredService<ILogger<IngestionEngine>>()));
builder.Services.AddHostedService<EdgeWorker>();

await builder.Build().RunAsync();
return 0;
