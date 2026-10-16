"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { Check, ListTree, Play, Plus, X } from "lucide-react";
import { BomRoutingEditor, WorkOrderDetail } from "@/components/planning";
import { api } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { statusTone } from "@/lib/types";
import { BarChart, HBars, PERIODS, fmt, periodQuery } from "@/components/charts";
import { Ring } from "@/components/charts";
import { Badge, Button, Card, ErrorNote, Field, Input, Modal, PageHeader, Select, Table, cx, errorText } from "@/components/ui";

type Oee = { availability: number | null; performance: number | null; quality: number | null; value: number | null };
type MachineOee = {
  assetId: string; code: string; name: string; areaName: string | null; lineName: string | null; status: keyof typeof statusTone; packMlState: string;
  runSec: number; downSec: number; plannedSec: number; good: number; scrap: number; idealCycleSec: number | null; partsPerHour: number; oee: Oee;
};
type Report = { from: string; to: string; plant: Oee; good: number; scrap: number; runSec: number; plannedSec: number; machines: MachineOee[]; hourlyOutput: { hour: string; parts: number }[] };
type Reason = { reasonCode: string; name: string; category: string };
type Downtime = { downtimeId: number; assetId: string; assetName: string; startTs: string; endTs: string | null; seconds: number; state: string; reasonCode: string | null; reasonName: string | null; reasonCategory: string | null; comment: string | null };
type Pareto = { reasonCode: string | null; reason: string; category: string; count: number; seconds: number };
type Product = { productId: string; code: string; name: string; idealCycleSec: number | null; unit: string; isActive: boolean };
type WorkOrder = { workOrderId: string; code: string; productId: string; productCode: string; productName: string; assetId: string; assetName: string; quantity: number; status: string; dueDate: string | null; lotNo: string | null; produced: number; scrap: number; actualStart: string | null; actualEnd: string | null };
type ShiftReport = { shift: string; from: string; to: string; oee: Oee; good: number; scrap: number; downtimeMinutes: number; topReasons: Pareto[] };

type Tab = "overview" | "downtime" | "orders" | "shifts";
const tone = (v: number | null) => (v === null ? "muted" : v >= 85 ? "ok" : v >= 60 ? "warn" : "bad") as "ok" | "warn" | "bad" | "muted";
const pct = (v: number | null) => (v === null ? "—" : `${v}%`);
const dur = (s: number) => (s >= 3600 ? `${(s / 3600).toFixed(1)} h` : `${Math.round(s / 60)} min`);
const local = (iso: string) => new Date(iso.endsWith("Z") ? iso : iso + "Z").toLocaleString([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });

export default function ProductionPage() {
  const [tab, setTab] = useState<Tab>("overview");
  return (
    <>
      <PageHeader title="Production & OEE" sub="Availability × performance × quality, downtime reasons, work orders and shift reports." />
      <div className="mb-4 flex gap-1 overflow-x-auto border-b border-line">
        {([["overview", "OEE"], ["downtime", "Downtime"], ["orders", "Work orders"], ["shifts", "Shift report"]] as [Tab, string][]).map(([t, label]) => (
          <button key={t} onClick={() => setTab(t)}
            className={cx("-mb-px whitespace-nowrap border-b-2 px-4 py-2 text-sm", tab === t ? "border-brand font-medium text-brand" : "border-transparent text-muted hover:text-ink")}>
            {label}
          </button>
        ))}
      </div>
      {tab === "overview" && <Overview />}
      {tab === "downtime" && <DowntimeTab />}
      {tab === "orders" && <OrdersTab />}
      {tab === "shifts" && <ShiftsTab />}
    </>
  );
}

function Overview() {
  const [period, setPeriod] = useState("24h");
  const [r, setR] = useState<Report | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(() => {
    api<Report>(`/api/v1/production/oee?${periodQuery(period)}`).then(setR).catch((e) => setError(errorText(e)));
  }, [period]);
  useEffect(() => {
    load();
    const t = setInterval(load, 30_000);
    return () => clearInterval(t);
  }, [load]);
  return (
    <>
      <ErrorNote error={error} />
      <div className="mb-4 flex justify-end">
        <Select value={period} onChange={(e) => setPeriod(e.target.value)} className="w-40 py-1" aria-label="Period">
          {PERIODS.map((p) => <option key={p.key} value={p.key}>{p.label}</option>)}
        </Select>
      </div>
      {r && (
        <>
          <div className="mb-6 grid gap-4 lg:grid-cols-3">
            <Card title="Plant OEE" className="lg:col-span-2">
              <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
                <Ring pct={r.plant.value} label="OEE" tone={tone(r.plant.value) === "muted" ? "muted" : tone(r.plant.value)} />
                <Ring pct={r.plant.availability} label="Availability" tone="info" />
                <Ring pct={r.plant.performance} label="Performance" tone="brand" />
                <Ring pct={r.plant.quality} label="Quality" tone="ok" />
              </div>
              <p className="mt-4 text-sm text-muted">
                {r.good.toLocaleString()} good · {r.scrap.toLocaleString()} scrap · running {dur(r.runSec)} of {dur(r.plannedSec)} planned (all machines).
                World-class OEE is about 85 %.
              </p>
            </Card>
            <Card title="Output per hour (parts)">
              <BarChart bars={r.hourlyOutput.map((h) => ({ label: h.hour.slice(11, 13), value: h.parts, title: `${h.hour.slice(5, 16)}: ${h.parts} parts` }))} height={170} />
            </Card>
          </div>
          <Card title="Machines (worst OEE first)">
            <Table head={["Machine", "State (PackML)", "OEE", "A", "P", "Q", "Good", "Scrap", "Parts/h", "Down"]} empty={r.machines.length === 0}>
              {r.machines.map((m) => (
                <tr key={m.assetId}>
                  <td className="px-3 py-2">
                    <Link href={`/app/plant/assets/${m.assetId}`} className="font-medium hover:text-brand">{m.name}</Link>
                    <div className="text-[11px] text-muted">{[m.areaName, m.lineName].filter(Boolean).join(" · ")}</div>
                  </td>
                  <td className="px-3 py-2"><Badge tone={statusTone[m.status] ?? "muted"}>{m.status}</Badge> <span className="text-xs text-muted">{m.packMlState}</span></td>
                  <td className="px-3 py-2"><Badge tone={tone(m.oee.value)}>{pct(m.oee.value)}</Badge></td>
                  <td className="px-3 py-2 tabular-nums">{pct(m.oee.availability)}</td>
                  <td className="px-3 py-2 tabular-nums">{pct(m.oee.performance)}</td>
                  <td className="px-3 py-2 tabular-nums">{pct(m.oee.quality)}</td>
                  <td className="px-3 py-2 tabular-nums">{m.good.toLocaleString()}</td>
                  <td className="px-3 py-2 tabular-nums">{m.scrap.toLocaleString()}</td>
                  <td className="px-3 py-2 tabular-nums">{fmt(m.partsPerHour)}</td>
                  <td className="px-3 py-2 tabular-nums">{dur(m.downSec)}</td>
                </tr>
              ))}
            </Table>
            {r.machines.some((m) => m.idealCycleSec === null) && (
              <p className="mt-3 text-xs text-muted">Machines without an ideal cycle time show no performance or OEE — set it on the asset.</p>
            )}
          </Card>
        </>
      )}
    </>
  );
}

function DowntimeTab() {
  const { can } = useAuth();
  const operate = can("production.operate");
  const [rows, setRows] = useState<Downtime[]>([]);
  const [reasons, setReasons] = useState<Reason[]>([]);
  const [pareto, setPareto] = useState<Pareto[]>([]);
  const [pending, setPending] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [edit, setEdit] = useState<Downtime | null>(null);
  const [form, setForm] = useState({ reasonCode: "", comment: "" });

  const load = useCallback(() => {
    const q = periodQuery("7d");
    Promise.all([api<Downtime[]>(`/api/v1/production/downtimes?${q}&pending=${pending}`), api<Pareto[]>(`/api/v1/production/pareto?${q}`)])
      .then(([d, p]) => { setRows(d); setPareto(p); }).catch((e) => setError(errorText(e)));
  }, [pending]);
  useEffect(() => {
    api<Reason[]>("/api/v1/production/reasons").then(setReasons).catch(() => undefined);
    load();
    const t = setInterval(load, 15_000);
    return () => clearInterval(t);
  }, [load]);

  async function save(e: React.FormEvent) {
    e.preventDefault();
    if (!edit) return;
    try {
      await api(`/api/v1/production/downtimes/${edit.downtimeId}/reason`, { method: "PUT", json: { reasonCode: form.reasonCode, comment: form.comment || null } });
      setEdit(null);
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }

  const byCategory = reasons.reduce<Record<string, Reason[]>>((m, r) => ({ ...m, [r.category]: [...(m[r.category] ?? []), r] }), {});
  return (
    <>
      <ErrorNote error={error} />
      <div className="mb-6 grid gap-4 lg:grid-cols-3">
        <Card title="Downtime Pareto — last 7 days" className="lg:col-span-1">
          <HBars rows={pareto.map((p) => ({ label: p.reason, value: Math.round(p.seconds / 60), sub: `${p.count}×`, tone: p.reasonCode ? "warn" as const : "muted" as const }))} unit=" min" />
        </Card>
        <Card title={pending ? "Open or unexplained stops" : "All stops — last 7 days"} className="lg:col-span-2"
          actions={<label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={pending} onChange={(e) => setPending(e.target.checked)} /> Needs a reason</label>}>
          <Table head={["Machine", "Started", "Duration", "State", "Reason", ""]} empty={rows.length === 0}>
            {rows.map((d) => (
              <tr key={d.downtimeId}>
                <td className="px-3 py-2 font-medium">{d.assetName}</td>
                <td className="whitespace-nowrap px-3 py-2 text-xs">{local(d.startTs)}</td>
                <td className="px-3 py-2 tabular-nums">{dur(d.seconds)} {!d.endTs && <Badge tone="bad">ongoing</Badge>}</td>
                <td className="px-3 py-2"><Badge tone={d.state === "Fault" ? "bad" : d.state === "Idle" ? "info" : "warn"}>{d.state}</Badge></td>
                <td className="px-3 py-2 text-sm">{d.reasonName ?? <span className="text-muted">—</span>}{d.comment && <div className="text-xs text-muted">{d.comment}</div>}</td>
                <td className="px-3 py-2 text-right">
                  {operate && <Button variant="outline" className="py-1 text-xs" onClick={() => { setEdit(d); setForm({ reasonCode: d.reasonCode ?? "", comment: d.comment ?? "" }); }}>
                    {d.reasonCode ? "Change" : "Add reason"}
                  </Button>}
                </td>
              </tr>
            ))}
          </Table>
        </Card>
      </div>
      <Modal open={!!edit} title={`Why did ${edit?.assetName} stop?`} onClose={() => setEdit(null)}>
        <form onSubmit={save} className="space-y-4">
          <Field label="Reason">
            <Select required value={form.reasonCode} onChange={(e) => setForm({ ...form, reasonCode: e.target.value })}>
              <option value="">Choose…</option>
              {Object.entries(byCategory).map(([cat, list]) => (
                <optgroup key={cat} label={cat}>{list.map((r) => <option key={r.reasonCode} value={r.reasonCode}>{r.name}</option>)}</optgroup>
              ))}
            </Select>
          </Field>
          <Field label="Comment" hint="Required for 'Other'"><Input value={form.comment} maxLength={300} onChange={(e) => setForm({ ...form, comment: e.target.value })} /></Field>
          <div className="flex justify-end gap-2"><Button type="button" variant="ghost" onClick={() => setEdit(null)}>Cancel</Button><Button type="submit">Save</Button></div>
        </form>
      </Modal>
    </>
  );
}

function OrdersTab() {
  const { can } = useAuth();
  const manage = can("production.manage");
  const operate = can("production.operate");
  const [orders, setOrders] = useState<WorkOrder[]>([]);
  const [products, setProducts] = useState<Product[]>([]);
  const [machines, setMachines] = useState<{ assetId: string; name: string; category: string; assetTypeCode: string }[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [newOrder, setNewOrder] = useState(false);
  const [newProduct, setNewProduct] = useState(false);
  const [wo, setWo] = useState({ code: "", productId: "", assetId: "", quantity: "100", dueDate: "", lotNo: "" });
  const [pr, setPr] = useState({ code: "", name: "", idealCycleSec: "", unit: "pcs" });
  const [bomFor, setBomFor] = useState<Product | null>(null);
  const [detail, setDetail] = useState<WorkOrder | null>(null);

  const load = useCallback(() => {
    Promise.all([api<WorkOrder[]>("/api/v1/production/work-orders"), api<Product[]>("/api/v1/production/products")])
      .then(([o, p]) => { setOrders(o); setProducts(p); }).catch((e) => setError(errorText(e)));
  }, []);
  useEffect(() => {
    load();
    api<{ assetId: string; name: string; category: string; assetTypeCode: string }[]>("/api/v1/plant/floor")
      .then((f) => setMachines(f.filter((m) => (m.category === "Production" || m.category === "Robotics") && !["agv", "amr"].includes(m.assetTypeCode))))
      .catch(() => undefined);
    const t = setInterval(load, 10_000);
    return () => clearInterval(t);
  }, [load]);

  async function act(id: string, action: string) {
    try {
      await api(`/api/v1/production/work-orders/${id}/${action}`, { method: "POST" });
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }
  async function createOrder(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api("/api/v1/production/work-orders", { method: "POST", json: { ...wo, quantity: Number(wo.quantity), dueDate: wo.dueDate || null, lotNo: wo.lotNo || null } });
      setNewOrder(false);
      setWo({ code: "", productId: "", assetId: "", quantity: "100", dueDate: "", lotNo: "" });
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }
  async function createProduct(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api("/api/v1/production/products", { method: "POST", json: { ...pr, idealCycleSec: pr.idealCycleSec ? Number(pr.idealCycleSec) : null } });
      setNewProduct(false);
      setPr({ code: "", name: "", idealCycleSec: "", unit: "pcs" });
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }

  return (
    <>
      <ErrorNote error={error} />
      <Card title="Work orders" className="mb-6" actions={manage && (
        <>
          <Button variant="outline" className="py-1" onClick={() => setNewProduct(true)}><Plus size={14} /> Product</Button>
          <Button className="py-1" onClick={() => setNewOrder(true)} disabled={products.length === 0}><Plus size={14} /> Work order</Button>
        </>
      )}>
        {products.length === 0 && <p className="mb-3 text-sm text-muted">Add a product first, then create work orders for it.</p>}
        <Table head={["Order", "Product", "Machine", "Progress", "Scrap", "Status", ""]} empty={orders.length === 0}>
          {orders.map((o) => {
            const p = Math.min(100, (o.produced / o.quantity) * 100);
            return (
              <tr key={o.workOrderId}>
                <td className="px-3 py-2 font-mono text-xs">{o.code}{o.lotNo && <div className="text-muted">lot {o.lotNo}</div>}</td>
                <td className="px-3 py-2">{o.productName}</td>
                <td className="px-3 py-2">{o.assetName}</td>
                <td className="min-w-40 px-3 py-2">
                  <div className="flex justify-between text-xs"><span>{o.produced.toLocaleString()} / {o.quantity.toLocaleString()}</span><span>{Math.round(p)}%</span></div>
                  <div className="mt-1 h-2 rounded-full bg-panel-2"><div className="h-2 rounded-full bg-ok" style={{ width: `${p}%` }} /></div>
                </td>
                <td className="px-3 py-2 tabular-nums">{o.scrap}</td>
                <td className="px-3 py-2"><Badge tone={o.status === "Running" ? "ok" : o.status === "Planned" ? "info" : o.status === "Completed" ? "muted" : "warn"}>{o.status}</Badge></td>
                <td className="whitespace-nowrap px-3 py-2 text-right">
                  {operate && o.status === "Planned" && <Button variant="outline" className="py-1 text-xs" onClick={() => act(o.workOrderId, "start")}><Play size={12} /> Start</Button>}
                  {operate && o.status === "Running" && <Button variant="outline" className="py-1 text-xs" onClick={() => act(o.workOrderId, "complete")}><Check size={12} /> Complete</Button>}
                  {operate && (o.status === "Planned" || o.status === "Running") && <Button variant="ghost" className="py-1 text-xs text-bad" onClick={() => act(o.workOrderId, "cancel")} aria-label="Cancel order"><X size={12} /></Button>}
                  <Button variant="ghost" className="py-1 text-xs" onClick={() => setDetail(o)}><ListTree size={12} /> Details</Button>
                </td>
              </tr>
            );
          })}
        </Table>
      </Card>
      <Card title="Products">
        <Table head={["Code", "Name", "Ideal cycle", "Unit", ""]} empty={products.length === 0}>
          {products.filter((p) => p.isActive).map((p) => (
            <tr key={p.productId}><td className="px-3 py-2 font-mono text-xs">{p.code}</td><td className="px-3 py-2">{p.name}</td>
              <td className="px-3 py-2 tabular-nums">{p.idealCycleSec ? `${p.idealCycleSec} s` : "—"}</td><td className="px-3 py-2">{p.unit}</td>
              <td className="px-3 py-2 text-right"><Button variant="ghost" className="py-1 text-xs" onClick={() => setBomFor(p)}><ListTree size={12} /> BOM & routing</Button></td></tr>
          ))}
        </Table>
      </Card>
      <Modal open={!!bomFor} title={`${bomFor?.name ?? ""} — bill of materials and routing`} onClose={() => setBomFor(null)} wide>
        {bomFor && <BomRoutingEditor productId={bomFor.productId} canEdit={manage} onSaved={load} />}
      </Modal>
      <Modal open={!!detail} title={`Work order ${detail?.code ?? ""} · ${detail?.productName ?? ""}`} onClose={() => setDetail(null)} wide>
        {detail && <WorkOrderDetail workOrderId={detail.workOrderId} status={orders.find((o) => o.workOrderId === detail.workOrderId)?.status ?? detail.status} canOperate={operate} onChange={load} />}
      </Modal>
      <Modal open={newOrder} title="New work order" onClose={() => setNewOrder(false)}>
        <form onSubmit={createOrder} className="grid gap-4 sm:grid-cols-2">
          <Field label="Order code"><Input required value={wo.code} onChange={(e) => setWo({ ...wo, code: e.target.value })} placeholder="WO-2026-001" /></Field>
          <Field label="Quantity"><Input required type="number" min="1" value={wo.quantity} onChange={(e) => setWo({ ...wo, quantity: e.target.value })} /></Field>
          <Field label="Product"><Select required value={wo.productId} onChange={(e) => setWo({ ...wo, productId: e.target.value })}>
            <option value="">Choose…</option>{products.map((p) => <option key={p.productId} value={p.productId}>{p.code} · {p.name}</option>)}</Select></Field>
          <Field label="Machine"><Select required value={wo.assetId} onChange={(e) => setWo({ ...wo, assetId: e.target.value })}>
            <option value="">Choose…</option>{machines.map((m) => <option key={m.assetId} value={m.assetId}>{m.name}</option>)}</Select></Field>
          <Field label="Due date"><Input type="date" value={wo.dueDate} onChange={(e) => setWo({ ...wo, dueDate: e.target.value })} /></Field>
          <Field label="Lot number"><Input value={wo.lotNo} onChange={(e) => setWo({ ...wo, lotNo: e.target.value })} /></Field>
          <div className="flex justify-end gap-2 sm:col-span-2"><Button type="button" variant="ghost" onClick={() => setNewOrder(false)}>Cancel</Button><Button type="submit">Create</Button></div>
        </form>
      </Modal>
      <Modal open={newProduct} title="New product" onClose={() => setNewProduct(false)}>
        <form onSubmit={createProduct} className="grid gap-4 sm:grid-cols-2">
          <Field label="Code" hint="Letters, digits, dashes"><Input required value={pr.code} onChange={(e) => setPr({ ...pr, code: e.target.value })} /></Field>
          <Field label="Name"><Input required value={pr.name} onChange={(e) => setPr({ ...pr, name: e.target.value })} /></Field>
          <Field label="Ideal cycle time (s)"><Input type="number" step="0.1" min="0.1" value={pr.idealCycleSec} onChange={(e) => setPr({ ...pr, idealCycleSec: e.target.value })} /></Field>
          <Field label="Unit"><Input value={pr.unit} onChange={(e) => setPr({ ...pr, unit: e.target.value })} /></Field>
          <div className="flex justify-end gap-2 sm:col-span-2"><Button type="button" variant="ghost" onClick={() => setNewProduct(false)}>Cancel</Button><Button type="submit">Create</Button></div>
        </form>
      </Modal>
    </>
  );
}

function ShiftsTab() {
  const [date, setDate] = useState(() => new Date().toISOString().slice(0, 10));
  const [rows, setRows] = useState<ShiftReport[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    setRows(null);
    api<ShiftReport[]>(`/api/v1/production/shifts?date=${date}`).then(setRows).catch((e) => setError(errorText(e)));
  }, [date]);
  return (
    <>
      <ErrorNote error={error} />
      <div className="mb-4 flex items-center justify-end gap-2 text-sm"><span className="text-muted">Day</span>
        <Input type="date" value={date} onChange={(e) => setDate(e.target.value)} className="w-44 py-1" /></div>
      {!rows ? <p className="text-sm text-muted">Loading…</p> : (
        <div className="grid gap-4 lg:grid-cols-2">
          {rows.map((s) => (
            <Card key={s.shift} title={`${s.shift} shift · ${s.from.slice(11, 16)}–${s.to.slice(11, 16)}`}>
              <div className="mb-4 grid grid-cols-4 gap-2">
                <Ring pct={s.oee.value} label="OEE" size={84} tone={tone(s.oee.value) === "muted" ? "muted" : tone(s.oee.value)} />
                <Ring pct={s.oee.availability} label="Avail." size={84} tone="info" />
                <Ring pct={s.oee.performance} label="Perf." size={84} />
                <Ring pct={s.oee.quality} label="Quality" size={84} tone="ok" />
              </div>
              <p className="text-sm">{s.good.toLocaleString()} good · {s.scrap.toLocaleString()} scrap · {fmt(s.downtimeMinutes)} min downtime</p>
              {s.topReasons.length > 0 && <ul className="mt-2 text-xs text-muted">{s.topReasons.map((r) => <li key={r.reason}>{r.reason}: {Math.round(r.seconds / 60)} min ({r.count}×)</li>)}</ul>}
            </Card>
          ))}
        </div>
      )}
    </>
  );
}
