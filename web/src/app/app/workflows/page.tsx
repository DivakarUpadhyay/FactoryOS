"use client";

import { useMemo, useState } from "react";
import Link from "next/link";
import {
  AlertTriangle, ArrowRight, BadgeCheck, Bot, Boxes, BrainCircuit, CalendarClock, ClipboardCheck, Factory, FileBarChart, Gauge, Leaf, Router, Search, Users, Zap,
} from "lucide-react";
import { useAuth } from "@/lib/auth";
import { Badge, Card, Input, PageHeader, cx } from "@/components/ui";

type Step = {
  title: string;
  /** Who does it: a built-in role, or "System" for what FactoryOS does by itself. */
  who: string;
  href?: string;
  /** Where on that page: tab › button. */
  where?: string;
  detail: string;
  permission?: string;
};
type Flow = { id: string; title: string; icon: React.ElementType; module?: string; permission?: string; summary: string; outcome: string; steps: Step[] };

/** The end-to-end business flows of a factory on FactoryOS, in the order they happen, each step pointing at the screen that does it. */
const FLOWS: Flow[] = [
  {
    id: "setup", title: "Set up the factory", icon: Factory, module: "plant", permission: "plant.read",
    summary: "From an empty company to machines sending data.", outcome: "Every machine is in the plant tree, connected and on the floor plan.",
    steps: [
      { title: "Company settings", who: "Factory admin", href: "/app/settings", where: "Settings", permission: "settings.manage",
        detail: "Time zone, currency, time-of-use tariff and contract capacity (kW), main meter, emission factors, shifts and breaks, alarm e-mails and webhook." },
      { title: "Build the plant tree", who: "Factory admin", href: "/app/plant", where: "Hierarchy", permission: "plant.manage",
        detail: "Site → Plant → Area → Line → Cell." },
      { title: "Add the assets", who: "Factory admin", href: "/app/plant", where: "Assets › Add asset", permission: "plant.manage",
        detail: "Type, protocol, endpoint, rated kW and ideal cycle time (needed for OEE performance)." },
      { title: "Tags and connection", who: "Factory admin", href: "/app/plant", where: "Open the asset › Tags · Connection · Device keys", permission: "plant.manage",
        detail: "Apply the tag template or add tags; enter the protocol settings and press Test connection to see live values at once." },
      { title: "Check connectivity", who: "Factory admin", href: "/app/connectivity", where: "Connectivity",
        detail: "Every asset's state, last data and errors, live. Fix anything Offline or With errors." },
      { title: "Place machines on the floor plan", who: "Factory admin", href: "/app/floor", where: "Floor plan › Arrange",
        detail: "Drag machines to where they stand; the 3D digital twin uses the same positions." },
    ],
  },
  {
    id: "people", title: "Users and access", icon: Users, permission: "users.read",
    summary: "Give each person exactly the access their job needs.", outcome: "Everyone signs in with their own account; every change is audited.",
    steps: [
      { title: "Invite a user", who: "Factory admin", href: "/app/users", where: "Users › Add user", permission: "users.manage",
        detail: "Name, e-mail, a temporary password (12+ characters, letters and digits) and one or more roles." },
      { title: "Adjust roles", who: "Factory admin", href: "/app/roles", where: "Roles & permissions", permission: "roles.manage",
        detail: "Built-in roles: plant manager, supervisor, operator, technician, quality inspector, energy manager, viewer. Make custom roles for anything else." },
      { title: "First sign-in", who: "New user", href: "/app/security", where: "Sign-in & security",
        detail: "The user replaces the temporary password with their own and turns on two-step sign-in with an authenticator app." },
      { title: "Review the audit log", who: "Factory admin", href: "/app/audit", where: "Audit log", permission: "audit.read",
        detail: "Who changed what and when — users, roles, settings, rules, records." },
    ],
  },
  {
    id: "edge", title: "Connect machines through an edge agent", icon: Router, module: "plant", permission: "plant.read",
    summary: "A gateway on the plant network reads the machines and keeps working when the internet is down.", outcome: "Readings arrive without gaps, even after an outage.",
    steps: [
      { title: "Create the agent", who: "Factory admin", href: "/app/connectivity", where: "Edge agents › Agent", permission: "connectivity.manage",
        detail: "Name it; copy the key shown once (it can be rotated later)." },
      { title: "Install factoryos-edge on the gateway", who: "IT / integrator",
        detail: "Install it as a service with the cloud URL and the key (see docs/EDGE.md). It caches its configuration encrypted on disk." },
      { title: "Assign assets to the agent", who: "Factory admin", href: "/app/connectivity", where: "Edge agents › Assets", permission: "connectivity.manage",
        detail: "Those assets are read by the agent instead of the cloud; protocols include OPC UA, Modbus, S7, MQTT, Sparkplug B, ROS 2, UR RTDE and SECS/GEM." },
      { title: "Agent online", who: "System", href: "/app/connectivity", where: "Edge agents",
        detail: "Heartbeat, version, host and buffered readings are shown. Local detectors flag spikes, drift and frozen sensors within seconds." },
      { title: "Internet outage", who: "System",
        detail: "Readings are stored on the gateway's disk and sent in order when the link returns — nothing is lost or repeated." },
    ],
  },
  {
    id: "production", title: "Work order to finished stock", icon: Gauge, module: "production", permission: "production.read",
    summary: "Plan an order, run it on a machine, count good and scrap, book it into stock.", outcome: "Finished lots in stock, OEE and the shift report up to date.",
    steps: [
      { title: "Product and bill of materials", who: "Plant manager", href: "/app/production", where: "Products › New product", permission: "production.manage",
        detail: "Code, name, unit, ideal cycle time and the materials one unit uses." },
      { title: "Plan the work order", who: "Supervisor", href: "/app/production", where: "Work orders › New work order", permission: "production.manage",
        detail: "Product, machine, quantity, due date and lot number. Status: Planned." },
      { title: "Issue material", who: "Store keeper", href: "/app/inventory", where: "Stock › Issue material", permission: "production.operate",
        detail: "Issue raw lots to the work order so the finished lot can be traced back to them." },
      { title: "Start", who: "Supervisor / operator", href: "/app/production", where: "Work orders › Start", permission: "production.operate",
        detail: "Status: Running. The machine counter counts parts from now on." },
      { title: "Count and explain stops", who: "Operator", href: "/op", where: "Shop-floor app",
        detail: "Record good and scrap counts with a scrap reason; pick the reason for each stop FactoryOS detected." },
      { title: "Complete", who: "Supervisor", href: "/app/production", where: "Work orders › Complete", permission: "production.operate",
        detail: "Status: Completed. The order's energy and footprint are calculated from its run window." },
      { title: "Book into stock", who: "Store keeper", href: "/app/inventory", where: "Stock › Book production into stock", permission: "production.operate",
        detail: "Creates the finished lot with a QR label." },
      { title: "Shift report and OEE", who: "Plant manager", href: "/app/production", where: "OEE · Downtime · Shift report",
        detail: "Availability × performance × quality per machine and plant; downtime Pareto by reason." },
    ],
  },
  {
    id: "alarm", title: "Alarm to repair", icon: AlertTriangle, module: "alarms", permission: "alarms.read",
    summary: "A sensor crosses a limit; someone is told, acknowledges it and the machine is fixed.", outcome: "The cause and fix are recorded; MTTR and MTBF update.",
    steps: [
      { title: "Alarm rule", who: "Factory admin", href: "/app/alarms", where: "Rules › New rule", permission: "alarms.manage",
        detail: "Sensor, operator, threshold, for N seconds, on one asset / a type / all; severity; e-mail/webhook; optionally open a work order." },
      { title: "Alarm raised", who: "System", href: "/app/alarms", where: "Open alarms",
        detail: "Pop-up and bell for everyone signed in, e-mail and webhook sent; escalated after 15 minutes if nobody acknowledges." },
      { title: "Acknowledge", who: "Supervisor", href: "/app/alarms", where: "Open alarms › Acknowledge", permission: "alarms.acknowledge",
        detail: "With a comment saying what is being done." },
      { title: "Corrective work order", who: "System / supervisor", href: "/app/maintenance", where: "Work orders › New work order", permission: "maintenance.execute",
        detail: "Opened automatically by the rule, or by hand; assign a technician and priority." },
      { title: "Repair", who: "Technician", href: "/app/maintenance", where: "Work orders › Start", permission: "maintenance.execute",
        detail: "Tick the checklist and record the cause and the fix — the order cannot be completed without them." },
      { title: "Complete and learn", who: "Technician", href: "/app/maintenance", where: "Work orders › Complete · Fix library", permission: "maintenance.execute",
        detail: "The fix goes into the fix library for next time; reliability shows MTTR and MTBF per machine." },
    ],
  },
  {
    id: "pm", title: "Preventive maintenance", icon: CalendarClock, module: "maintenance", permission: "maintenance.read",
    summary: "Service machines by time, running hours or cycles before they fail.", outcome: "PM compliance tracked; fewer breakdowns.",
    steps: [
      { title: "Preventive plan", who: "Maintenance manager", href: "/app/maintenance", where: "Preventive plans › New preventive plan", permission: "maintenance.manage",
        detail: "Machine, every N days / running hours / cycles, checklist (one step per line), estimated minutes." },
      { title: "Order opens by itself", who: "System", href: "/app/maintenance", where: "Work orders",
        detail: "When the machine reaches the interval, measured from its own counters." },
      { title: "Do the service", who: "Technician", href: "/app/maintenance", where: "Work orders › Start · Complete", permission: "maintenance.execute",
        detail: "Every checklist step must be ticked before the order completes." },
      { title: "Check compliance", who: "Maintenance manager", href: "/app/maintenance", where: "Reliability",
        detail: "PM compliance, MTTR and MTBF; predictive orders come from anomalies too." },
    ],
  },
  {
    id: "quality", title: "Quality control and non-conformance", icon: ClipboardCheck, module: "quality", permission: "quality.read",
    summary: "Watch a characteristic with SPC; when it goes wrong, contain and correct it.", outcome: "The non-conformance is closed with disposition, root cause and corrective action.",
    steps: [
      { title: "Characteristic", who: "Quality engineer", href: "/app/quality", where: "SPC charts › New characteristic", permission: "quality.manage",
        detail: "Nominal and spec limits, subgroup size; feed it from a sensor or enter measurements by hand." },
      { title: "Inspect", who: "Quality inspector", href: "/app/quality", where: "Inspections", permission: "quality.execute",
        detail: "Enter subgroups; X̄-R or I-MR charts with Western Electric rules and Cp/Cpk/Pp/Ppk." },
      { title: "Out of control", who: "System", href: "/app/alarms", where: "Open alarms",
        detail: "A broken run rule raises a quality alarm." },
      { title: "Report a non-conformance", who: "Quality inspector", href: "/app/quality", where: "Non-conformances › Report a non-conformance", permission: "quality.execute",
        detail: "Product, lot, quantity affected, severity." },
      { title: "Close it", who: "Quality engineer", href: "/app/quality", where: "Non-conformances", permission: "quality.manage",
        detail: "Disposition (scrap, rework, return, use as is), root cause and corrective action are required to close." },
      { title: "Trace the lot", who: "Quality engineer", href: "/app/inventory", where: "Traceability",
        detail: "Which work order, machine, input lots, alarms and stops — and where the lot went." },
    ],
  },
  {
    id: "inventory", title: "Material from receipt to traceability", icon: Boxes, module: "inventory", permission: "production.read",
    summary: "Every lot is received, used and traced.", outcome: "Forward and backward traceability for any lot.",
    steps: [
      { title: "Item master", who: "Store keeper", href: "/app/inventory", where: "Stock › New item", permission: "production.manage",
        detail: "Raw, WIP, finished, spare or consumable; unit, minimum stock and reorder quantity." },
      { title: "Receive goods", who: "Store keeper", href: "/app/inventory", where: "Stock › Receive goods", permission: "production.operate",
        detail: "Lot, supplier and supplier lot, location, expiry; print the QR lot label." },
      { title: "Issue and consume", who: "Store keeper", href: "/app/inventory", where: "Stock › Issue material", permission: "production.operate",
        detail: "To a work order or a maintenance order; scan the box (barcode, item code or lot)." },
      { title: "Low stock", who: "System", href: "/app/inventory", where: "Stock",
        detail: "Items below minimum are flagged (also on dashboards)." },
      { title: "Trace", who: "Anyone with access", href: "/app/inventory", where: "Traceability",
        detail: "Enter or scan a lot to see how it was made and where it was used." },
    ],
  },
  {
    id: "energy", title: "Energy cost control", icon: Zap, module: "energy", permission: "energy.read",
    summary: "See where energy and money go and act on the biggest items.", outcome: "Lower bill, peaks kept under the contract capacity.",
    steps: [
      { title: "Tariff, contract and main meter", who: "Factory admin", href: "/app/settings", where: "Electricity tariff and contract · Main meter", permission: "settings.manage",
        detail: "Peak/off-peak rates, basic charge per contract kW, contract capacity." },
      { title: "Review consumption", who: "Energy manager", href: "/app/energy", where: "Overview",
        detail: "kWh, cost, peak demand vs contract, energy per part and 'What to act on'." },
      { title: "Cut idle waste", who: "Energy manager", href: "/app/energy", where: "Overview › Idle waste",
        detail: "Machines drawing power while idle, with hours and cost." },
      { title: "Plan for peaks", who: "Energy manager", href: "/app/energy", where: "Bill forecast · Load shedding · Cost per shift",
        detail: "Forecast the month's bill and which loads to shed first when demand nears the contract." },
      { title: "Daily e-mail", who: "Energy manager", href: "/app/reports", where: "Reports › Send to", permission: "reports.read",
        detail: "Key figures e-mailed every day at a chosen hour." },
    ],
  },
  {
    id: "iso50001", title: "ISO 50001 energy management cycle", icon: BadgeCheck, module: "energy", permission: "energy.read",
    summary: "Plan – do – check – act, with evidence for each clause.", outcome: "Audit-ready EnMS with verified savings.",
    steps: [
      { title: "Readiness", who: "Energy manager", href: "/app/enms", where: "Overview",
        detail: "Clause-by-clause status (4–10) with the evidence FactoryOS already holds." },
      { title: "Energy review and SEUs", who: "Energy manager", href: "/app/enms", where: "Energy review & SEUs", permission: "energy.manage",
        detail: "Significant energy uses, their drivers and findings (6.3)." },
      { title: "EnPIs and baselines", who: "Energy manager", href: "/app/enms", where: "EnPIs & baselines", permission: "energy.manage",
        detail: "ISO 50006 regression baselines and CUSUM (6.4, 6.5)." },
      { title: "Objectives and actions", who: "Energy manager", href: "/app/enms", where: "Objectives & actions", permission: "energy.manage",
        detail: "Targets, action plans, owners, expected kWh and investment (6.2)." },
      { title: "Meters and calibration", who: "Energy manager", href: "/app/enms", where: "Meters & calibration", permission: "energy.manage",
        detail: "Meter register, accuracy class, calibration certificates and due dates (9.1)." },
      { title: "Verify savings", who: "Energy manager", href: "/app/enms", where: "Savings M&V", permission: "energy.manage",
        detail: "IPMVP option A–D; B and C checked against ASHRAE Guideline 14 and written to the action as verified." },
      { title: "Audit and management review", who: "Plant manager", href: "/app/enms", where: "Audits & reviews · Documents", permission: "energy.manage",
        detail: "Internal audits with findings and corrective actions; management reviews (9.2, 9.3, 10)." },
    ],
  },
  {
    id: "carbon", title: "Carbon and product footprint reporting", icon: Leaf, module: "carbon", permission: "energy.read",
    summary: "Scope 1, 2 and 3 emissions and kg CO₂e per product from published factors.", outcome: "GHG inventory and product footprints ready to export.",
    steps: [
      { title: "Choose the grid region", who: "Energy manager", href: "/app/enms", where: "Emission factors › Use …", permission: "energy.manage",
        detail: "Taiwan, UK (DESNZ 2025), US (eGRID2023) or India (CEA); refrigerants with AR5 GWPs; Scope 3 factors." },
      { title: "Record activities", who: "Energy manager", href: "/app/enms", where: "GHG activities › Activity · Energy data", permission: "energy.manage",
        detail: "Fuels, refrigerant top-ups, process CO₂ and Scope 3 (freight, travel, waste…), each with a reference." },
      { title: "Material factors and product mass", who: "Energy manager", href: "/app/carbon", where: "Product carbon footprint › edit (pencil)", permission: "energy.manage",
        detail: "Cradle-to-gate factor and source for each material in the bill of materials, and the mass of one unit." },
      { title: "Review the inventory", who: "Energy manager", href: "/app/carbon", where: "Carbon / ESG",
        detail: "Scope 1, Scope 2 location- and market-based, Scope 3 by category, solar avoided, per area and per part." },
      { title: "Export", who: "Energy manager", href: "/app/carbon", where: "CSV",
        detail: "Daily detail as CSV; product footprints per unit and per tonne." },
    ],
  },
  {
    id: "ai", title: "OT AI insight loop", icon: BrainCircuit, module: "alarms", permission: "alarms.read",
    summary: "Detectors learn each signal's normal; operators confirm or reject what they find.", outcome: "Fewer false alarms over time; real problems become work orders.",
    steps: [
      { title: "Detectors run", who: "System", href: "/app/ai", where: "OT AI insights",
        detail: "Every 5 minutes in the cloud and within seconds on edge agents: energy anomalies, stopped meters, drift, frozen sensors, time to a limit, idle waste." },
      { title: "Review an insight", who: "Supervisor", href: "/app/ai", where: "OT AI insights",
        detail: "Each finding names the asset, signal, score and why." },
      { title: "Real or false", who: "Supervisor", href: "/app/ai", where: "Feedback › real / false", permission: "alarms.acknowledge",
        detail: "Each false alarm makes that detector less eager on that signal; each confirmation keeps it sharp." },
      { title: "Act", who: "Technician", href: "/app/maintenance", where: "Work orders › New work order", permission: "maintenance.execute",
        detail: "Turn a confirmed problem into a work order." },
    ],
  },
  {
    id: "agv", title: "AGV transport mission", icon: Bot, module: "robotics", permission: "robotics.read",
    summary: "Move material between stations with the AGV/AMR fleet.", outcome: "The mission is done by the nearest free vehicle with enough battery.",
    steps: [
      { title: "Stations", who: "Factory admin", href: "/app/robotics", where: "New station", permission: "plant.manage",
        detail: "Pick-up and drop-off points on the fleet map." },
      { title: "Request a transport", who: "Operator", href: "/app/robotics", where: "New transport mission", permission: "robotics.operate",
        detail: "Pick up at, drop off at, priority." },
      { title: "Dispatch", who: "System", href: "/app/robotics", where: "Fleet map",
        detail: "Sent to the nearest free vehicle with enough battery (as a VDA 5050 order to real vehicles): Queued → Assigned → PickedUp → Done." },
    ],
  },
  {
    id: "reports", title: "Reporting", icon: FileBarChart, permission: "reports.read",
    summary: "Share the figures with management and customers.", outcome: "Printed, exported or e-mailed on a schedule.",
    steps: [
      { title: "Run a report", who: "Plant manager", href: "/app/reports", where: "Reports",
        detail: "Production & OEE, energy, maintenance, quality, alarms for a period." },
      { title: "Print or export", who: "Plant manager", href: "/app/reports", where: "Reports › Print / PDF · CSV",
        detail: "PDF via print, or CSV that opens in Excel (Chinese text included)." },
      { title: "Schedule", who: "Plant manager", href: "/app/reports", where: "Reports › Send to",
        detail: "E-mail recipients and the hour of day." },
      { title: "Own dashboards", who: "Anyone", href: "/app/dashboards", where: "My dashboards",
        detail: "Build screens from widgets and share them with the company." },
    ],
  },
];

const whoTone = (who: string) => (who === "System" ? "brand" : who.includes("admin") ? "warn" : "muted") as "brand" | "warn" | "muted";

/** Every business flow in the plan, step by step, linked to the screen that does each step. */
export default function WorkflowsPage() {
  const { can, hasModule } = useAuth();
  const flows = useMemo(() => FLOWS.filter((f) => (!f.module || hasModule(f.module)) && (!f.permission || can(f.permission))), [hasModule, can]);
  const [q, setQ] = useState("");
  const [sel, setSel] = useState(flows[0]?.id ?? "");
  const term = q.trim().toLowerCase();
  const shown = flows.filter((f) => !term || [f.title, f.summary, ...f.steps.flatMap((s) => [s.title, s.who, s.detail])].join(" ").toLowerCase().includes(term));
  const flow = shown.find((f) => f.id === sel) ?? shown[0];

  /** On a phone the steps sit below the list, so bring them into view. */
  function pick(id: string) {
    setSel(id);
    if (window.innerWidth < 1024) requestAnimationFrame(() => document.getElementById("flow-steps")?.scrollIntoView({ behavior: "smooth", block: "start" }));
  }

  return (
    <>
      <PageHeader title="Workflows" sub="How work moves through FactoryOS: each business flow in order, who does each step and the screen where it is done." />
      <div className="grid gap-6 lg:grid-cols-[320px_1fr]">
        <div>
          <div className="relative mb-3">
            <Search size={14} className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-muted" />
            <Input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search flows, steps, roles" aria-label="Search workflows" className="pl-8" />
          </div>
          <nav aria-label="Workflows" className="space-y-1">
            {shown.map((f) => {
              const Icon = f.icon;
              return (
                <button key={f.id} onClick={() => pick(f.id)}
                  className={cx("flex w-full items-start gap-3 rounded-lg border px-3 py-2 text-left", flow?.id === f.id ? "border-brand bg-brand/5" : "border-line hover:bg-black/5")}>
                  <Icon size={16} className="mt-0.5 shrink-0 text-brand" />
                  <span className="min-w-0">
                    <span className="block text-sm font-medium">{f.title}</span>
                    <span className="block text-xs text-muted">{f.steps.length} steps · {f.summary}</span>
                  </span>
                </button>
              );
            })}
            {shown.length === 0 && <p className="px-1 text-sm text-muted">No workflow matches.</p>}
          </nav>
        </div>

        {flow && (
          <div id="flow-steps" className="scroll-mt-16">
          <Card title={flow.title}>
            <p className="text-sm text-muted">{flow.summary}</p>
            <div className="my-4 flex flex-wrap items-center gap-1.5" aria-label="Flow overview">
              {flow.steps.map((s, i) => (
                <span key={s.title} className="flex items-center gap-1.5">
                  <span className="rounded-full border border-line px-2.5 py-0.5 text-xs"><span className="mr-1 text-muted">{i + 1}</span>{s.title}</span>
                  {i < flow.steps.length - 1 && <ArrowRight size={12} className="text-muted" />}
                </span>
              ))}
            </div>
            <ol className="relative space-y-4 border-l border-line pl-6">
              {flow.steps.map((s, i) => {
                const allowed = !s.permission || can(s.permission);
                return (
                  <li key={s.title} className="relative">
                    <span className="absolute -left-[37px] flex h-6 w-6 items-center justify-center rounded-full bg-brand text-xs font-semibold text-white">{i + 1}</span>
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="font-medium">{s.title}</span>
                      <Badge tone={whoTone(s.who)}>{s.who}</Badge>
                      {!allowed && <Badge tone="muted">not in your role</Badge>}
                    </div>
                    <p className="mt-1 text-sm">{s.detail}</p>
                    {(s.href || s.where) && (
                      <div className="mt-1 text-xs text-muted">
                        {s.href ? <Link href={s.href} className="inline-flex items-center gap-1 text-brand hover:underline">{s.where ?? "Open"} <ArrowRight size={11} /></Link> : s.where}
                      </div>
                    )}
                  </li>
                );
              })}
            </ol>
            <div className="mt-5 rounded-lg bg-black/5 px-3 py-2 text-sm"><span className="font-medium">Result: </span>{flow.outcome}</div>
          </Card>
          </div>
        )}
      </div>
    </>
  );
}
