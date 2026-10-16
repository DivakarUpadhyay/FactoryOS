"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { ArrowDown, ArrowUp, Copy, Maximize2, Minimize2, Pencil, Plus, Save, Trash2, X } from "lucide-react";
import { api } from "@/lib/api";
import { useLive } from "@/lib/live";
import { statusTone } from "@/lib/types";
import { BarChart, HBars, Ring, fmt } from "@/components/charts";
import { Badge, Button, Card, ErrorNote, Field, Input, Modal, PageHeader, Select, cx, errorText } from "@/components/ui";

type Widget = { type: string; title?: string; wide?: boolean; metric?: string; assetId?: string; tagId?: string; text?: string };
type Dashboard = { dashboardId: string; name: string; isShared: boolean; ownerName: string; mine: boolean; widgets: Widget[] };

const catalogue: { type: string; label: string; hint: string }[] = [
  { type: "stat", label: "Key figure", hint: "One number: OEE, energy, cost, alarms, open orders, low stock" },
  { type: "oee", label: "OEE rings", hint: "Plant OEE with availability, performance, quality" },
  { type: "energy-hourly", label: "Energy per hour", hint: "Grid kWh per hour, peak hours highlighted" },
  { type: "output", label: "Output per hour", hint: "Parts made per hour across machines" },
  { type: "alarms", label: "Open alarms", hint: "Newest open alarms" },
  { type: "machines", label: "Machine states", hint: "Every machine coloured by state, live" },
  { type: "tag", label: "Live value", hint: "One sensor value, updated instantly" },
  { type: "fleet", label: "AGV fleet", hint: "Vehicles with battery and mission" },
  { type: "stock-low", label: "Low stock", hint: "Items below their minimum" },
  { type: "text", label: "Note", hint: "A text note for the team" },
];
const metrics = [
  { key: "oee", label: "Plant OEE (24 h)" }, { key: "kwh", label: "Grid energy (24 h)" }, { key: "cost", label: "Energy cost (24 h)" },
  { key: "peak", label: "Peak demand (24 h)" }, { key: "alarms", label: "Open alarms" }, { key: "orders", label: "Open maintenance orders" },
  { key: "good", label: "Good parts (24 h)" }, { key: "low", label: "Items below minimum" },
];
const since24 = () => `from=${new Date(Date.now() - 86400_000).toISOString()}&to=${new Date().toISOString()}`;

/** Polls a URL every 30 s for a widget; null while loading, undefined data on failure (e.g. module not in plan). */
function usePoll<T>(url: string | null, every = 30_000): { data: T | null; error: string | null } {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    if (!url) return;
    let alive = true;
    const run = () => api<T>(url.replace("{24h}", since24())).then((d) => { if (alive) { setData(d); setError(null); } }).catch((e) => alive && setError(errorText(e)));
    run();
    const t = setInterval(run, every);
    return () => { alive = false; clearInterval(t); };
  }, [url, every]);
  return { data, error };
}

export default function DashboardsPage() {
  const [boards, setBoards] = useState<Dashboard[]>([]);
  const [current, setCurrent] = useState<Dashboard | null>(null);
  const [edit, setEdit] = useState(false);
  const [draft, setDraft] = useState<Dashboard | null>(null);
  const [adding, setAdding] = useState<Widget | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback((selectId?: string) => api<Dashboard[]>("/api/v1/dashboards").then((b) => {
    setBoards(b);
    setCurrent((c) => b.find((x) => x.dashboardId === (selectId ?? c?.dashboardId)) ?? b[0] ?? null);
  }).catch((e) => setError(errorText(e))), []);
  useEffect(() => { load(); }, [load]);

  function startNew() {
    const d: Dashboard = { dashboardId: "", name: "My dashboard", isShared: false, ownerName: "", mine: true,
      widgets: [{ type: "stat", metric: "oee" }, { type: "stat", metric: "kwh" }, { type: "stat", metric: "alarms" }, { type: "oee", wide: true }, { type: "machines", wide: true }] };
    setDraft(d);
    setEdit(true);
  }
  async function save() {
    if (!draft) return;
    try {
      const body = { name: draft.name, isShared: draft.isShared, widgets: draft.widgets };
      if (draft.dashboardId) {
        await api(`/api/v1/dashboards/${draft.dashboardId}`, { method: "PUT", json: body });
        await load(draft.dashboardId);
      } else {
        const r = await api<{ dashboardId: string }>("/api/v1/dashboards", { method: "POST", json: body });
        await load(r.dashboardId);
      }
      setEdit(false);
      setDraft(null);
    } catch (err) {
      setError(errorText(err));
    }
  }
  async function remove() {
    if (!current || !confirm(`Delete the dashboard "${current.name}"?`)) return;
    await api(`/api/v1/dashboards/${current.dashboardId}`, { method: "DELETE" }).catch((e) => setError(errorText(e)));
    setCurrent(null);
    load();
  }
  const move = (i: number, d: number) => draft && setDraft({ ...draft, widgets: draft.widgets.map((w, j) => (j === i ? draft.widgets[i + d] : j === i + d ? draft.widgets[i] : w)) });
  const shown = edit ? draft : current;

  return (
    <>
      <PageHeader title="Dashboards" sub="Build your own screen from widgets — for the morning meeting, a line TV or your phone."
        actions={
          <div className="flex flex-wrap items-center gap-2">
            {!edit && boards.length > 0 && (
              <Select value={current?.dashboardId ?? ""} onChange={(e) => setCurrent(boards.find((b) => b.dashboardId === e.target.value) ?? null)} className="w-56 py-1" aria-label="Dashboard">
                {boards.map((b) => <option key={b.dashboardId} value={b.dashboardId}>{b.name}{b.mine ? "" : ` (by ${b.ownerName})`}</option>)}
              </Select>
            )}
            {!edit && current?.mine && <Button variant="outline" className="py-1" onClick={() => { setDraft(structuredClone(current)); setEdit(true); }}><Pencil size={14} /> Edit</Button>}
            {!edit && current && !current.mine && <Button variant="outline" className="py-1" onClick={() => { setDraft({ ...structuredClone(current), dashboardId: "", name: `${current.name} (copy)`, mine: true }); setEdit(true); }}><Copy size={14} /> Copy</Button>}
            {!edit && current?.mine && <Button variant="ghost" className="py-1 text-bad" onClick={remove} aria-label="Delete dashboard"><Trash2 size={14} /></Button>}
            {!edit && <Button className="py-1" onClick={startNew}><Plus size={14} /> New</Button>}
            {edit && <>
              <Button variant="outline" className="py-1" onClick={() => setAdding({ type: "stat", metric: "oee" })}><Plus size={14} /> Widget</Button>
              <Button variant="ghost" className="py-1" onClick={() => { setEdit(false); setDraft(null); }}>Cancel</Button>
              <Button className="py-1" onClick={save}><Save size={14} /> Save</Button>
            </>}
          </div>
        } />
      <ErrorNote error={error} />
      {edit && draft && (
        <Card className="mb-4">
          <div className="flex flex-wrap items-end gap-4">
            <Field label="Name"><Input value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} className="w-64" /></Field>
            <label className="mb-2 flex items-center gap-2 text-sm"><input type="checkbox" checked={draft.isShared} onChange={(e) => setDraft({ ...draft, isShared: e.target.checked })} /> Share with everyone in the company</label>
          </div>
        </Card>
      )}
      {!shown ? (
        <Card><p className="text-sm text-muted">No dashboards yet. Click <b>New</b> to build one.</p></Card>
      ) : (
        <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
          {shown.widgets.map((w, i) => (
            <div key={i} className={cx(w.wide && "md:col-span-2", "relative")}>
              {edit && (
                <div className="absolute right-2 top-2 z-10 flex gap-1 rounded-lg bg-panel/90 p-1 shadow">
                  <button onClick={() => move(i, -1)} disabled={i === 0} aria-label="Move left" className="rounded p-1 hover:bg-panel-2 disabled:opacity-30"><ArrowUp size={12} /></button>
                  <button onClick={() => move(i, 1)} disabled={i === shown.widgets.length - 1} aria-label="Move right" className="rounded p-1 hover:bg-panel-2 disabled:opacity-30"><ArrowDown size={12} /></button>
                  <button onClick={() => setDraft({ ...draft!, widgets: draft!.widgets.map((x, j) => (j === i ? { ...x, wide: !x.wide } : x)) })} aria-label="Width" className="rounded p-1 hover:bg-panel-2">{w.wide ? <Minimize2 size={12} /> : <Maximize2 size={12} />}</button>
                  <button onClick={() => setDraft({ ...draft!, widgets: draft!.widgets.filter((_, j) => j !== i) })} aria-label="Remove widget" className="rounded p-1 text-bad hover:bg-panel-2"><X size={12} /></button>
                </div>
              )}
              <WidgetView w={w} />
            </div>
          ))}
        </div>
      )}
      <Modal open={!!adding} title="Add a widget" onClose={() => setAdding(null)}>
        {adding && <AddWidget w={adding} onChange={setAdding} onAdd={() => { setDraft({ ...draft!, widgets: [...draft!.widgets, adding] }); setAdding(null); }} />}
      </Modal>
    </>
  );
}

function AddWidget({ w, onChange, onAdd }: { w: Widget; onChange: (w: Widget) => void; onAdd: () => void }) {
  const [assets, setAssets] = useState<{ assetId: string; name: string }[]>([]);
  const [tags, setTags] = useState<{ tagId: string; name: string; unit: string }[]>([]);
  useEffect(() => { if (w.type === "tag") api<{ assetId: string; name: string }[]>("/api/v1/plant/floor").then(setAssets).catch(() => undefined); }, [w.type]);
  useEffect(() => { if (w.assetId) api<{ tagId: string; name: string; unit: string }[]>(`/api/v1/plant/assets/${w.assetId}/tags`).then(setTags).catch(() => undefined); }, [w.assetId]);
  return (
    <div className="space-y-4">
      <div className="grid gap-2 sm:grid-cols-2">
        {catalogue.map((c) => (
          <button key={c.type} onClick={() => onChange({ type: c.type, metric: c.type === "stat" ? "oee" : undefined })}
            className={cx("rounded-lg border p-3 text-left text-sm", w.type === c.type ? "border-brand bg-brand/10" : "border-line hover:bg-panel-2")}>
            <div className="font-medium">{c.label}</div><div className="text-xs text-muted">{c.hint}</div>
          </button>
        ))}
      </div>
      {w.type === "stat" && <Field label="Figure"><Select value={w.metric} onChange={(e) => onChange({ ...w, metric: e.target.value })}>{metrics.map((m) => <option key={m.key} value={m.key}>{m.label}</option>)}</Select></Field>}
      {w.type === "tag" && <>
        <Field label="Machine"><Select value={w.assetId ?? ""} onChange={(e) => onChange({ ...w, assetId: e.target.value, tagId: undefined })}><option value="">Choose…</option>{assets.map((a) => <option key={a.assetId} value={a.assetId}>{a.name}</option>)}</Select></Field>
        <Field label="Value"><Select value={w.tagId ?? ""} onChange={(e) => onChange({ ...w, tagId: e.target.value, title: `${assets.find((a) => a.assetId === w.assetId)?.name} · ${tags.find((t) => t.tagId === e.target.value)?.name}` })}><option value="">Choose…</option>{tags.map((t) => <option key={t.tagId} value={t.tagId}>{t.name}{t.unit ? ` (${t.unit})` : ""}</option>)}</Select></Field>
      </>}
      {w.type === "text" && <Field label="Text"><Input value={w.text ?? ""} onChange={(e) => onChange({ ...w, text: e.target.value })} placeholder="Target this week: OEE 75 %, zero safety incidents" /></Field>}
      <Field label="Title (optional)"><Input value={w.title ?? ""} onChange={(e) => onChange({ ...w, title: e.target.value })} /></Field>
      <div className="flex justify-end"><Button onClick={onAdd} disabled={w.type === "tag" && !w.tagId}>Add</Button></div>
    </div>
  );
}

function WidgetView({ w }: { w: Widget }) {
  switch (w.type) {
    case "stat": return <StatWidget w={w} />;
    case "oee": return <OeeWidget w={w} />;
    case "energy-hourly": return <EnergyWidget w={w} />;
    case "output": return <OutputWidget w={w} />;
    case "alarms": return <AlarmsWidget w={w} />;
    case "machines": return <MachinesWidget w={w} />;
    case "tag": return <TagWidget w={w} />;
    case "fleet": return <FleetWidget w={w} />;
    case "stock-low": return <StockWidget w={w} />;
    default: return <Card title={w.title || "Note"}><p className="whitespace-pre-wrap text-sm">{w.text}</p></Card>;
  }
}

const Unavailable = ({ error }: { error: string | null }) => <p className="text-sm text-muted">{error ?? "Loading…"}</p>;

function StatWidget({ w }: { w: Widget }) {
  const src: Record<string, string> = { oee: "/api/v1/production/oee?{24h}", good: "/api/v1/production/oee?{24h}", kwh: "/api/v1/energy/summary?{24h}", cost: "/api/v1/energy/summary?{24h}",
    peak: "/api/v1/energy/summary?{24h}", alarms: "/api/v1/alarms/summary", orders: "/api/v1/maintenance/kpis", low: "/api/v1/inventory/items" };
  const { data, error } = usePoll<Record<string, unknown> | unknown[]>(src[w.metric ?? "oee"]);
  const d = data as Record<string, never> | null;
  const value = !data ? null : ({
    oee: () => `${(d!.plant as { value: number | null }).value ?? "—"}%`, good: () => Number(d!.good).toLocaleString(), kwh: () => `${fmt(Number(d!.gridKwh))} kWh`,
    cost: () => `${d!.currency} ${Number(d!.cost).toLocaleString()}`, peak: () => `${fmt(Number(d!.peakKw))} kW`, alarms: () => String(d!.active),
    orders: () => String(Number(d!.open) + Number(d!.inProgress)), low: () => String((data as { low: boolean }[]).filter((i) => i.low).length),
  } as Record<string, () => string>)[w.metric ?? "oee"]?.();
  return (
    <Card>
      <div className="text-xs uppercase tracking-wide text-muted">{w.title || metrics.find((m) => m.key === w.metric)?.label}</div>
      <div className="mt-2 text-3xl font-semibold">{value ?? <span className="text-base text-muted">{error ? "—" : "…"}</span>}</div>
      {error && <div className="mt-1 text-xs text-muted">{error}</div>}
    </Card>
  );
}

function OeeWidget({ w }: { w: Widget }) {
  const { data, error } = usePoll<{ plant: { value: number | null; availability: number | null; performance: number | null; quality: number | null } }>("/api/v1/production/oee?{24h}");
  return (
    <Card title={w.title || "OEE — last 24 hours"}>
      {!data ? <Unavailable error={error} /> : (
        <div className="grid grid-cols-4 gap-2">
          <Ring pct={data.plant.value} label="OEE" size={90} /><Ring pct={data.plant.availability} label="Avail." size={90} tone="info" />
          <Ring pct={data.plant.performance} label="Perf." size={90} /><Ring pct={data.plant.quality} label="Quality" size={90} tone="ok" />
        </div>
      )}
    </Card>
  );
}

function EnergyWidget({ w }: { w: Widget }) {
  const { data, error } = usePoll<{ hourly: { hour: string; kwh: number; peak: boolean }[] }>("/api/v1/energy/summary?{24h}");
  return <Card title={w.title || "Grid energy per hour"}>{!data ? <Unavailable error={error} /> : <BarChart bars={data.hourly.map((h) => ({ label: h.hour.slice(11, 13), value: h.kwh, tone: h.peak ? "warn" as const : "brand" as const }))} height={150} unit=" kWh" />}</Card>;
}

function OutputWidget({ w }: { w: Widget }) {
  const { data, error } = usePoll<{ hourlyOutput: { hour: string; parts: number }[] }>("/api/v1/production/oee?{24h}");
  return <Card title={w.title || "Parts per hour"}>{!data ? <Unavailable error={error} /> : <BarChart bars={data.hourlyOutput.map((h) => ({ label: h.hour.slice(11, 13), value: h.parts, tone: "ok" as const }))} height={150} />}</Card>;
}

function AlarmsWidget({ w }: { w: Widget }) {
  const { data, error } = usePoll<{ alarmId: number; severity: "Critical" | "Warning" | "Info"; message: string }[]>("/api/v1/alarms?state=open", 15_000);
  const tone = { Critical: "bad", Warning: "warn", Info: "info" } as const;
  return (
    <Card title={w.title || "Open alarms"} actions={<Link href="/app/alarms" className="text-xs text-brand">All →</Link>}>
      {!data ? <Unavailable error={error} /> : data.length === 0 ? <p className="text-sm text-muted">All quiet.</p> : (
        <ul className="space-y-2 text-sm">{data.slice(0, 6).map((a) => <li key={a.alarmId} className="flex gap-2"><Badge tone={tone[a.severity]}>{a.severity}</Badge><span className="line-clamp-2">{a.message}</span></li>)}</ul>
      )}
    </Card>
  );
}

function MachinesWidget({ w }: { w: Widget }) {
  const [rows, setRows] = useState<{ assetId: string; name: string; status: keyof typeof statusTone; category: string }[] | null>(null);
  useEffect(() => { api<typeof rows>("/api/v1/plant/floor").then((r) => setRows(r!.filter((a) => a.category === "Production" || a.category === "Robotics"))).catch(() => setRows([])); }, []);
  useLive({ status: (msgs) => setRows((cur) => cur && cur.map((a) => { const m = msgs.find((x) => x.assetId === a.assetId); return m ? { ...a, status: m.status as typeof a.status } : a; })) });
  return (
    <Card title={w.title || "Machine states (live)"}>
      {!rows ? <Unavailable error={null} /> : (
        <div className="grid grid-cols-2 gap-2 sm:grid-cols-3">
          {rows.map((a) => <Link key={a.assetId} href={`/app/plant/assets/${a.assetId}`} className="flex items-center justify-between gap-2 rounded-lg border border-line px-2 py-1.5 text-xs hover:bg-panel-2"><span className="truncate">{a.name}</span><Badge tone={statusTone[a.status] ?? "muted"}>{a.status}</Badge></Link>)}
        </div>
      )}
    </Card>
  );
}

function TagWidget({ w }: { w: Widget }) {
  const [v, setV] = useState<{ value: number | null; unit: string; name: string } | null>(null);
  useEffect(() => {
    if (!w.assetId) return;
    api<{ values: { tagId: string; value: number | null; unit: string; name: string }[] }>(`/api/v1/plant/assets/${w.assetId}/live`)
      .then((l) => { const t = l.values.find((x) => x.tagId === w.tagId); if (t) setV({ value: t.value, unit: t.unit, name: t.name }); }).catch(() => undefined);
  }, [w.assetId, w.tagId]);
  useLive({ values: (msgs) => { const m = msgs.find((x) => x.tagId === w.tagId); if (m) setV((cur) => (cur ? { ...cur, value: m.value } : cur)); } });
  return (
    <Card>
      <div className="text-xs uppercase tracking-wide text-muted">{w.title || v?.name || "Live value"}</div>
      <div className="mt-2 text-3xl font-semibold tabular-nums">{v?.value != null ? fmt(v.value) : "—"} <span className="text-base font-normal text-muted">{v?.unit}</span></div>
      <div className="mt-1 text-[11px] text-ok">● live</div>
    </Card>
  );
}

function FleetWidget({ w }: { w: Widget }) {
  const { data, error } = usePoll<{ vehicles: { assetId: string; name: string; status: string; battery: number | null; currentMission: string | null }[] }>("/api/v1/robotics/fleet", 10_000);
  return <Card title={w.title || "AGV fleet"}>{!data ? <Unavailable error={error} /> : <HBars rows={data.vehicles.map((v) => ({ label: `${v.name} · ${v.status}`, value: v.battery ?? 0, sub: v.currentMission ?? undefined, tone: (v.battery ?? 0) < 25 ? "warn" as const : "ok" as const }))} unit="%" max={100} />}</Card>;
}

function StockWidget({ w }: { w: Widget }) {
  const { data, error } = usePoll<{ itemId: string; name: string; onHand: number; minStock: number; unit: string; low: boolean }[]>("/api/v1/inventory/items", 60_000);
  const low = data?.filter((i) => i.low) ?? [];
  return (
    <Card title={w.title || "Below minimum stock"}>
      {!data ? <Unavailable error={error} /> : low.length === 0 ? <p className="text-sm text-muted">Everything stocked.</p> : (
        <ul className="space-y-1 text-sm">{low.map((i) => <li key={i.itemId} className="flex justify-between"><span>{i.name}</span><span className="tabular-nums text-bad">{i.onHand} / {i.minStock} {i.unit}</span></li>)}</ul>
      )}
    </Card>
  );
}
