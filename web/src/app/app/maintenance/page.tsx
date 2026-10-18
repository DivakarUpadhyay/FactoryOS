"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { CalendarClock, Check, ClipboardList, Play, Plus, Timer, Wrench, X } from "lucide-react";
import { api } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { FixLibrary, ResolutionPanel } from "@/components/fix-knowledge";
import { Badge, Button, Card, ErrorNote, Field, Input, Modal, PageHeader, Select, Stat, Table, cx, errorText } from "@/components/ui";

type Item = { text: string; done: boolean };
type Wo = {
  maintWoId: string; code: string; assetId: string; assetName: string; planId: string | null; alarmId: number | null; type: "Preventive" | "Corrective" | "Predictive";
  priority: number; title: string; description: string | null; status: "Open" | "InProgress" | "Done" | "Cancelled"; assignedTo: string | null; assignedName: string | null;
  checklist: Item[]; dueAt: string | null; startedAt: string | null; completedAt: string | null; notes: string | null; createdAt: string; overdue: boolean;
};
type Plan = { planId: string; assetId: string; assetName: string; name: string; triggerType: "Calendar" | "RunHours" | "Cycles"; interval: number; checklist: string[]; estimatedMinutes: number | null; lastDoneAt: string; used: number; usedPct: number; hasOpenOrder: boolean; isActive: boolean };
type Reliability = { assetId: string; assetName: string; failures: number; downtimeMinutes: number; mttrMinutes: number | null; mtbfHours: number | null; runHours: number; openOrders: number };
type Kpis = { open: number; inProgress: number; overdue: number; doneLast30: number; pmCompliancePct: number | null; mttrMinutes: number | null; mtbfHours: number | null; assets: Reliability[] };
type User = { userId: string; displayName: string; isActive: boolean };
type Asset = { assetId: string; name: string };

const typeTone = { Preventive: "info", Corrective: "bad", Predictive: "warn" } as const;
const statusTone = { Open: "info", InProgress: "warn", Done: "ok", Cancelled: "muted" } as const;
const unit = { Calendar: "days", RunHours: "run hours", Cycles: "cycles" };
const when = (iso: string | null) => (iso ? new Date(iso.endsWith("Z") ? iso : iso + "Z").toLocaleString([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" }) : "—");

export default function MaintenancePage() {
  const { can } = useAuth();
  const execute = can("maintenance.execute");
  const manage = can("maintenance.manage");
  const [tab, setTab] = useState<"orders" | "plans" | "reliability" | "fixes">("orders");
  const [kpis, setKpis] = useState<Kpis | null>(null);
  const [orders, setOrders] = useState<Wo[]>([]);
  const [plans, setPlans] = useState<Plan[]>([]);
  const [users, setUsers] = useState<User[]>([]);
  const [assets, setAssets] = useState<Asset[]>([]);
  const [showDone, setShowDone] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [open, setOpen] = useState<Wo | null>(null);
  const [newWo, setNewWo] = useState(false);
  const [newPlan, setNewPlan] = useState(false);
  const [wf, setWf] = useState({ assetId: "", type: "Corrective", priority: "2", title: "", description: "", checklist: "" });
  const [pf, setPf] = useState({ assetId: "", name: "", triggerType: "Calendar", interval: "30", checklist: "", estimatedMinutes: "" });

  const load = useCallback(() => {
    Promise.all([api<Kpis>("/api/v1/maintenance/kpis"), api<Wo[]>(`/api/v1/maintenance/work-orders${showDone ? "" : "?status=open"}`), api<Plan[]>("/api/v1/maintenance/plans")])
      .then(([k, o, p]) => { setKpis(k); setOrders(o); setPlans(p); setError(null); }).catch((e) => setError(errorText(e)));
  }, [showDone]);
  useEffect(() => {
    load();
    api<User[]>("/api/v1/users").then((u) => setUsers(u.filter((x) => x.isActive))).catch(() => undefined);
    api<Asset[]>("/api/v1/plant/floor").then(setAssets).catch(() => undefined);
    const t = setInterval(load, 20_000);
    return () => clearInterval(t);
  }, [load]);

  async function act(id: string, action: string) {
    try {
      await api(`/api/v1/maintenance/work-orders/${id}/${action}`, { method: "POST" });
      setOpen(null);
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }
  async function saveChecklist(wo: Wo, items: Item[], notes: string | null) {
    try {
      await api(`/api/v1/maintenance/work-orders/${wo.maintWoId}/checklist`, { method: "PUT", json: { items, notes } });
      setOpen({ ...wo, checklist: items, notes });
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }
  async function assign(wo: Wo, userId: string) {
    try {
      await api(`/api/v1/maintenance/work-orders/${wo.maintWoId}/assign`, { method: "PUT", json: { userId: userId || null } });
      setOpen({ ...wo, assignedTo: userId || null, assignedName: users.find((u) => u.userId === userId)?.displayName ?? null });
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }
  async function createWo(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api("/api/v1/maintenance/work-orders", { method: "POST", json: { ...wf, priority: Number(wf.priority), checklist: wf.checklist.split("\n").filter((x) => x.trim()) } });
      setNewWo(false);
      setWf({ assetId: "", type: "Corrective", priority: "2", title: "", description: "", checklist: "" });
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }
  async function createPlan(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api("/api/v1/maintenance/plans", { method: "POST", json: { ...pf, interval: Number(pf.interval), estimatedMinutes: pf.estimatedMinutes ? Number(pf.estimatedMinutes) : null, checklist: pf.checklist.split("\n").filter((x) => x.trim()) } });
      setNewPlan(false);
      setPf({ assetId: "", name: "", triggerType: "Calendar", interval: "30", checklist: "", estimatedMinutes: "" });
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }

  return (
    <>
      <PageHeader title="Maintenance" sub="Preventive plans by calendar, running hours or cycles; work orders from alarms and anomalies; reliability."
        actions={execute && <Button onClick={() => setNewWo(true)}><Plus size={14} /> Work order</Button>} />
      <ErrorNote error={error} />
      {kpis && (
        <div className="mb-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          <Stat label="Open work orders" value={kpis.open + kpis.inProgress} sub={`${kpis.inProgress} in progress · ${kpis.overdue} overdue`} icon={<ClipboardList size={16} />} />
          <Stat label="PM compliance" value={kpis.pmCompliancePct !== null ? `${kpis.pmCompliancePct}%` : "—"} sub={`${kpis.doneLast30} done in 30 days`} icon={<CalendarClock size={16} />} />
          <Stat label="MTTR" value={kpis.mttrMinutes !== null ? `${kpis.mttrMinutes} min` : "—"} sub="Mean time to repair, 30 days" icon={<Timer size={16} />} />
          <Stat label="MTBF" value={kpis.mtbfHours !== null ? `${kpis.mtbfHours} h` : "—"} sub="Mean running time between failures" icon={<Wrench size={16} />} />
        </div>
      )}
      <div className="mb-4 flex gap-1 border-b border-line">
        {([["orders", "Work orders"], ["plans", "Preventive plans"], ["reliability", "Reliability"], ["fixes", "Fix library"]] as const).map(([t, label]) => (
          <button key={t} onClick={() => setTab(t)} className={cx("-mb-px border-b-2 px-4 py-2 text-sm", tab === t ? "border-brand font-medium text-brand" : "border-transparent text-muted hover:text-ink")}>{label}</button>
        ))}
      </div>

      {tab === "orders" && (
        <Card title={showDone ? "All work orders (90 days)" : "Open work orders"}
          actions={<label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={showDone} onChange={(e) => setShowDone(e.target.checked)} /> Show finished</label>}>
          <Table head={["Order", "Machine", "Type", "Priority", "Due", "Checklist", "Assigned", "Status"]} empty={orders.length === 0}>
            {orders.map((o) => (
              <tr key={o.maintWoId} className="cursor-pointer hover:bg-panel-2" onClick={() => setOpen(o)}>
                <td className="px-3 py-2"><div className="font-medium">{o.title}</div><div className="font-mono text-[11px] text-muted">{o.code}</div></td>
                <td className="px-3 py-2">{o.assetName}</td>
                <td className="px-3 py-2"><Badge tone={typeTone[o.type]}>{o.type}</Badge></td>
                <td className="px-3 py-2">{["", "Urgent", "Normal", "Low"][o.priority]}</td>
                <td className={cx("whitespace-nowrap px-3 py-2 text-xs", o.overdue && "font-medium text-bad")}>{when(o.dueAt)}{o.overdue && " · overdue"}</td>
                <td className="px-3 py-2 text-xs tabular-nums">{o.checklist.length ? `${o.checklist.filter((c) => c.done).length}/${o.checklist.length}` : "—"}</td>
                <td className="px-3 py-2 text-sm">{o.assignedName ?? <span className="text-muted">—</span>}</td>
                <td className="px-3 py-2"><Badge tone={statusTone[o.status]}>{o.status === "InProgress" ? "In progress" : o.status}</Badge></td>
              </tr>
            ))}
          </Table>
        </Card>
      )}

      {tab === "plans" && (
        <Card title={`${plans.length} preventive plans`} actions={manage && <Button className="py-1" onClick={() => setNewPlan(true)}><Plus size={14} /> Plan</Button>}>
          <Table head={["Plan", "Machine", "Every", "Used since last service", "Last done", "Order"]} empty={plans.length === 0}>
            {plans.map((p) => (
              <tr key={p.planId} className={p.isActive ? "" : "opacity-50"}>
                <td className="px-3 py-2 font-medium">{p.name}<div className="text-[11px] text-muted">{p.checklist.length} checklist items{p.estimatedMinutes ? ` · ${p.estimatedMinutes} min` : ""}</div></td>
                <td className="px-3 py-2"><Link href={`/app/plant/assets/${p.assetId}`} className="hover:text-brand">{p.assetName}</Link></td>
                <td className="px-3 py-2 text-sm">{p.interval.toLocaleString()} {unit[p.triggerType]}</td>
                <td className="min-w-48 px-3 py-2">
                  <div className="flex justify-between text-xs"><span>{p.used.toLocaleString()} {unit[p.triggerType]}</span><span>{Math.min(999, Math.round(p.usedPct))}%</span></div>
                  <div className="mt-1 h-2 rounded-full bg-panel-2"><div className="h-2 rounded-full" style={{ width: `${Math.min(100, p.usedPct)}%`, background: p.usedPct >= 100 ? "var(--bad)" : p.usedPct >= 80 ? "var(--warn)" : "var(--ok)" }} /></div>
                </td>
                <td className="whitespace-nowrap px-3 py-2 text-xs">{when(p.lastDoneAt)}</td>
                <td className="px-3 py-2">{p.hasOpenOrder ? <Badge tone="warn">open</Badge> : <span className="text-xs text-muted">—</span>}</td>
              </tr>
            ))}
          </Table>
        </Card>
      )}

      {tab === "reliability" && kpis && (
        <Card title="Reliability by machine — last 30 days (failures = time in Fault)">
          <Table head={["Machine", "Failures", "Downtime", "MTTR", "MTBF", "Running", "Open orders"]} empty={kpis.assets.length === 0}>
            {kpis.assets.map((a) => (
              <tr key={a.assetId}>
                <td className="px-3 py-2"><Link href={`/app/plant/assets/${a.assetId}`} className="hover:text-brand">{a.assetName}</Link></td>
                <td className="px-3 py-2 tabular-nums">{a.failures}</td>
                <td className="px-3 py-2 tabular-nums">{a.downtimeMinutes} min</td>
                <td className="px-3 py-2 tabular-nums">{a.mttrMinutes !== null ? `${a.mttrMinutes} min` : "—"}</td>
                <td className="px-3 py-2 tabular-nums">{a.mtbfHours !== null ? `${a.mtbfHours} h` : "—"}</td>
                <td className="px-3 py-2 tabular-nums">{a.runHours} h</td>
                <td className="px-3 py-2 tabular-nums">{a.openOrders}</td>
              </tr>
            ))}
          </Table>
        </Card>
      )}

      {tab === "fixes" && <FixLibrary />}

      <Modal open={!!open} wide title={open?.title ?? ""} onClose={() => setOpen(null)}>
        {open && <WoDetail wo={open} users={users} execute={execute} manage={manage} onAct={act} onChecklist={saveChecklist} onAssign={assign} />}
      </Modal>
      <Modal open={newWo} title="New work order" onClose={() => setNewWo(false)}>
        <form onSubmit={createWo} className="grid gap-4 sm:grid-cols-2">
          <Field label="Machine"><Select required value={wf.assetId} onChange={(e) => setWf({ ...wf, assetId: e.target.value })}><option value="">Choose…</option>{assets.map((a) => <option key={a.assetId} value={a.assetId}>{a.name}</option>)}</Select></Field>
          <Field label="Type"><Select value={wf.type} onChange={(e) => setWf({ ...wf, type: e.target.value })}><option>Corrective</option><option>Preventive</option><option>Predictive</option></Select></Field>
          <div className="sm:col-span-2"><Field label="Title"><Input required value={wf.title} onChange={(e) => setWf({ ...wf, title: e.target.value })} placeholder="Replace worn conveyor belt" /></Field></div>
          <Field label="Priority"><Select value={wf.priority} onChange={(e) => setWf({ ...wf, priority: e.target.value })}><option value="1">Urgent</option><option value="2">Normal</option><option value="3">Low</option></Select></Field>
          <div className="sm:col-span-2"><Field label="Description"><Input value={wf.description} onChange={(e) => setWf({ ...wf, description: e.target.value })} /></Field></div>
          <div className="sm:col-span-2"><Field label="Checklist" hint="One step per line"><textarea className="w-full rounded-lg border border-line bg-panel px-3 py-2 text-sm" rows={4} value={wf.checklist} onChange={(e) => setWf({ ...wf, checklist: e.target.value })} /></Field></div>
          <div className="flex justify-end gap-2 sm:col-span-2"><Button type="button" variant="ghost" onClick={() => setNewWo(false)}>Cancel</Button><Button type="submit">Create</Button></div>
        </form>
      </Modal>
      <Modal open={newPlan} title="New preventive plan" onClose={() => setNewPlan(false)}>
        <form onSubmit={createPlan} className="grid gap-4 sm:grid-cols-2">
          <Field label="Machine"><Select required value={pf.assetId} onChange={(e) => setPf({ ...pf, assetId: e.target.value })}><option value="">Choose…</option>{assets.map((a) => <option key={a.assetId} value={a.assetId}>{a.name}</option>)}</Select></Field>
          <Field label="Name"><Input required value={pf.name} onChange={(e) => setPf({ ...pf, name: e.target.value })} placeholder="Quarterly gearbox oil change" /></Field>
          <Field label="Every"><Select value={pf.triggerType} onChange={(e) => setPf({ ...pf, triggerType: e.target.value })}><option value="Calendar">N days</option><option value="RunHours">N running hours</option><option value="Cycles">N cycles / parts</option></Select></Field>
          <Field label="N"><Input required type="number" min="1" value={pf.interval} onChange={(e) => setPf({ ...pf, interval: e.target.value })} /></Field>
          <Field label="Estimated minutes"><Input type="number" min="1" value={pf.estimatedMinutes} onChange={(e) => setPf({ ...pf, estimatedMinutes: e.target.value })} /></Field>
          <div className="sm:col-span-2"><Field label="Checklist" hint="One step per line"><textarea className="w-full rounded-lg border border-line bg-panel px-3 py-2 text-sm" rows={4} value={pf.checklist} onChange={(e) => setPf({ ...pf, checklist: e.target.value })} /></Field></div>
          <div className="flex justify-end gap-2 sm:col-span-2"><Button type="button" variant="ghost" onClick={() => setNewPlan(false)}>Cancel</Button><Button type="submit">Create</Button></div>
        </form>
      </Modal>
    </>
  );
}

function WoDetail({ wo, users, execute, manage, onAct, onChecklist, onAssign }: {
  wo: Wo; users: User[]; execute: boolean; manage: boolean; onAct: (id: string, action: string) => void;
  onChecklist: (wo: Wo, items: Item[], notes: string | null) => void; onAssign: (wo: Wo, userId: string) => void;
}) {
  const [notes, setNotes] = useState(wo.notes ?? "");
  const live = wo.status === "Open" || wo.status === "InProgress";
  const needsFix = wo.type !== "Preventive";
  const [recorded, setRecorded] = useState(false);
  const onResolved = useCallback((r: boolean) => setRecorded(r), []);
  return (
    <div className="space-y-4 text-sm">
      <div className="flex flex-wrap gap-2"><Badge tone={typeTone[wo.type]}>{wo.type}</Badge><Badge tone={statusTone[wo.status]}>{wo.status}</Badge>{wo.overdue && <Badge tone="bad">overdue</Badge>}
        <span className="text-muted">{wo.assetName} · {wo.code}</span></div>
      {wo.description && <p>{wo.description}</p>}
      {manage && live && (
        <Field label="Assigned to"><Select value={wo.assignedTo ?? ""} onChange={(e) => onAssign(wo, e.target.value)}>
          <option value="">Nobody</option>{users.map((u) => <option key={u.userId} value={u.userId}>{u.displayName}</option>)}</Select></Field>
      )}
      {wo.checklist.length > 0 && (
        <div>
          <div className="mb-2 font-medium">Checklist</div>
          <ul className="space-y-1.5">
            {wo.checklist.map((c, i) => (
              <li key={i}><label className="flex items-center gap-2">
                <input type="checkbox" checked={c.done} disabled={!execute || !live}
                  onChange={(e) => onChecklist(wo, wo.checklist.map((x, j) => (j === i ? { ...x, done: e.target.checked } : x)), notes || null)} />
                <span className={c.done ? "text-muted line-through" : ""}>{c.text}</span></label></li>
            ))}
          </ul>
        </div>
      )}
      {(needsFix || wo.status === "Done") && <ResolutionPanel woId={wo.maintWoId} editable={execute && wo.status !== "Cancelled"} required={needsFix && live} onChange={onResolved} />}
      <Field label="Notes"><Input value={notes} disabled={!execute || !live} onChange={(e) => setNotes(e.target.value)} onBlur={() => notes !== (wo.notes ?? "") && onChecklist(wo, wo.checklist, notes || null)} /></Field>
      <p className="text-xs text-muted">Created {when(wo.createdAt)} · due {when(wo.dueAt)}{wo.startedAt && ` · started ${when(wo.startedAt)}`}{wo.completedAt && ` · finished ${when(wo.completedAt)}`}</p>
      {execute && live && (
        <div className="flex justify-end gap-2">
          <Button variant="ghost" className="text-bad" onClick={() => onAct(wo.maintWoId, "cancel")}><X size={14} /> Cancel order</Button>
          {wo.status === "Open" && <Button variant="outline" onClick={() => onAct(wo.maintWoId, "start")}><Play size={14} /> Start</Button>}
          <Button onClick={() => onAct(wo.maintWoId, "complete")} disabled={wo.checklist.some((c) => !c.done) || (needsFix && !recorded)}
            title={needsFix && !recorded ? "Record the cause and the fix first" : undefined}><Check size={14} /> Complete</Button>
        </div>
      )}
    </div>
  );
}
