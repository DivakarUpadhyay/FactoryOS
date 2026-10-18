using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Alarms;
using Divakar.FactoryOS.Application.Assistant;
using Divakar.FactoryOS.Application.Platform;
using Divakar.FactoryOS.Application.Reports;
using Divakar.FactoryOS.Application.Inventory;
using Divakar.FactoryOS.Application.QualityControl;
using Divakar.FactoryOS.Application.Maintenance;
using Divakar.FactoryOS.Application.Notifications;
using Divakar.FactoryOS.Application.Auth;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Application.Energy;
using Divakar.FactoryOS.Application.Production;
using Divakar.FactoryOS.Application.Robotics;
using Divakar.FactoryOS.Application.Settings;
using Divakar.FactoryOS.Application.Live;
using Divakar.FactoryOS.Application.Plant;
using Divakar.FactoryOS.Application.Tenancy;
using Divakar.FactoryOS.Application.Users;
using Divakar.FactoryOS.Infrastructure.Database;
using Divakar.FactoryOS.Infrastructure.Repositories;
using Divakar.FactoryOS.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Divakar.FactoryOS.Infrastructure;

public static class DependencyInjection
{
    private static readonly HttpClient Webhooks = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly HttpClient AiHttp = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var db = new DbOptions
        {
            ConnectionString = config.GetConnectionString("FactoryOS")
                               ?? throw new InvalidOperationException("ConnectionStrings:FactoryOS is not set."),
        };
        // Dapper type handlers (DateOnly) must exist before the first query, whichever repository runs first
        DapperSetup.Once();
        var jwt = config.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
        services.AddSingleton(db);
        services.AddSingleton(jwt);
        services.AddSingleton(SecretProtector.FromConfiguration(config));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddScoped<Db>();
        services.AddScoped<IAuditLog, AuditLogWriter>();
        services.AddScoped<IAuditReader, AuditReader>();
        services.AddScoped<IAuthRepository, AuthRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IPlantRepository, PlantRepository>();
        services.AddScoped<IConnectivityRepository, ConnectivityRepository>();
        services.AddScoped<IIngestionStore, IngestionStore>();
        services.AddScoped<ILiveFeed, LiveFeedRepository>();
        services.AddScoped<IPowerSenseRepository, PowerSenseRepository>();
        services.AddScoped<IFixKnowledgeRepository, FixKnowledgeRepository>();
        services.AddScoped<IFloorRepository, FloorRepository>();
        services.AddScoped<IEnergyRepository, EnergyRepository>();
        services.AddScoped<ILoadShedRepository, LoadShedRepository>();
        services.AddScoped<IAssetBindingSource, AssetBindingSource>();
        services.AddScoped<ISettingsRepository, SettingsRepository>();
        services.AddScoped<IProductionRepository, ProductionRepository>();
        services.AddScoped<IPlanningRepository, PlanningRepository>();
        services.AddScoped<IProductionJobsStore, ProductionJobsStore>();
        services.AddScoped<IRoboticsRepository, RoboticsRepository>();
        services.AddScoped<IFleetStore, FleetStore>();
        services.AddScoped<IAlarmRepository, AlarmRepository>();
        services.AddScoped<IAlarmStore, AlarmStore>();
        services.AddScoped<IMaintenanceRepository, MaintenanceRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IQualityRepository, QualityRepository>();
        services.AddScoped<IInspectionRepository, InspectionRepository>();
        services.AddScoped<IQualityJobsStore, QualityJobsStore>();
        services.AddScoped<IInventoryRepository, InventoryRepository>();
        services.AddScoped<IDashboardRepository, DashboardRepository>();
        services.AddScoped<IReportScheduleRepository, ReportScheduleRepository>();
        services.AddScoped<IBillingRepository, BillingRepository>();
        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
        services.AddSingleton(config.GetSection("Ai").Get<AiOptions>() ?? new AiOptions());
        services.AddSingleton<IAiClient>(sp => new ClaudeClient(AiHttp, sp.GetRequiredService<AiOptions>()));
        services.AddSingleton(config.GetSection("Smtp").Get<SmtpOptions>() ?? new SmtpOptions());
        services.AddScoped<INotificationTransport, SmtpTransport>();
        services.AddScoped<INotificationTransport>(_ => new WebhookTransport(Webhooks));
        return services;
    }
}
