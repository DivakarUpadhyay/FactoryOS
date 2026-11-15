// Online demo (NEXT_PUBLIC_DEMO=1): one sign-in per system role, so every role's view of FactoryOS can be shown.
// The roles and permissions are the same as SystemRoles / Permissions in Divakar.FactoryOS.Domain.Security.
export const DEMO_PASSWORD = "FactoryDemo@2026";

const ALL = [
  "users.read", "users.manage", "roles.manage", "settings.manage", "audit.read",
  "plant.read", "plant.manage", "connectivity.manage",
  "energy.read", "energy.manage",
  "production.read", "production.operate", "production.manage",
  "maintenance.read", "maintenance.execute", "maintenance.manage",
  "quality.read", "quality.execute", "quality.manage",
  "robotics.read", "robotics.operate",
  "alarms.read", "alarms.acknowledge", "alarms.manage",
  "reports.read",
];

export type DemoAccount = { role: string; roleName: string; email: string; displayName: string; userId: string; permissions: string[]; sees: string };

export const DEMO_ACCOUNTS: DemoAccount[] = [
  { role: "factory-admin", roleName: "Factory administrator", email: "admin@divakar-demo.local", displayName: "Demo Factory Admin",
    userId: "d0000000-0000-4000-8000-000000000001", permissions: ALL, sees: "Everything: users, roles, settings, audit, reports" },
  { role: "plant-manager", roleName: "Plant manager", email: "manager@divakar-demo.local", displayName: "Demo Plant Manager",
    userId: "d0000000-0000-4000-8000-000000000002",
    permissions: ["users.read", "audit.read", "plant.read", "energy.read", "production.read", "production.manage", "maintenance.read",
      "quality.read", "robotics.read", "alarms.read", "alarms.acknowledge", "reports.read"],
    sees: "All modules, users and audit (read), reports" },
  { role: "supervisor", roleName: "Shift supervisor", email: "supervisor@divakar-demo.local", displayName: "Demo Shift Supervisor",
    userId: "d0000000-0000-4000-8000-000000000003",
    permissions: ["plant.read", "production.read", "production.operate", "maintenance.read", "quality.read", "robotics.read",
      "robotics.operate", "alarms.read", "alarms.acknowledge", "reports.read"],
    sees: "Production, alarms, robotics, reports" },
  { role: "operator", roleName: "Machine operator", email: "operator@divakar-demo.local", displayName: "Demo Machine Operator",
    userId: "d0000000-0000-4000-8000-000000000004", permissions: ["plant.read", "production.read", "production.operate", "alarms.read"],
    sees: "Plant, production terminal, alarms" },
  { role: "technician", roleName: "Maintenance technician", email: "technician@divakar-demo.local", displayName: "Demo Maintenance Technician",
    userId: "d0000000-0000-4000-8000-000000000005",
    permissions: ["plant.read", "maintenance.read", "maintenance.execute", "alarms.read", "alarms.acknowledge"],
    sees: "Plant, maintenance work orders, alarms" },
  { role: "quality-inspector", roleName: "Quality inspector", email: "quality@divakar-demo.local", displayName: "Demo Quality Inspector",
    userId: "d0000000-0000-4000-8000-000000000006", permissions: ["plant.read", "quality.read", "quality.execute", "production.read"],
    sees: "Quality inspections, SPC, NCRs, production" },
  { role: "energy-manager", roleName: "Energy manager", email: "energy@divakar-demo.local", displayName: "Demo Energy Manager",
    userId: "d0000000-0000-4000-8000-000000000007",
    permissions: ["plant.read", "energy.read", "energy.manage", "alarms.read", "alarms.acknowledge", "reports.read"],
    sees: "Energy, power, carbon, ISO 50001, reports" },
  { role: "viewer", roleName: "Viewer (read only)", email: "demo@divakar-demo.local", displayName: "Demo Viewer",
    userId: "08da718d-e3d8-45fd-8660-949362322105",
    permissions: ["alarms.read", "energy.read", "maintenance.read", "plant.read", "production.read", "quality.read", "robotics.read"],
    sees: "Every module, read only" },
];

export const demoAccountByEmail = (email?: string | null) =>
  DEMO_ACCOUNTS.find((a) => a.email === email?.trim().toLowerCase());
