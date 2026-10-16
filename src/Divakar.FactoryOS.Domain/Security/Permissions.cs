namespace Divakar.FactoryOS.Domain.Security;

/// <summary>Permission codes. They match core.Permissions (migration 0004) one to one.</summary>
public static class Permissions
{
    public const string TenantsManage = "platform.tenants.manage";
    public const string PlansManage = "platform.plans.manage";

    public const string UsersRead = "users.read";
    public const string UsersManage = "users.manage";
    public const string RolesManage = "roles.manage";
    public const string SettingsManage = "settings.manage";
    public const string AuditRead = "audit.read";

    public const string PlantRead = "plant.read";
    public const string PlantManage = "plant.manage";
    public const string ConnectivityManage = "connectivity.manage";

    public const string EnergyRead = "energy.read";
    public const string EnergyManage = "energy.manage";

    public const string ProductionRead = "production.read";
    public const string ProductionOperate = "production.operate";
    public const string ProductionManage = "production.manage";

    public const string MaintenanceRead = "maintenance.read";
    public const string MaintenanceExecute = "maintenance.execute";
    public const string MaintenanceManage = "maintenance.manage";

    public const string QualityRead = "quality.read";
    public const string QualityExecute = "quality.execute";
    public const string QualityManage = "quality.manage";

    public const string RoboticsRead = "robotics.read";
    public const string RoboticsOperate = "robotics.operate";

    public const string AlarmsRead = "alarms.read";
    public const string AlarmsAcknowledge = "alarms.acknowledge";
    public const string AlarmsManage = "alarms.manage";

    public const string ReportsRead = "reports.read";

    /// <summary>Only platform operators hold these; tenant roles can never be given them.</summary>
    public static readonly IReadOnlySet<string> PlatformOnly = new HashSet<string> { TenantsManage, PlansManage };

    /// <summary>Every permission a tenant role may hold.</summary>
    public static readonly IReadOnlyList<string> TenantAll =
    [
        UsersRead, UsersManage, RolesManage, SettingsManage, AuditRead,
        PlantRead, PlantManage, ConnectivityManage,
        EnergyRead, EnergyManage,
        ProductionRead, ProductionOperate, ProductionManage,
        MaintenanceRead, MaintenanceExecute, MaintenanceManage,
        QualityRead, QualityExecute, QualityManage,
        RoboticsRead, RoboticsOperate,
        AlarmsRead, AlarmsAcknowledge, AlarmsManage,
        ReportsRead,
    ];
}
