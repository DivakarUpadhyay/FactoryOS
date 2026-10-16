"use client";

import { useCallback, useEffect, useState } from "react";
import { ClipboardCheck, FileWarning, Gauge, Plus, TriangleAlert } from "lucide-react";
import { api } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { ControlChart, SpcLimits, SpcPoint } from "@/components/spc-chart";
import { InspectionsTab } from "@/components/inspections";
import { Badge, Button, Card, ErrorNote, Field, Input, Modal, PageHeader, Select, Stat, Table, cx, errorText } from "@/components/ui";

type Char = {
  charId: string; code: string; name: string; unit: string; productId: string | null; productName: string | null; assetId: string | null; assetName: string | null;
  tagId: string | null; tagName: string | null; nominal: number | null; lsl: number | null; usl: number | null; subgroupSize: number; sampleSeconds: number; isActive: boolean;
  measurements: number; lastAt: string | null;
};
type Capability = { cp: number | null; cpk: number | null; pp: number | null; ppk: number | null; mean: number; sigmaWithin: number; sigmaOverall: number; pctOutOfSpec: number };
type Chart = { chartType: string; subgroupSize: number; limits: SpcLimits | null; capability: Capability | null; points: SpcPoint[]; violations: number; verdict: string };
type Row = { charId: string; code: string; name: string; assetName: string | null; samples: number; cpk: number | null; ppk: number | null; violations: number; verdict: string };
type Summary = { openNcrs: number; criticalNcrs: number; characteristics: number; outOfControl: number; notCapable: number; rows: Row[] };
type Ncr = {
  ncId: string; code: string; title: string; description: string | null; severity: "Minor" | "Major" | "Critical"; status: string; productId: string | null; productName: string | null;
  assetId: string | null; assetName: string | null; lotNo: string | null; quantity: number | null; disposition: string | null; rootCause: string | null; correctiveAction: string | null;
  ownerId: string | null; ownerName: string | null; dueAt: string | null; createdAt: string; closedAt: string | null;
};
type Floor = { assetId: string; name: string };
type Tag = { tagId: string; code: string; name: string };
type Product = { productId: string; code: string; name: string };

const cpkTone = (v: number | null) => (v === null ? "muted" : v >= 1.33 ? "ok" : v >= 1 ? "warn" : "bad") as "ok" | "warn" | "bad" | "muted";
const sevTone = { Minor: "info", Major: "warn", Critical: "bad" } as const;

export default function QualityPage() {
  const { can } = useAuth();
  const [tab, setTab] = useState<"spc" | "insp" | "ncr">("spc");
  const [summary, setSummary] = useState<Summary | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(() => api<Summary>("/api/v1/quality/summary").then(setSummary).catch((e) => setError(errorText(e))), []);
  useEffect(() => {
    load();
    const t = setInterval(load, 60_000);
    return () => clearInterval(t);
  }, [load]);

  return (
    <>
      <PageHeader title="Quality / SPC" sub="Control charts with capability (Cp, Cpk) and run rules; non-conformances with root cause and corrective action." />
      <ErrorNote error={error} />
      {summary && (
        <div className="mb-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          <Stat label="Characteristics" value={summary.characteristics} icon={<ClipboardCheck size={16} />} />
          <Stat label="Out of control" value={summary.outOfControl} sub="Breaking a run rule" icon={<TriangleAlert size={16} />} />
          <Stat label="Not capable" value={summary.notCapable} sub="Cpk below 1.00" icon={<Gauge size={16} />} />
          <Stat label="Open non-conformances" value={summary.openNcrs} sub={`${summary.criticalNcrs} critical`} icon={<FileWarning size={16} />} />
        </div>
      )}
      <div className="mb-4 flex gap-1 border-b border-line">
        {([["spc", "SPC charts"], ["insp", "Inspections"], ["ncr", "Non-conformances"]] as const).map(([t, label]) => (
          <button key={t} onClick={() => setTab(t)} className={cx("-mb-px border-b-2 px-4 py-2 text-sm", tab === t ? "border-brand font-medium text-brand" : "border-transparent text-muted hover:text-ink")}>{label}</button>
        ))}
      </div>
      {tab === "insp" && <InspectionsTab canInspect={can("quality.execute")} canManage={can("quality.manage")} />}
      {tab === "insp" ? null : tab === "spc" ? <SpcTab summary={summary} canMeasure={can("quality.execute")} canManage={can("quality.manage")} onChange={load} /> : <NcrTab canCreate={can("quality.execute")} canManage={can("quality.manage")} onChange={load} />}
    </>
  );
}

function SpcTab({ summary, canMeasure, canManage, onChange }: { summary: Summary | null; canMeasure: boolean; canManage: boolean; onChange: () => void }) {
  const [chars, setChars] = useState<Char[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [chart, setChart] = useState<Chart | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [entry, setEntry] = useState<string[]>([]);
  const [lot, setLot] = useState("");
  const [newChar, setNewChar] = useState(false);
  const [assets, setAssets] = useState<Floor[]>([]);
  const [tags, setTags] = useState<Tag[]>([]);
  const [products, setProducts] = useState<Product[]>([]);
  const [cf, setCf] = useState({ code: "", name: "", unit: "", productId: "", assetId: "", tagId: "", nominal: "", lsl: "", usl: "", subgroupSize: "5", sampleSeconds: "60" });

  const loadChars = useCallback(() => api<Char[]>("/api/v1/quality/characteristics").then((c) => {
    setChars(c);
    setSelected((s) => s ?? c[0]?.charId ?? null);
  }).catch((e) => setError(errorText(e))), []);
  const loadChart = useCallback(() => {
    if (!selected) return;
    api<Chart>(`/api/v1/quality/characteristics/${selected}/spc`).then(setChart).catch((e) => setError(errorText(e)));
  }, [selected]);
  useEffect(() => { loadChars(); }, [loadChars]);
  useEffect(() => {
    loadChart();
    const t = setInterval(loadChart, 30_000);
    return () => clearInterval(t);
  }, [loadChart]);
  useEffect(() => {
    const c = chars.find((x) => x.charId === selected);
    setEntry(Array(c?.subgroupSize ?? 1).fill(""));
  }, [selected, chars]);
  useEffect(() => {
    if (!newChar) return;
    api<Floor[]>("/api/v1/plant/floor").then(setAssets).catch(() => undefined);
    api<Product[]>("/api/v1/production/products").then(setProducts).catch(() => undefined);
  }, [newChar]);
  useEffect(() => {
    if (cf.assetId) api<Tag[]>(`/api/v1/plant/assets/${cf.assetId}/tags`).then(setTags).catch(() => setTags([]));
    else setTags([]);
  }, [cf.assetId]);

  const c = chars.find((x) => x.charId === selected);
  async function measure(e: React.FormEvent) {
    e.preventDefault();
    if (!c) return;
    try {
      await api("/api/v1/quality/measurements", { method: "POST", json: { charId: c.charId, values: entry.map(Number), lotNo: lot || null } });
      setEntry(Array(c.subgroupSize).fill(""));
      loadChart();
      loadChars();
      onChange();
    } catch (err) {
      setError(errorText(err));
    }
  }
  async function createChar(e: React.FormEvent) {
    e.preventDefault();
    const n = (v: string) => (v === "" ? null : Number(v));
    try {
      const r = await api<{ charId: string }>("/api/v1/quality/characteristics", { method: "POST", json: {
        code: cf.code, name: cf.name, unit: cf.unit, productId: cf.productId || null, assetId: cf.assetId || null, tagId: cf.tagId || null,
        nominal: n(cf.nominal), lsl: n(cf.lsl), usl: n(cf.usl), subgroupSize: cf.tagId ? 1 : Number(cf.subgroupSize), sampleSeconds: Number(cf.sampleSeconds),
      } });
      setNewChar(false);
      setSelected(r.charId);
      loadChars();
      onChange();
    } catch (err) {
      setError(errorText(err));
    }
  }

  return (
    <>
      <ErrorNote error={error} />
      <div className="grid gap-4 xl:grid-cols-3">
        <Card title="Characteristics" actions={canManage && <Button variant="outline" className="py-1" onClick={() => setNewChar(true)}><Plus size={14} /> New</Button>}>
          <ul className="space-y-1">
            {(summary?.rows ?? []).map((r) => (
              <li key={r.charId}>
                <button onClick={() => setSelected(r.charId)} className={cx("w-full rounded-lg px-3 py-2 text-left text-sm", selected === r.charId ? "bg-brand/10 text-brand" : "hover:bg-panel-2")}>
                  <div className="flex items-center justify-between gap-2"><span className="font-medium">{r.name}</span><Badge tone={cpkTone(r.cpk)}>{r.cpk !== null ? `Cpk ${r.cpk}` : "—"}</Badge></div>
                  <div className="text-xs text-muted">{r.assetName ?? "—"} · {r.samples} values{r.violations > 0 && <span className="text-bad"> · {r.violations} signals</span>}</div>
                </button>
              </li>
            ))}
          </ul>
        </Card>
        <div className="space-y-4 xl:col-span-2">
          {c && (
            <Card title={`${c.name}${c.unit ? ` (${c.unit})` : ""}`} actions={<span className="text-xs text-muted">{c.tagName ? `from sensor ${c.tagName} every ${c.sampleSeconds}s` : `subgroups of ${c.subgroupSize}`}</span>}>
              {chart?.limits ? (
                <>
                  <p className={cx("mb-2 text-sm font-medium", chart.verdict.startsWith("Out") ? "text-bad" : chart.verdict.includes("not capable") ? "text-warn" : "text-ok")}>{chart.verdict}</p>
                  <ControlChart points={chart.points} value={(p) => p.mean} centre={chart.limits.centre} ucl={chart.limits.ucl} lcl={chart.limits.lcl} lsl={c.lsl} usl={c.usl}
                    label={chart.chartType === "I-MR" ? "Individual values" : "Subgroup means (X̄)"} />
                  <ControlChart points={chart.points.slice(chart.chartType === "I-MR" ? 1 : 0)} value={(p) => p.range} centre={chart.limits.rangeCentre} ucl={chart.limits.rangeUcl} lcl={chart.limits.rangeLcl}
                    label={chart.chartType === "I-MR" ? "Moving range" : "Ranges (R)"} height={140} />
                  {chart.capability && (
                    <div className="mt-3 grid grid-cols-3 gap-3 text-center text-sm sm:grid-cols-6">
                      {([["Cp", chart.capability.cp], ["Cpk", chart.capability.cpk], ["Pp", chart.capability.pp], ["Ppk", chart.capability.ppk]] as const).map(([k, v]) => (
                        <div key={k} className="rounded-lg bg-panel-2 p-2"><div className="text-xs text-muted">{k}</div><div className={cx("font-semibold", v !== null && v < 1 && "text-bad")}>{v ?? "—"}</div></div>
                      ))}
                      <div className="rounded-lg bg-panel-2 p-2"><div className="text-xs text-muted">Mean</div><div className="font-semibold">{chart.capability.mean.toFixed(4)}</div></div>
                      <div className="rounded-lg bg-panel-2 p-2"><div className="text-xs text-muted">Out of spec</div><div className="font-semibold">{chart.capability.pctOutOfSpec}%</div></div>
                    </div>
                  )}
                </>
              ) : <p className="text-sm text-muted">{chart?.verdict ?? "Loading…"}</p>}
            </Card>
          )}
          {c && canMeasure && !c.tagId && (
            <Card title={`Enter a subgroup of ${c.subgroupSize}`}>
              <form onSubmit={measure} className="flex flex-wrap items-end gap-2">
                {entry.map((v, i) => (
                  <Input key={i} required type="number" step="any" value={v} aria-label={`Value ${i + 1}`} className="w-24"
                    onChange={(e) => setEntry(entry.map((x, j) => (j === i ? e.target.value : x)))} placeholder={c.nominal?.toString() ?? ""} />
                ))}
                <Input value={lot} onChange={(e) => setLot(e.target.value)} placeholder="Lot (optional)" className="w-40" />
                <Button type="submit">Add</Button>
              </form>
            </Card>
          )}
        </div>
      </div>
      <Modal open={newChar} title="New characteristic" onClose={() => setNewChar(false)}>
        <form onSubmit={createChar} className="grid gap-4 sm:grid-cols-2">
          <Field label="Code"><Input required value={cf.code} onChange={(e) => setCf({ ...cf, code: e.target.value })} /></Field>
          <Field label="Name"><Input required value={cf.name} onChange={(e) => setCf({ ...cf, name: e.target.value })} placeholder="Shaft diameter" /></Field>
          <Field label="Unit"><Input value={cf.unit} onChange={(e) => setCf({ ...cf, unit: e.target.value })} placeholder="mm" /></Field>
          <Field label="Product"><Select value={cf.productId} onChange={(e) => setCf({ ...cf, productId: e.target.value })}><option value="">—</option>{products.map((p) => <option key={p.productId} value={p.productId}>{p.name}</option>)}</Select></Field>
          <Field label="Machine"><Select value={cf.assetId} onChange={(e) => setCf({ ...cf, assetId: e.target.value, tagId: "" })}><option value="">—</option>{assets.map((a) => <option key={a.assetId} value={a.assetId}>{a.name}</option>)}</Select></Field>
          <Field label="Feed from sensor" hint="Leave empty to enter measurements by hand"><Select value={cf.tagId} onChange={(e) => setCf({ ...cf, tagId: e.target.value })} disabled={!cf.assetId}>
            <option value="">Manual entry</option>{tags.map((t) => <option key={t.tagId} value={t.tagId}>{t.name}</option>)}</Select></Field>
          <Field label="Nominal"><Input type="number" step="any" value={cf.nominal} onChange={(e) => setCf({ ...cf, nominal: e.target.value })} /></Field>
          <Field label="Subgroup size"><Input type="number" min="1" max="10" value={cf.tagId ? "1" : cf.subgroupSize} disabled={!!cf.tagId} onChange={(e) => setCf({ ...cf, subgroupSize: e.target.value })} /></Field>
          <Field label="Lower spec limit (LSL)"><Input type="number" step="any" value={cf.lsl} onChange={(e) => setCf({ ...cf, lsl: e.target.value })} /></Field>
          <Field label="Upper spec limit (USL)"><Input type="number" step="any" value={cf.usl} onChange={(e) => setCf({ ...cf, usl: e.target.value })} /></Field>
          {cf.tagId && <Field label="Sample every (s)"><Input type="number" min="10" value={cf.sampleSeconds} onChange={(e) => setCf({ ...cf, sampleSeconds: e.target.value })} /></Field>}
          <div className="flex justify-end gap-2 sm:col-span-2"><Button type="button" variant="ghost" onClick={() => setNewChar(false)}>Cancel</Button><Button type="submit">Create</Button></div>
        </form>
      </Modal>
    </>
  );
}

function NcrTab({ canCreate, canManage, onChange }: { canCreate: boolean; canManage: boolean; onChange: () => void }) {
  const [rows, setRows] = useState<Ncr[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [edit, setEdit] = useState<(Partial<Ncr> & { status?: string }) | null>(null);
  const load = useCallback(() => api<Ncr[]>("/api/v1/quality/ncr").then(setRows).catch((e) => setError(errorText(e))), []);
  useEffect(() => { load(); }, [load]);

  async function save(e: React.FormEvent) {
    e.preventDefault();
    if (!edit) return;
    const body = {
      title: edit.title, description: edit.description || null, severity: edit.severity ?? "Minor", productId: edit.productId ?? null, assetId: edit.assetId ?? null,
      lotNo: edit.lotNo || null, quantity: edit.quantity ?? null, disposition: edit.disposition || null, rootCause: edit.rootCause || null,
      correctiveAction: edit.correctiveAction || null, ownerId: edit.ownerId ?? null, dueAt: edit.dueAt || null, status: edit.status,
    };
    try {
      if (edit.ncId) await api(`/api/v1/quality/ncr/${edit.ncId}`, { method: "PUT", json: body });
      else await api("/api/v1/quality/ncr", { method: "POST", json: body });
      setEdit(null);
      load();
      onChange();
    } catch (err) {
      setError(errorText(err));
    }
  }

  return (
    <>
      <ErrorNote error={error} />
      <Card title="Non-conformances" actions={canCreate && <Button className="py-1" onClick={() => setEdit({ severity: "Minor" })}><Plus size={14} /> Report</Button>}>
        <Table head={["NCR", "Severity", "Lot", "Qty", "Disposition", "Status", "Opened"]} empty={rows.length === 0}>
          {rows.map((n) => (
            <tr key={n.ncId} className={cx(canManage && "cursor-pointer hover:bg-panel-2")} onClick={() => canManage && setEdit({ ...n })}>
              <td className="px-3 py-2"><div className="font-medium">{n.title}</div><div className="font-mono text-[11px] text-muted">{n.code}</div></td>
              <td className="px-3 py-2"><Badge tone={sevTone[n.severity]}>{n.severity}</Badge></td>
              <td className="px-3 py-2 font-mono text-xs">{n.lotNo ?? "—"}</td>
              <td className="px-3 py-2 tabular-nums">{n.quantity ?? "—"}</td>
              <td className="px-3 py-2 text-sm">{n.disposition ?? "—"}</td>
              <td className="px-3 py-2"><Badge tone={n.status === "Closed" ? "muted" : n.status === "Open" ? "bad" : "warn"}>{n.status}</Badge></td>
              <td className="whitespace-nowrap px-3 py-2 text-xs">{new Date(n.createdAt + "Z").toLocaleDateString()}</td>
            </tr>
          ))}
        </Table>
      </Card>
      <Modal open={!!edit} title={edit?.ncId ? `Non-conformance ${edit.code}` : "Report a non-conformance"} onClose={() => setEdit(null)}>
        {edit && (
          <form onSubmit={save} className="grid gap-4 sm:grid-cols-2">
            <div className="sm:col-span-2"><Field label="What is wrong?"><Input required value={edit.title ?? ""} onChange={(e) => setEdit({ ...edit, title: e.target.value })} /></Field></div>
            <Field label="Severity"><Select value={edit.severity} onChange={(e) => setEdit({ ...edit, severity: e.target.value as Ncr["severity"] })}><option>Minor</option><option>Major</option><option>Critical</option></Select></Field>
            <Field label="Lot"><Input value={edit.lotNo ?? ""} onChange={(e) => setEdit({ ...edit, lotNo: e.target.value })} /></Field>
            <Field label="Quantity affected"><Input type="number" min="0" value={edit.quantity ?? ""} onChange={(e) => setEdit({ ...edit, quantity: e.target.value === "" ? null : Number(e.target.value) })} /></Field>
            <Field label="Description"><Input value={edit.description ?? ""} onChange={(e) => setEdit({ ...edit, description: e.target.value })} /></Field>
            {edit.ncId && (
              <>
                <Field label="Status"><Select value={edit.status} onChange={(e) => setEdit({ ...edit, status: e.target.value })}>{["Open", "Contained", "Investigating", "Closed"].map((s) => <option key={s}>{s}</option>)}</Select></Field>
                <Field label="Disposition"><Select value={edit.disposition ?? ""} onChange={(e) => setEdit({ ...edit, disposition: e.target.value || null })}><option value="">—</option>{["Rework", "Scrap", "UseAsIs", "Return"].map((d) => <option key={d}>{d}</option>)}</Select></Field>
                <div className="sm:col-span-2"><Field label="Root cause"><Input value={edit.rootCause ?? ""} onChange={(e) => setEdit({ ...edit, rootCause: e.target.value })} /></Field></div>
                <div className="sm:col-span-2"><Field label="Corrective action (CAPA)" hint="Needed, with the disposition and root cause, to close"><Input value={edit.correctiveAction ?? ""} onChange={(e) => setEdit({ ...edit, correctiveAction: e.target.value })} /></Field></div>
              </>
            )}
            <div className="flex justify-end gap-2 sm:col-span-2"><Button type="button" variant="ghost" onClick={() => setEdit(null)}>Cancel</Button><Button type="submit">Save</Button></div>
          </form>
        )}
      </Modal>
    </>
  );
}
