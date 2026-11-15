// Online demo: answers for the pages the recorded snapshot can't cover, because it was recorded as the read-only viewer
// (users, roles, audit, reports). Users and roles come from the same role list as the .NET SystemRoles; reports are
// built from the recorded production, energy, maintenance, quality and alarm data, so the numbers match the other pages.
import { DEMO_ACCOUNTS, type DemoAccount } from "./demo-accounts";

type Json = Record<string, unknown> & { [k: string]: any }; // eslint-disable-line @typescript-eslint/no-explicit-any

const hoursAgo = (h: number) => new Date(Date.now() - h * 3_600_000).toISOString();

/** Route → permission, exactly as RequirePermission(...) in Divakar.FactoryOS.Api Endpoints.cs (most specific first).
 *  The demo returns the same 403 a real server would. */
const GUARDS: [RegExp, string][] = [
  [/^\/api\/v1\/roles\/permissions$/, "roles.manage"],
  [/^\/api\/v1\/roles$/, "users.read"],
  [/^\/api\/v1\/users/, "users.read"],
  [/^\/api\/v1\/audit/, "audit.read"],
  [/^\/api\/v1\/reports\/schedules/, "settings.manage"],
  [/^\/api\/v1\/reports/, "reports.read"],
];

export function missingPermission(route: string, account: DemoAccount): string | null {
  const g = GUARDS.find(([re]) => re.test(route));
  return g && !account.permissions.includes(g[1]) ? g[1] : null;
}

export function users() {
  return DEMO_ACCOUNTS.map((a, i) => ({
    userId: a.userId, email: a.email, displayName: a.displayName, isActive: true,
    lastLoginAt: hoursAgo(1 + i * 5), createdAt: "2026-10-16T08:00:00Z", roles: [a.role],
  }));
}

export function roles() {
  return DEMO_ACCOUNTS.map((a, i) => ({
    roleId: `e0000000-0000-4000-8000-00000000000${i + 1}`, code: a.role, name: a.roleName, isSystem: true,
    permissions: a.permissions, users: 1,
  }));
}

export const allPermissions = () => DEMO_ACCOUNTS[0].permissions;

const AUDIT_ACTIONS: [number, string, string, string, string][] = [
  // [account index, action, entity, entity id, detail]
  [0, "user.login", "user", "", "Signed in"],
  [4, "maintenance.workorder.start", "maintenance_work_order", "cm-261016-085126-524", "Packaging 2: started corrective work order"],
  [2, "alarm.acknowledge", "alarm", "797", "Machine fault on Injection moulder 180 t acknowledged: tool change"],
  [5, "quality.inspection.record", "inspection", "INSP-2611-014", "Bracket hole diameter: 5 samples, all in tolerance"],
  [3, "production.downtime.reason", "downtime", "DT-2611-208", "Turning centre 3: reason set to Material shortage"],
  [6, "energy.loadshed.rule.update", "load_shed_rule", "LS-01", "Peak limit raised from 850 kW to 880 kW"],
  [0, "role.update", "role", "operator", "Added alarms.read to Machine operator"],
  [1, "report.schedule.create", "report_schedule", "production", "Daily Production & OEE report to plant-leads at 07:00"],
  [0, "settings.update", "settings", "", "Shift times changed: A 06:00, B 14:00, C 22:00"],
  [0, "user.create", "user", "technician@divakar-demo.local", "Created Demo Maintenance Technician"],
  [7, "user.login", "user", "", "Signed in"],
  [4, "maintenance.workorder.complete", "maintenance_work_order", "pm-2611-033", "Air compressor 1: PM checklist done (8/8)"],
  [2, "production.workorder.release", "production_work_order", "WO-26-1107", "Released 1,200 x Bracket A to CNC Cell Line"],
  [0, "connectivity.connector.update", "connector", "opcua-hall-1", "OPC UA endpoint certificate renewed"],
];

export function audit(url: URL) {
  const all = AUDIT_ACTIONS.map(([who, action, entity, entityId, detail], i) => ({
    auditId: 5000 - i, tenantId: "4acf7dd5-a3f0-4958-afaa-c19dd909074c", userId: DEMO_ACCOUNTS[who].userId,
    userEmail: DEMO_ACCOUNTS[who].email, action, entity, entityId: entityId || null, detail,
    ipAddress: `10.20.1.${11 + who}`, at: hoursAgo(0.4 + i * 2.7),
  }));
  const before = Number(url.searchParams.get("before") ?? "0");
  const take = Number(url.searchParams.get("take") ?? "100");
  return all.filter((e) => !before || e.auditId < before).slice(0, take);
}

export function schedules() {
  return [
    { scheduleId: "f0000000-0000-4000-8000-000000000001", reportType: "production", recipients: "plant-leads@divakar-demo.local", localHour: 7, lastSentOn: hoursAgo(20).slice(0, 10), isActive: true },
    { scheduleId: "f0000000-0000-4000-8000-000000000002", reportType: "energy", recipients: "energy@divakar-demo.local", localHour: 8, lastSentOn: hoursAgo(19).slice(0, 10), isActive: true },
  ];
}

const n = (v: number | null | undefined, d = 1) => (v == null ? "–" : Number(v).toLocaleString("en-US", { maximumFractionDigits: d }));

/** One report, built from recorded data (get = a recorded GET body by path). */
export function report(type: string, url: URL, get: (path: string) => Json | Json[] | null) {
  const from = url.searchParams.get("from") ?? hoursAgo(24);
  const to = url.searchParams.get("to") ?? new Date().toISOString();
  const base = { type, from, to };
  switch (type) {
    case "production": {
      const o = get("GET /api/v1/production/oee") as Json | null;
      if (!o) break;
      return { ...base, title: "Production & OEE",
        figures: [
          { label: "Plant OEE", value: `${n(o.plant.value)}%` }, { label: "Availability", value: `${n(o.plant.availability)}%` },
          { label: "Performance", value: `${n(o.plant.performance)}%` }, { label: "Quality", value: `${n(o.plant.quality)}%` },
          { label: "Good parts", value: n(o.good, 0) }, { label: "Scrap", value: n(o.scrap, 0) },
        ],
        columns: ["Machine", "Line", "Status", "Good", "Scrap", "OEE %", "Availability %", "Parts/h"],
        rows: (o.machines as Json[]).map((m) => [m.name, m.lineName ?? "", m.status, n(m.good, 0), n(m.scrap, 0), n(m.oee?.value), n(m.oee?.availability), n(m.partsPerHour)]),
        notes: ["OEE = availability x performance x quality, per machine and for the plant (planned time only)."] };
    }
    case "energy": {
      const e = get("GET /api/v1/energy/summary") as Json | null;
      if (!e) break;
      return { ...base, title: "Energy",
        figures: [
          { label: "Consumption", value: `${n(e.consumptionKwh, 0)} kWh` }, { label: "From grid", value: `${n(e.gridKwh, 0)} kWh` },
          { label: "Solar", value: `${n(e.solarKwh, 0)} kWh` }, { label: "Cost", value: `${e.currency} ${n(e.cost, 0)}` },
          { label: "Peak demand", value: `${n(e.peakKw)} kW (${n(e.contractUsePct, 0)}% of contract)` }, { label: "CO2e", value: `${n(e.co2Kg)} kg` },
        ],
        columns: ["Meter", "Category", "kWh", "Cost", "kWh per part"],
        rows: (e.meters as Json[]).map((m) => [m.name, m.category ?? "", n(m.kwh), n(m.cost, 0), m.kwhPerPart == null ? "–" : n(m.kwhPerPart, 3)]),
        notes: [`Idle energy: ${n(e.idleKwh)} kWh (${e.currency} ${n(e.idleCost, 0)}).`, "Main meters: " + (e.mainMeters as string[]).join(", ")] };
    }
    case "maintenance": {
      const k = get("GET /api/v1/maintenance/kpis") as Json | null;
      if (!k) break;
      return { ...base, title: "Maintenance",
        figures: [
          { label: "Open work orders", value: n(k.open, 0) }, { label: "Overdue", value: n(k.overdue, 0) },
          { label: "Done (30 days)", value: n(k.doneLast30, 0) }, { label: "MTTR", value: `${n(k.mttrMinutes)} min` },
          { label: "MTBF", value: `${n(k.mtbfHours)} h` },
        ],
        columns: ["Asset", "Failures", "Downtime (min)", "MTTR (min)", "MTBF (h)", "Open orders"],
        rows: (k.assets as Json[]).map((a) => [a.assetName, n(a.failures, 0), n(a.downtimeMinutes), n(a.mttrMinutes), n(a.mtbfHours), n(a.openOrders, 0)]),
        notes: ["MTTR = downtime / failures; MTBF = run hours / failures."] };
    }
    case "quality": {
      const q = get("GET /api/v1/quality/summary") as Json | null;
      if (!q) break;
      return { ...base, title: "Quality",
        figures: [
          { label: "Characteristics", value: n(q.characteristics, 0) }, { label: "Out of control", value: n(q.outOfControl, 0) },
          { label: "Not capable", value: n(q.notCapable, 0) }, { label: "Open NCRs", value: n(q.openNcrs, 0) },
        ],
        columns: ["Characteristic", "Machine", "Samples", "Cpk", "Ppk", "Rule violations", "Verdict"],
        rows: (q.rows as Json[]).map((r) => [r.name, r.assetName, n(r.samples, 0), n(r.cpk, 2), n(r.ppk, 2), n(r.violations, 0), r.verdict]),
        notes: ["Cpk >= 1.33 is capable. Western Electric run rules mark out-of-control points."] };
    }
    case "alarms": {
      const s = get("GET /api/v1/alarms/summary") as Json | null;
      const list = (get("GET /api/v1/alarms") as Json[] | null) ?? [];
      if (!s) break;
      return { ...base, title: "Alarms",
        figures: [
          { label: "Active", value: n(s.active, 0) }, { label: "Critical", value: n(s.critical, 0) },
          { label: "Warning", value: n(s.warning, 0) }, { label: "Unacknowledged", value: n(s.unacknowledged, 0) },
        ],
        columns: ["#", "Raised", "Machine", "Severity", "Message", "State"],
        rows: list.slice(0, 40).map((a) => [String(a.alarmId), String(a.raisedAt).replace("T", " ").slice(0, 16), a.assetName ?? "", a.severity, a.message, a.state]),
        notes: ["Latest 40 alarms in the period."] };
    }
  }
  return null;
}

export function toCsv(r: { columns: string[]; rows: string[][] }) {
  const q = (s: string) => (/[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s);
  return [r.columns, ...r.rows].map((row) => row.map((c) => q(String(c))).join(",")).join("\n") + "\n";
}
