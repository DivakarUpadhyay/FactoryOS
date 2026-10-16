using P = Divakar.FactoryOS.Domain.Security.Permissions;

namespace Divakar.FactoryOS.Domain.Security;

/// <summary>A role template every new tenant starts with. Tenants can edit their copies or add their own roles.</summary>
public sealed record RoleTemplate(string Code, string Name, IReadOnlyList<string> Permissions);

public static class SystemRoles
{
    public const string FactoryAdmin = "factory-admin";

    public static readonly IReadOnlyList<RoleTemplate> Templates =
    [
        new(FactoryAdmin, "Factory administrator", P.TenantAll),
        new("plant-manager", "Plant manager",
        [
            P.UsersRead, P.AuditRead, P.PlantRead, P.EnergyRead, P.ProductionRead, P.ProductionManage,
            P.MaintenanceRead, P.QualityRead, P.RoboticsRead, P.AlarmsRead, P.AlarmsAcknowledge, P.ReportsRead,
        ]),
        new("supervisor", "Shift supervisor",
        [
            P.PlantRead, P.ProductionRead, P.ProductionOperate, P.MaintenanceRead, P.QualityRead,
            P.RoboticsRead, P.RoboticsOperate, P.AlarmsRead, P.AlarmsAcknowledge, P.ReportsRead,
        ]),
        new("operator", "Machine operator", [P.PlantRead, P.ProductionRead, P.ProductionOperate, P.AlarmsRead]),
        new("technician", "Maintenance technician",
            [P.PlantRead, P.MaintenanceRead, P.MaintenanceExecute, P.AlarmsRead, P.AlarmsAcknowledge]),
        new("quality-inspector", "Quality inspector", [P.PlantRead, P.QualityRead, P.QualityExecute, P.ProductionRead]),
        new("energy-manager", "Energy manager",
            [P.PlantRead, P.EnergyRead, P.EnergyManage, P.AlarmsRead, P.AlarmsAcknowledge, P.ReportsRead]),
        new("viewer", "Viewer (read only)",
            [P.PlantRead, P.EnergyRead, P.ProductionRead, P.MaintenanceRead, P.QualityRead, P.RoboticsRead, P.AlarmsRead]),
    ];
}
