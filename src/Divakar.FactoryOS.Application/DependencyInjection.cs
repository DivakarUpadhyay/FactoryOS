using Divakar.FactoryOS.Application.Alarms;
using Divakar.FactoryOS.Application.Assistant;
using Divakar.FactoryOS.Application.Platform;
using Divakar.FactoryOS.Application.Reports;
using Divakar.FactoryOS.Application.Inventory;
using Divakar.FactoryOS.Application.QualityControl;
using Divakar.FactoryOS.Application.Auth;
using Divakar.FactoryOS.Application.Maintenance;
using Divakar.FactoryOS.Application.Notifications;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Application.Energy;
using Divakar.FactoryOS.Application.Live;
using Divakar.FactoryOS.Application.Production;
using Divakar.FactoryOS.Application.Robotics;
using Divakar.FactoryOS.Application.Settings;
using Divakar.FactoryOS.Application.Plant;
using Divakar.FactoryOS.Application.Tenancy;
using Divakar.FactoryOS.Application.Users;
using Microsoft.Extensions.DependencyInjection;

namespace Divakar.FactoryOS.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(new AuthOptions());
        services.AddScoped<AuthService>();
        services.AddScoped<TenantService>();
        services.AddScoped<UserService>();
        services.AddScoped<PlantService>();
        services.AddScoped<AuditQuery>();
        services.AddScoped<ConnectivityService>();
        services.AddScoped<DemoFactoryService>();
        services.AddScoped<PowerSenseService>();
        services.AddScoped<PowerSenseJob>();
        services.AddScoped<FloorService>();
        services.AddScoped<SettingsService>();
        services.AddScoped<ModuleGate>();
        services.AddScoped<EnergyService>();
        services.AddScoped<EnergyPlanningService>();
        services.AddScoped<LoadShedEngine>();
        services.AddScoped<ProductionService>();
        services.AddScoped<PlanningService>();
        services.AddScoped<RoboticsService>();
        services.AddScoped<FleetDispatcher>();
        services.AddScoped<AlarmService>();
        services.AddScoped<AlarmEngine>();
        services.AddScoped<AnomalyDetector>();
        services.AddScoped<MaintenanceService>();
        services.AddScoped<PmScheduler>();
        services.AddScoped<IAlarmWorkOrders>(sp => sp.GetRequiredService<PmScheduler>());
        services.AddScoped<IAlarmNotifier, AlarmNotifier>();
        services.AddScoped<NotificationSender>();
        services.AddScoped<NotificationService>();
        services.AddScoped<QualityService>();
        services.AddScoped<InspectionService>();
        services.AddScoped<SpcSampler>();
        services.AddScoped<InventoryService>();
        services.AddScoped<ReportService>();
        services.AddScoped<DashboardService>();
        services.AddSingleton(new BillingOptions());
        services.AddScoped<BillingService>();
        services.AddScoped<ApiKeyService>();
        services.AddScoped<AssistantService>();
        services.AddSingleton<ConnectorRegistry>();
        return services;
    }
}
