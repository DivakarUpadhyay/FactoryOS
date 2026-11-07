"use client";

import { useCallback, useEffect, useState } from "react";
import { CheckCircle2, CircleAlert, CircleDashed, Plus, Trash2 } from "lucide-react";
import { api } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { BarChart, HBars, Ring, fmt } from "@/components/charts";
import { Badge, Button, Card, ErrorNote, Field, Input, Modal, PageHeader, Select, Stat, Table, cx, errorText } from "@/components/ui";
import { ActivitiesTab, MetersTab, MvTab } from "./extra";

// ---------- types (API shapes) ----------
type Clause = { clause: string; title: string; state: "ok" | "warn" | "missing"; evidence: string };
type EnpiDay = { day: string; kwh: number; parts: number; expected: number; savings: number; cusum: number };
type EnpiResult = {
  enpiId: string; name: string; kind: string; from: string; to: string; actualKwh: number; expectedKwh: number; savingsKwh: number; improvementPct: number;
  baselineIntensity: number | null; reportingIntensity: number | null; r2: number | null; model: string; days: EnpiDay[]; notes: string[];
};
type Progress = { objectiveId: string; title: string; targetPct: number; targetDate: string; actualPct: number | null; state: string };
type Overview = {
  readinessPct: number; clauses: Clause[]; enpis: EnpiResult[]; objectives: Progress[]; openActions: number; expectedKwhYear: number; verifiedKwhYear: number;
  openNonconformities: number; lastAudit: string | null; lastManagementReview: string | null;
};
type Use = { assetId: string; name: string; category: string; kwh: number; sharePct: number; cumulativePct: number; suggested: boolean; seu: string | null };
type Source = { source: string; unit: string; quantity: number; kwh: number; co2eKg: number; factor: string | null };
type Review = { from: string; to: string; totalKwh: number; meteredMachinesKwh: number; unmeteredKwh: number; sources: Source[]; uses: Use[]; notes: string[] };
type Factor = {
  factorId: string; source: string; name: string; scope: number; method: string; region: string; year: number; unit: string; heatKcalPerUnit: number | null;
  co2KgPerTj: number | null; ch4KgPerTj: number | null; n2oKgPerTj: number | null; kgCo2ePerUnit: number | null; gwpSet: string; reference: string;
  isActive: boolean; co2e: number; scope3Category: number | null;
};
type Entry = { entryId: string; source: string; periodFrom: string; periodTo: string; quantity: number; unit: string; cost: number | null; instrument: string | null; reference: string | null; note: string | null };
type Seu = { seuId: string; name: string; assetIds: string; relevantVariables: string | null; operatingCriteria: string | null; owner: string | null };
type Enpi = { enpiId: string; name: string; seuId: string | null; seuName: string | null; kind: string; baselineFrom: string; baselineTo: string; baselineKwh: number | null; baselineParts: number | null; baselineDays: number | null; r2: number | null };
type Objective = { objectiveId: string; title: string; enpiId: string | null; enpiName: string | null; targetPct: number; targetDate: string; owner: string | null };
type Action = { actionId: string; objectiveId: string | null; objectiveTitle: string | null; title: string; owner: string | null; dueDate: string | null; expectedKwhYear: number | null; investment: number | null; verification: string | null; verifiedKwhYear: number | null; status: string };
type Doc = { documentId: string; kind: string; title: string; body: string | null; owner: string | null; approvedBy: string | null; approvedOn: string | null; reviewDue: string | null; updatedAt: string };
type Finding = { findingId: string; kind: string; clause: string | null; title: string; detail: string | null; rootCause: string | null; correctiveAction: string | null; owner: string | null; heldOn: string; dueDate: string | null; closedOn: string | null };
type Asset = { assetId: string; name: string; code: string };

const TABS = [
  ["overview", "Overview"], ["review", "Energy review & SEUs"], ["enpis", "EnPIs & baselines"], ["plans", "Objectives & actions"],
  ["mv", "Savings M&V"], ["meters", "Meters & calibration"], ["factors", "Emission factors"], ["data", "Energy data"], ["activities", "GHG activities"],
  ["documents", "Documents"], ["audits", "Audits & reviews"],
] as const;
type Tab = (typeof TABS)[number][0];

const DOC_KINDS: [string, string][] = [
  ["Policy", "5.2 Energy policy"], ["Scope", "4.3 Scope and boundaries"], ["Parties", "4.1–4.2 Context and interested parties"], ["Team", "5.3 Energy team and roles"],
  ["Risk", "6.1 Risks and opportunities"], ["Legal", "Legal and other requirements"], ["DataPlan", "6.6 Energy data collection plan"],
  ["Competence", "7.2–7.3 Competence and awareness"], ["Communication", "7.4 Communication"], ["Design", "8.2 Design"], ["Procurement", "8.3 Procurement"],
];
const SOURCES: [string, string][] = [
  ["electricity", "Electricity (unmetered site / bill)"], ["natural_gas", "Natural gas"], ["diesel", "Diesel"], ["gasoline", "Gasoline"], ["lpg", "LPG"],
  ["fuel_oil", "Fuel oil"], ["steam", "Purchased steam"], ["green_power", "Green power (T-REC / PPA)"],
];
const day = (s: string | null | undefined) => (s ? s.slice(0, 10) : "—");
const todayIso = () => new Date().toISOString().slice(0, 10);
const num = (v: string) => (v === "" ? null : Number(v));

function StateIcon({ state }: { state: string }) {
  if (state === "ok") return <CheckCircle2 size={16} className="text-ok" />;
  if (state === "warn") return <CircleAlert size={16} className="text-warn" />;
  return <CircleDashed size={16} className="text-muted" />;
}

export default function EnmsPage() {
  const { can } = useAuth();
  const edit = can("energy.manage");
  const [tab, setTab] = useState<Tab>("overview");
  const [error, setError] = useState<string | null>(null);

  return (
    <>
      <PageHeader title="ISO 50001 energy management" sub="Energy review, SEUs, EnPIs and baselines (ISO 50006), savings M&V (ISO 50015 / IPMVP), meter calibration, emission factors and audit records — built on the metered data." />
      <div className="mb-4 flex gap-1 overflow-x-auto border-b border-line">
        {TABS.map(([t, label]) => (
          <button key={t} onClick={() => { setError(null); setTab(t); }} className={cx("-mb-px whitespace-nowrap border-b-2 px-4 py-2 text-sm", tab === t ? "border-brand font-medium text-brand" : "border-transparent text-muted hover:text-ink")}>{label}</button>
        ))}
      </div>
      <ErrorNote error={error} />
      <div className="mt-2">
        {tab === "overview" && <OverviewTab onError={setError} />}
        {tab === "review" && <ReviewTab edit={edit} onError={setError} />}
        {tab === "enpis" && <EnpiTab edit={edit} onError={setError} />}
        {tab === "plans" && <PlansTab edit={edit} onError={setError} />}
        {tab === "mv" && <MvTab edit={edit} onError={setError} />}
        {tab === "meters" && <MetersTab edit={edit} onError={setError} />}
        {tab === "factors" && <FactorsTab edit={edit} onError={setError} />}
        {tab === "activities" && <ActivitiesTab edit={edit} onError={setError} />}
        {tab === "data" && <DataTab edit={edit} onError={setError} />}
        {tab === "documents" && <DocumentsTab edit={edit} onError={setError} />}
        {tab === "audits" && <AuditsTab edit={edit} onError={setError} />}
      </div>
    </>
  );
}

type TabProps = { edit?: boolean; onError: (e: string | null) => void };

function useLoad<T>(url: string, onError: (e: string | null) => void): [T | null, () => void] {
  const [data, setData] = useState<T | null>(null);
  const load = useCallback(() => { api<T>(url).then(setData).catch((e) => onError(errorText(e))); }, [url, onError]);
  useEffect(load, [load]);
  return [data, load];
}

async function run(onError: (e: string | null) => void, fn: () => Promise<unknown>, after: () => void) {
  onError(null);
  try { await fn(); after(); } catch (e) { onError(errorText(e)); }
}

// ---------- Overview ----------
function OverviewTab({ onError }: TabProps) {
  const [o] = useLoad<Overview>("/api/v1/enms/overview", onError);
  if (!o) return null;
  const ok = o.clauses.filter((c) => c.state === "ok").length;
  return (
    <>
      <div className="mb-6 grid gap-4 lg:grid-cols-4">
        <Card className="flex items-center justify-center"><Ring pct={o.readinessPct} label="EnMS readiness" tone={o.readinessPct >= 80 ? "ok" : o.readinessPct >= 50 ? "warn" : "bad"} /></Card>
        <Stat label="Clauses with evidence" value={`${ok} / ${o.clauses.length}`} sub="ISO 50001:2018 clauses 4–10" />
        <Stat label="Savings (actions)" value={`${fmt(o.verifiedKwhYear)} kWh/yr`} sub={`verified · ${fmt(o.expectedKwhYear)} kWh/yr planned · ${o.openActions} open`} />
        <Stat label="Open nonconformities" value={o.openNonconformities} sub={`Last audit ${day(o.lastAudit)} · last review ${day(o.lastManagementReview)}`} />
      </div>
      <div className="mb-6 grid gap-4 lg:grid-cols-2">
        <Card title="EnPIs — last 7 complete days against the baseline">
          {o.enpis.length === 0 ? <p className="text-sm text-muted">No EnPIs yet. Create one under “EnPIs & baselines”.</p> : (
            <Table head={["EnPI", "Actual kWh", "Expected kWh", "Improvement"]}>
              {o.enpis.map((e) => (
                <tr key={e.enpiId}>
                  <td className="px-3 py-2">{e.name}<div className="text-xs text-muted">{e.model}</div></td>
                  <td className="px-3 py-2 tabular-nums">{fmt(e.actualKwh)}</td>
                  <td className="px-3 py-2 tabular-nums">{fmt(e.expectedKwh)}</td>
                  <td className={cx("px-3 py-2 tabular-nums font-medium", e.improvementPct >= 0 ? "text-ok" : "text-bad")}>{e.improvementPct > 0 ? "+" : ""}{e.improvementPct}%</td>
                </tr>
              ))}
            </Table>
          )}
        </Card>
        <Card title="Objectives and energy targets (6.2)">
          {o.objectives.length === 0 ? <p className="text-sm text-muted">No objectives yet.</p> : (
            <Table head={["Objective", "Target", "Now", "State"]}>
              {o.objectives.map((p) => (
                <tr key={p.objectiveId}>
                  <td className="px-3 py-2">{p.title}<div className="text-xs text-muted">by {day(p.targetDate)}</div></td>
                  <td className="px-3 py-2 tabular-nums">{p.targetPct}%</td>
                  <td className="px-3 py-2 tabular-nums">{p.actualPct === null ? "—" : `${p.actualPct}%`}</td>
                  <td className="px-3 py-2"><Badge tone={p.state === "met" ? "ok" : p.state === "on track" ? "brand" : p.state === "no EnPI" ? "muted" : "warn"}>{p.state}</Badge></td>
                </tr>
              ))}
            </Table>
          )}
        </Card>
      </div>
      <Card title="Clause-by-clause readiness (what an auditor will ask for)">
        <Table head={["", "Clause", "Requirement", "Evidence in FactoryOS"]}>
          {o.clauses.map((c) => (
            <tr key={c.clause}>
              <td className="px-3 py-2"><StateIcon state={c.state} /></td>
              <td className="px-3 py-2 font-mono text-xs">{c.clause}</td>
              <td className="px-3 py-2">{c.title}</td>
              <td className="px-3 py-2 text-muted">{c.evidence}</td>
            </tr>
          ))}
        </Table>
      </Card>
    </>
  );
}

// ---------- Energy review & SEUs ----------
function ReviewTab({ edit, onError }: TabProps) {
  const [r] = useLoad<Review>("/api/v1/enms/review", onError);
  const [seus, loadSeus] = useLoad<Seu[]>("/api/v1/enms/seus", onError);
  const [assets] = useLoad<Asset[]>("/api/v1/plant/floor", onError);
  const [open, setOpen] = useState<Partial<Seu> & { ids?: string[] } | null>(null);
  const names = new Map((assets ?? []).map((a) => [a.assetId, a.name]));

  function save() {
    if (!open) return;
    const body = { name: open.name ?? "", assetIds: open.ids ?? [], relevantVariables: open.relevantVariables ?? null, operatingCriteria: open.operatingCriteria ?? null, owner: open.owner ?? null };
    run(onError, () => open.seuId ? api(`/api/v1/enms/seus/${open.seuId}`, { method: "PUT", json: body }) : api("/api/v1/enms/seus", { method: "POST", json: body }), () => { setOpen(null); loadSeus(); });
  }

  return (
    <>
      {r && (
        <>
          <div className="mb-6 grid gap-4 sm:grid-cols-3">
            <Stat label="Energy use (all sources)" value={`${fmt(r.totalKwh)} kWh`} sub={`${day(r.from)} to ${day(r.to)}`} />
            <Stat label="On machine meters" value={`${fmt(r.meteredMachinesKwh)} kWh`} />
            <Stat label="Not on a machine meter" value={`${fmt(r.unmeteredKwh)} kWh`} sub="lighting, HVAC, losses, unmetered loads" />
          </div>
          {r.notes.length > 0 && <Card title="Findings of the review" className="mb-6"><ul className="list-disc space-y-1 pl-5 text-sm">{r.notes.map((n) => <li key={n}>{n}</li>)}</ul></Card>}
          <div className="mb-6 grid gap-4 lg:grid-cols-2">
            <Card title="Energy by source (kWh equivalent, net heat value)">
              <Table head={["Source", "Quantity", "kWh eq.", "kg CO₂e", "Factor"]}>
                {r.sources.map((s, i) => (
                  <tr key={`${s.source}-${s.unit}-${i}`}>
                    <td className="px-3 py-2">{s.source.replace("_", " ")}</td>
                    <td className="px-3 py-2 tabular-nums">{fmt(s.quantity)} {s.unit}</td>
                    <td className="px-3 py-2 tabular-nums">{fmt(s.kwh)}</td>
                    <td className="px-3 py-2 tabular-nums">{fmt(s.co2eKg)}</td>
                    <td className="px-3 py-2 text-xs text-muted">{s.factor ?? "—"}</td>
                  </tr>
                ))}
              </Table>
            </Card>
            <Card title="Electricity by machine (Pareto)">
              <HBars rows={r.uses.slice(0, 12).map((u) => ({ label: u.name, value: u.kwh, sub: `${u.sharePct}% · cum. ${u.cumulativePct}%`, tone: u.suggested ? "warn" as const : "ok" as const }))} unit=" kWh" />
            </Card>
          </div>
          <Card title="Uses and significance (largest uses that together make 80 % of electricity are suggested as SEUs)" className="mb-6">
            <Table head={["Asset", "kWh", "Share", "Cumulative", "Significant?"]}>
              {r.uses.map((u) => (
                <tr key={u.assetId}>
                  <td className="px-3 py-2">{u.name}</td>
                  <td className="px-3 py-2 tabular-nums">{fmt(u.kwh)}</td>
                  <td className="px-3 py-2 tabular-nums">{u.sharePct}%</td>
                  <td className="px-3 py-2 tabular-nums">{u.cumulativePct}%</td>
                  <td className="px-3 py-2">{u.seu ? <Badge tone="ok">SEU: {u.seu}</Badge> : u.suggested ? <Badge tone="warn">suggested</Badge> : <span className="text-muted">—</span>}</td>
                </tr>
              ))}
            </Table>
          </Card>
        </>
      )}
      <Card title="Significant energy uses (6.3, 8.1)" actions={edit && <Button className="py-1" onClick={() => setOpen({ ids: (r?.uses ?? []).filter((u) => u.suggested && !u.seu).map((u) => u.assetId).slice(0, 3) })}><Plus size={14} /> SEU</Button>}>
        <Table head={["SEU", "Assets", "Relevant variables", "Operating criteria", "Owner", ""]} empty={(seus ?? []).length === 0}>
          {(seus ?? []).map((s) => {
            const ids: string[] = JSON.parse(s.assetIds);
            return (
              <tr key={s.seuId}>
                <td className="px-3 py-2 font-medium">{s.name}</td>
                <td className="px-3 py-2 text-xs">{ids.map((i) => names.get(i) ?? i.slice(0, 8)).join(", ")}</td>
                <td className="px-3 py-2">{s.relevantVariables ?? "—"}</td>
                <td className="px-3 py-2 text-xs">{s.operatingCriteria ?? "—"}</td>
                <td className="px-3 py-2">{s.owner ?? "—"}</td>
                <td className="px-3 py-2 text-right">{edit && (
                  <span className="flex justify-end gap-1">
                    <Button variant="ghost" className="py-1" onClick={() => setOpen({ ...s, ids })}>Edit</Button>
                    <Button variant="ghost" className="py-1" aria-label="Delete" onClick={() => run(onError, () => api(`/api/v1/enms/seus/${s.seuId}`, { method: "DELETE" }), loadSeus)}><Trash2 size={14} /></Button>
                  </span>
                )}</td>
              </tr>
            );
          })}
        </Table>
      </Card>
      <Modal open={!!open} title={open?.seuId ? "Edit significant energy use" : "New significant energy use"} onClose={() => setOpen(null)} wide>
        {open && (
          <div className="space-y-3">
            <Field label="Name"><Input value={open.name ?? ""} onChange={(e) => setOpen({ ...open, name: e.target.value })} placeholder="CNC machining cell" /></Field>
            <Field label="Assets" hint="Hold Ctrl to pick several.">
              <Select multiple size={8} value={open.ids ?? []} onChange={(e) => setOpen({ ...open, ids: Array.from(e.target.selectedOptions).map((o) => o.value) })}>
                {(assets ?? []).map((a) => <option key={a.assetId} value={a.assetId}>{a.name} ({a.code})</option>)}
              </Select>
            </Field>
            <Field label="Relevant variables" hint="What drives its energy: parts made, shift hours, outdoor temperature…"><Input value={open.relevantVariables ?? ""} onChange={(e) => setOpen({ ...open, relevantVariables: e.target.value })} /></Field>
            <Field label="Operating criteria (8.1)" hint="Settings and limits that keep it efficient."><Input value={open.operatingCriteria ?? ""} onChange={(e) => setOpen({ ...open, operatingCriteria: e.target.value })} /></Field>
            <Field label="Owner"><Input value={open.owner ?? ""} onChange={(e) => setOpen({ ...open, owner: e.target.value })} /></Field>
            <div className="flex justify-end"><Button onClick={save}>Save</Button></div>
          </div>
        )}
      </Modal>
    </>
  );
}

// ---------- EnPIs & baselines ----------
function EnpiTab({ edit, onError }: TabProps) {
  const [enpis, load] = useLoad<Enpi[]>("/api/v1/enms/enpis", onError);
  const [seus] = useLoad<Seu[]>("/api/v1/enms/seus", onError);
  const [sel, setSel] = useState<string | null>(null);
  const [res, setRes] = useState<EnpiResult | null>(null);
  const [form, setForm] = useState<{ name: string; seuId: string; kind: string; baselineFrom: string; baselineTo: string } | null>(null);

  const current = sel ?? enpis?.[0]?.enpiId ?? null;
  useEffect(() => {
    if (!current) { setRes(null); return; }
    api<EnpiResult>(`/api/v1/enms/enpis/${current}/results`).then(setRes).catch((e) => onError(errorText(e)));
  }, [current, onError]);

  function create() {
    if (!form) return;
    run(onError, () => api("/api/v1/enms/enpis", { method: "POST", json: { ...form, seuId: form.seuId || null } }), () => { setForm(null); load(); });
  }

  return (
    <>
      <Card title="EnPIs and their energy baselines (6.4, 6.5)" className="mb-6"
        actions={edit && <Button className="py-1" onClick={() => {
          const to = new Date(); to.setDate(to.getDate() - 7);
          const from = new Date(to); from.setDate(from.getDate() - 14);
          setForm({ name: "", seuId: "", kind: "Regression", baselineFrom: from.toISOString().slice(0, 10), baselineTo: to.toISOString().slice(0, 10) });
        }}><Plus size={14} /> EnPI</Button>}>
        <Table head={["EnPI", "Boundary", "Kind", "Baseline period", "Baseline", "R²", ""]} empty={(enpis ?? []).length === 0}>
          {(enpis ?? []).map((e) => (
            <tr key={e.enpiId} className={cx("cursor-pointer", current === e.enpiId && "bg-panel-2")} onClick={() => setSel(e.enpiId)}>
              <td className="px-3 py-2 font-medium">{e.name}</td>
              <td className="px-3 py-2">{e.seuName ?? "Whole site"}</td>
              <td className="px-3 py-2">{e.kind}</td>
              <td className="px-3 py-2 text-xs">{day(e.baselineFrom)} → {day(e.baselineTo)} ({e.baselineDays} d)</td>
              <td className="px-3 py-2 text-xs tabular-nums">{fmt(e.baselineKwh ?? 0)} kWh · {fmt(e.baselineParts ?? 0)} parts</td>
              <td className="px-3 py-2 tabular-nums">{e.r2 === null ? "—" : <span className={e.r2 < 0.75 ? "text-warn" : ""}>{e.r2.toFixed(2)}</span>}</td>
              <td className="px-3 py-2 text-right">{edit && <Button variant="ghost" className="py-1" aria-label="Delete" onClick={(ev) => { ev.stopPropagation(); run(onError, () => api(`/api/v1/enms/enpis/${e.enpiId}`, { method: "DELETE" }), () => { setSel(null); load(); }); }}><Trash2 size={14} /></Button>}</td>
            </tr>
          ))}
        </Table>
      </Card>
      {res && (
        <>
          <div className="mb-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
            <Stat label="Actual" value={`${fmt(res.actualKwh)} kWh`} sub={`${day(res.from)} to ${day(res.to)}`} />
            <Stat label="Expected (baseline, normalised)" value={`${fmt(res.expectedKwh)} kWh`} sub={res.model} />
            <Stat label="Savings" value={`${fmt(res.savingsKwh)} kWh`} sub={`${res.improvementPct > 0 ? "+" : ""}${res.improvementPct}% improvement`} />
            <Stat label="kWh per part" value={res.reportingIntensity ?? "—"} sub={`baseline ${res.baselineIntensity ?? "—"}`} />
          </div>
          {res.notes.length > 0 && <Card className="mb-6"><ul className="list-disc space-y-1 pl-5 text-sm">{res.notes.map((n) => <li key={n}>{n}</li>)}</ul></Card>}
          <div className="grid gap-4 lg:grid-cols-2">
            <Card title="Daily actual vs expected (kWh)">
              <BarChart bars={res.days.map((d) => ({ label: d.day.slice(5, 10), value: d.kwh, title: `${day(d.day)}: actual ${fmt(d.kwh)}, expected ${fmt(d.expected)}, ${fmt(d.parts)} parts`, tone: d.kwh <= d.expected ? "ok" as const : "warn" as const }))} unit=" kWh" />
            </Card>
            <Card title="CUSUM of savings (kWh) — a rising line means sustained improvement">
              <BarChart bars={res.days.map((d) => ({ label: d.day.slice(5, 10), value: Math.max(0, d.cusum), title: `${day(d.day)}: cumulative savings ${fmt(d.cusum)} kWh`, tone: d.cusum >= 0 ? "ok" as const : "bad" as const }))} unit=" kWh" />
            </Card>
          </div>
        </>
      )}
      <Modal open={!!form} title="New EnPI with energy baseline" onClose={() => setForm(null)}>
        {form && (
          <div className="space-y-3">
            <Field label="Name"><Input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} placeholder="Site electricity vs parts made" /></Field>
            <Field label="Boundary"><Select value={form.seuId} onChange={(e) => setForm({ ...form, seuId: e.target.value })}><option value="">Whole site (main meters)</option>{(seus ?? []).map((s) => <option key={s.seuId} value={s.seuId}>{s.name}</option>)}</Select></Field>
            <Field label="Kind" hint="Regression is the ISO 50006 model-based EnPI (needs ≥ 7 days). Ratio = kWh per part. Measured = raw kWh (not normalised).">
              <Select value={form.kind} onChange={(e) => setForm({ ...form, kind: e.target.value })}><option>Regression</option><option>Ratio</option><option>Measured</option></Select>
            </Field>
            <div className="grid grid-cols-2 gap-3">
              <Field label="Baseline from"><Input type="date" value={form.baselineFrom} onChange={(e) => setForm({ ...form, baselineFrom: e.target.value })} /></Field>
              <Field label="Baseline to"><Input type="date" value={form.baselineTo} onChange={(e) => setForm({ ...form, baselineTo: e.target.value })} /></Field>
            </div>
            <p className="text-xs text-muted">Choose a period that represents normal operation (ISO 50001 6.5 — usually 12 months once you have the data).</p>
            <div className="flex justify-end"><Button onClick={create}>Fit baseline</Button></div>
          </div>
        )}
      </Modal>
    </>
  );
}

// ---------- Objectives & actions ----------
function PlansTab({ edit, onError }: TabProps) {
  const [objectives, loadO] = useLoad<Objective[]>("/api/v1/enms/objectives", onError);
  const [actions, loadA] = useLoad<Action[]>("/api/v1/enms/actions", onError);
  const [enpis] = useLoad<Enpi[]>("/api/v1/enms/enpis", onError);
  const [obj, setObj] = useState<Partial<Objective> | null>(null);
  const [act, setAct] = useState<Partial<Action> | null>(null);

  function saveObj() {
    if (!obj) return;
    const body = { title: obj.title ?? "", enpiId: obj.enpiId || null, targetPct: Number(obj.targetPct ?? 0), targetDate: day(obj.targetDate), owner: obj.owner || null };
    run(onError, () => obj.objectiveId ? api(`/api/v1/enms/objectives/${obj.objectiveId}`, { method: "PUT", json: body }) : api("/api/v1/enms/objectives", { method: "POST", json: body }), () => { setObj(null); loadO(); });
  }
  function saveAct() {
    if (!act) return;
    const body = { objectiveId: act.objectiveId || null, title: act.title ?? "", owner: act.owner || null, dueDate: act.dueDate ? day(act.dueDate) : null, expectedKwhYear: act.expectedKwhYear ?? null, investment: act.investment ?? null, verification: act.verification || null, verifiedKwhYear: act.verifiedKwhYear ?? null, status: act.status ?? "Planned" };
    run(onError, () => act.actionId ? api(`/api/v1/enms/actions/${act.actionId}`, { method: "PUT", json: body }) : api("/api/v1/enms/actions", { method: "POST", json: body }), () => { setAct(null); loadA(); });
  }

  return (
    <>
      <Card title="Objectives and energy targets (6.2)" className="mb-6" actions={edit && <Button className="py-1" onClick={() => setObj({ targetPct: 5, targetDate: `${new Date().getFullYear() + 1}-06-30` })}><Plus size={14} /> Objective</Button>}>
        <Table head={["Objective", "Measured by", "Target", "By", "Owner", ""]} empty={(objectives ?? []).length === 0}>
          {(objectives ?? []).map((o) => (
            <tr key={o.objectiveId}>
              <td className="px-3 py-2 font-medium">{o.title}</td>
              <td className="px-3 py-2">{o.enpiName ?? "—"}</td>
              <td className="px-3 py-2 tabular-nums">{o.targetPct}%</td>
              <td className="px-3 py-2">{day(o.targetDate)}</td>
              <td className="px-3 py-2">{o.owner ?? "—"}</td>
              <td className="px-3 py-2 text-right">{edit && <span className="flex justify-end gap-1"><Button variant="ghost" className="py-1" onClick={() => setObj(o)}>Edit</Button><Button variant="ghost" className="py-1" aria-label="Delete" onClick={() => run(onError, () => api(`/api/v1/enms/objectives/${o.objectiveId}`, { method: "DELETE" }), () => { loadO(); loadA(); })}><Trash2 size={14} /></Button></span>}</td>
            </tr>
          ))}
        </Table>
      </Card>
      <Card title="Action plans and verified savings (6.2, 9.1)" actions={edit && <Button className="py-1" onClick={() => setAct({ status: "Planned" })}><Plus size={14} /> Action</Button>}>
        <Table head={["Action", "Objective", "Owner", "Due", "Expected kWh/yr", "Verified kWh/yr", "Status", ""]} empty={(actions ?? []).length === 0}>
          {(actions ?? []).map((a) => (
            <tr key={a.actionId}>
              <td className="px-3 py-2 font-medium">{a.title}{a.verification && <div className="text-xs text-muted">{a.verification}</div>}</td>
              <td className="px-3 py-2 text-xs">{a.objectiveTitle ?? "—"}</td>
              <td className="px-3 py-2">{a.owner ?? "—"}</td>
              <td className="px-3 py-2">{day(a.dueDate)}</td>
              <td className="px-3 py-2 tabular-nums">{a.expectedKwhYear === null ? "—" : fmt(a.expectedKwhYear)}</td>
              <td className="px-3 py-2 tabular-nums">{a.verifiedKwhYear === null ? "—" : fmt(a.verifiedKwhYear)}</td>
              <td className="px-3 py-2"><Badge tone={a.status === "Verified" ? "ok" : a.status === "Done" ? "brand" : a.status === "InProgress" ? "warn" : "muted"}>{a.status}</Badge></td>
              <td className="px-3 py-2 text-right">{edit && <span className="flex justify-end gap-1"><Button variant="ghost" className="py-1" onClick={() => setAct(a)}>Edit</Button><Button variant="ghost" className="py-1" aria-label="Delete" onClick={() => run(onError, () => api(`/api/v1/enms/actions/${a.actionId}`, { method: "DELETE" }), loadA)}><Trash2 size={14} /></Button></span>}</td>
            </tr>
          ))}
        </Table>
      </Card>
      <Modal open={!!obj} title={obj?.objectiveId ? "Edit objective" : "New objective"} onClose={() => setObj(null)}>
        {obj && (
          <div className="space-y-3">
            <Field label="Objective"><Input value={obj.title ?? ""} onChange={(e) => setObj({ ...obj, title: e.target.value })} placeholder="Cut site energy per part" /></Field>
            <Field label="Measured by EnPI"><Select value={obj.enpiId ?? ""} onChange={(e) => setObj({ ...obj, enpiId: e.target.value })}><option value="">—</option>{(enpis ?? []).map((e) => <option key={e.enpiId} value={e.enpiId}>{e.name}</option>)}</Select></Field>
            <div className="grid grid-cols-2 gap-3">
              <Field label="Target improvement %"><Input type="number" step="0.1" value={obj.targetPct ?? ""} onChange={(e) => setObj({ ...obj, targetPct: Number(e.target.value) })} /></Field>
              <Field label="By"><Input type="date" value={day(obj.targetDate)} onChange={(e) => setObj({ ...obj, targetDate: e.target.value })} /></Field>
            </div>
            <Field label="Owner"><Input value={obj.owner ?? ""} onChange={(e) => setObj({ ...obj, owner: e.target.value })} /></Field>
            <div className="flex justify-end"><Button onClick={saveObj}>Save</Button></div>
          </div>
        )}
      </Modal>
      <Modal open={!!act} title={act?.actionId ? "Edit action" : "New action"} onClose={() => setAct(null)}>
        {act && (
          <div className="space-y-3">
            <Field label="Action"><Input value={act.title ?? ""} onChange={(e) => setAct({ ...act, title: e.target.value })} placeholder="Fix compressed-air leaks" /></Field>
            <Field label="Objective"><Select value={act.objectiveId ?? ""} onChange={(e) => setAct({ ...act, objectiveId: e.target.value })}><option value="">—</option>{(objectives ?? []).map((o) => <option key={o.objectiveId} value={o.objectiveId}>{o.title}</option>)}</Select></Field>
            <div className="grid grid-cols-2 gap-3">
              <Field label="Owner"><Input value={act.owner ?? ""} onChange={(e) => setAct({ ...act, owner: e.target.value })} /></Field>
              <Field label="Due"><Input type="date" value={act.dueDate ? day(act.dueDate) : ""} onChange={(e) => setAct({ ...act, dueDate: e.target.value || null })} /></Field>
              <Field label="Expected kWh / year"><Input type="number" value={act.expectedKwhYear ?? ""} onChange={(e) => setAct({ ...act, expectedKwhYear: num(e.target.value) })} /></Field>
              <Field label="Investment"><Input type="number" value={act.investment ?? ""} onChange={(e) => setAct({ ...act, investment: num(e.target.value) })} /></Field>
            </div>
            <Field label="Status"><Select value={act.status ?? "Planned"} onChange={(e) => setAct({ ...act, status: e.target.value })}><option>Planned</option><option>InProgress</option><option>Done</option><option>Verified</option></Select></Field>
            <Field label="How savings are verified" hint="e.g. compressor kWh before/after, normalised to air demand (ISO 50015)."><Input value={act.verification ?? ""} onChange={(e) => setAct({ ...act, verification: e.target.value })} /></Field>
            <Field label="Verified kWh / year"><Input type="number" value={act.verifiedKwhYear ?? ""} onChange={(e) => setAct({ ...act, verifiedKwhYear: num(e.target.value) })} /></Field>
            <div className="flex justify-end"><Button onClick={saveAct}>Save</Button></div>
          </div>
        )}
      </Modal>
    </>
  );
}

// ---------- Emission factors ----------
type FactorForm = Omit<Factor, "factorId" | "co2e"> & { factorId?: string; co2e?: number };
const emptyFactor: FactorForm = { source: "electricity", name: "", scope: 2, method: "Market", region: "TW", year: new Date().getFullYear(), unit: "kWh", heatKcalPerUnit: null, co2KgPerTj: null, ch4KgPerTj: null, n2oKgPerTj: null, kgCo2ePerUnit: null, gwpSet: "AR5", reference: "", isActive: true, scope3Category: null };
const category = (source: string) => source.startsWith("ref_") ? "refrigerant" : source.startsWith("proc_") ? "process" : source.startsWith("s3_") ? "scope3" : "energy";
const FACTOR_VIEWS: [string, string][] = [["energy", "Energy"], ["refrigerant", "Refrigerants & SF₆"], ["process", "Process"], ["scope3", "Scope 3"], ["all", "All"]];

function FactorsTab({ edit, onError }: TabProps) {
  const [list, load] = useLoad<Factor[]>("/api/v1/enms/factors", onError);
  const [form, setForm] = useState<FactorForm | null>(null);
  const [view, setView] = useState("energy");
  const grid = (list ?? []).filter((f) => f.source === "electricity" && f.method === "Location");
  const regions = Array.from(new Set(grid.map((f) => f.region))).sort();
  const activeRegion = grid.find((f) => f.isActive)?.region ?? "TW";
  const shown = (list ?? []).filter((f) => view === "all" || category(f.source) === view);

  function chooseRegion(region: string) {
    run(onError, () => api("/api/v1/enms/factors/region", { method: "POST", json: { region } }), load);
  }

  function save() {
    if (!form) return;
    const { factorId, co2e: _c, ...body } = form;
    void _c;
    run(onError, () => factorId ? api(`/api/v1/enms/factors/${factorId}`, { method: "PUT", json: body }) : api("/api/v1/enms/factors", { method: "POST", json: body }), () => { setForm(null); load(); });
  }
  const setN = (k: keyof Factor) => (e: React.ChangeEvent<HTMLInputElement>) => form && setForm({ ...form, [k]: num(e.target.value) });

  return (
    <>
      <Card className="mb-4">
        <p className="text-sm">Every factor names its published source. Fuels are computed from the per-TJ factors and the net heat value: kg CO₂e/unit = heat (kcal) × 4.1868×10⁻⁹ TJ/kcal × (CO₂ + CH₄×GWP + N₂O×GWP). Carbon / ESG and the energy review use the factor for the report year; Settings values are only a fallback.</p>
      </Card>
      <Card className="mb-4" title="Grid electricity region (location-based Scope 2)">
        <div className="flex flex-wrap items-center gap-2 text-sm">
          <span>In use: <Badge tone="brand">{activeRegion}</Badge></span>
          {edit && regions.filter((r) => r !== activeRegion).map((r) => <Button key={r} variant="outline" className="py-1" onClick={() => chooseRegion(r)}>Use {r}</Button>)}
          <span className="text-xs text-muted">Published factors for TW (MOEA), UK (DESNZ 2025), US (EPA eGRID2023) and IN (CEA) are in the library; add your own country&apos;s factor with its source.</span>
        </div>
      </Card>
      <Card title="Emission factor library" actions={
        <span className="flex gap-2">
          <Select value={view} onChange={(e) => setView(e.target.value)} className="w-44 py-1" aria-label="Factor type">{FACTOR_VIEWS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</Select>
          {edit && <Button className="py-1" onClick={() => setForm({ ...emptyFactor })}><Plus size={14} /> Factor</Button>}
        </span>
      }>
        <Table head={["Source", "Name", "Scope", "Method", "Region · year", "kg CO₂e / unit", "Basis", "Reference", ""]} empty={shown.length === 0}>
          {shown.map((f) => (
            <tr key={f.factorId} className={f.isActive ? "" : "opacity-50"}>
              <td className="px-3 py-2">{f.source.replace("_", " ")}</td>
              <td className="px-3 py-2">{f.name}</td>
              <td className="px-3 py-2">{f.scope}{f.scope3Category ? ` · cat ${f.scope3Category}` : ""}</td>
              <td className="px-3 py-2">{f.method}</td>
              <td className="px-3 py-2 whitespace-nowrap">{f.region} · {f.year}</td>
              <td className="px-3 py-2 tabular-nums font-medium">{f.co2e.toFixed(4)} /{f.unit}</td>
              <td className="px-3 py-2 text-xs text-muted">{f.kgCo2ePerUnit !== null ? "published total" : `${fmt(f.heatKcalPerUnit ?? 0)} kcal · CO₂ ${fmt(f.co2KgPerTj ?? 0)} · CH₄ ${f.ch4KgPerTj} · N₂O ${f.n2oKgPerTj} kg/TJ · ${f.gwpSet}`}</td>
              <td className="max-w-xs px-3 py-2 text-xs text-muted">{f.reference}</td>
              <td className="px-3 py-2 text-right">{edit && <Button variant="ghost" className="py-1" onClick={() => setForm({ ...emptyFactor, ...f })}>Edit</Button>}</td>
            </tr>
          ))}
        </Table>
      </Card>
      <Modal open={!!form} title={form?.factorId ? "Edit emission factor" : "New emission factor"} onClose={() => setForm(null)} wide>
        {form && (
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Type"><Select value={category(form.source)} onChange={(e) => {
              const c = e.target.value;
              setForm(c === "energy" ? { ...form, source: "electricity", scope: 2, method: "Market", scope3Category: null }
                : c === "refrigerant" ? { ...form, source: "ref_", scope: 1, method: "Fugitive", unit: "kg", region: "GLOBAL", scope3Category: null }
                : c === "process" ? { ...form, source: "proc_", scope: 1, method: "Process", unit: "kg", scope3Category: null }
                : { ...form, source: "s3_", scope: 3, method: "Activity", scope3Category: 1 });
            }}><option value="energy">Energy (fuel, electricity, steam)</option><option value="refrigerant">Refrigerant / SF₆ (Scope 1 fugitive)</option><option value="process">Process emissions (Scope 1)</option><option value="scope3">Scope 3 activity</option></Select></Field>
            {category(form.source) === "energy"
              ? <Field label="Source"><Select value={form.source} onChange={(e) => setForm({ ...form, source: e.target.value })}>{SOURCES.filter(([s]) => s !== "green_power").map(([s, l]) => <option key={s} value={s}>{l}</option>)}</Select></Field>
              : <Field label="Code" hint="Lower-case letters, digits and _, after the prefix."><Input value={form.source} onChange={(e) => setForm({ ...form, source: e.target.value.toLowerCase() })} /></Field>}
            {form.scope === 3 && <Field label="Scope 3 category (GHG Protocol)"><Select value={form.scope3Category ?? 1} onChange={(e) => setForm({ ...form, scope3Category: Number(e.target.value) })}>{Array.from({ length: 15 }, (_, i) => <option key={i + 1} value={i + 1}>{i + 1}</option>)}</Select></Field>}
            <Field label="Name"><Input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} placeholder="Supplier-specific green tariff" /></Field>
            <Field label="Scope"><Select value={form.scope} onChange={(e) => setForm({ ...form, scope: Number(e.target.value) })}><option value={1}>1 — direct (fuel burned on site)</option><option value={2}>2 — purchased electricity / steam</option><option value={3}>3 — value chain</option></Select></Field>
            <Field label="Method"><Select value={form.method} onChange={(e) => setForm({ ...form, method: e.target.value })}><option>Combustion</option><option>Location</option><option>Market</option><option>Fugitive</option><option>Process</option><option>Activity</option></Select></Field>
            <Field label="Region"><Input value={form.region} onChange={(e) => setForm({ ...form, region: e.target.value })} /></Field>
            <Field label="Year"><Input type="number" value={form.year} onChange={(e) => setForm({ ...form, year: Number(e.target.value) })} /></Field>
            <Field label="Unit"><Input value={form.unit} onChange={(e) => setForm({ ...form, unit: e.target.value })} placeholder="kWh, Nm3, L, kg" /></Field>
            <Field label="GWP set"><Select value={form.gwpSet} onChange={(e) => setForm({ ...form, gwpSet: e.target.value })}><option value="AR5">IPCC AR5 (CH₄ 28, N₂O 265)</option><option value="AR6">IPCC AR6 (CH₄ 29.8, N₂O 273)</option></Select></Field>
            <Field label="Published total kg CO₂e / unit" hint="Electricity and supplier factors. Leave empty for fuels."><Input type="number" step="0.0001" value={form.kgCo2ePerUnit ?? ""} onChange={setN("kgCo2ePerUnit")} /></Field>
            <Field label="Net heat value (kcal / unit)"><Input type="number" value={form.heatKcalPerUnit ?? ""} onChange={setN("heatKcalPerUnit")} /></Field>
            <Field label="CO₂ kg/TJ"><Input type="number" value={form.co2KgPerTj ?? ""} onChange={setN("co2KgPerTj")} /></Field>
            <Field label="CH₄ kg/TJ"><Input type="number" step="0.1" value={form.ch4KgPerTj ?? ""} onChange={setN("ch4KgPerTj")} /></Field>
            <Field label="N₂O kg/TJ"><Input type="number" step="0.01" value={form.n2oKgPerTj ?? ""} onChange={setN("n2oKgPerTj")} /></Field>
            <Field label="Active"><Select value={form.isActive ? "1" : "0"} onChange={(e) => setForm({ ...form, isActive: e.target.value === "1" })}><option value="1">Active</option><option value="0">Retired</option></Select></Field>
            <div className="sm:col-span-2"><Field label="Reference (published source)"><Input value={form.reference} onChange={(e) => setForm({ ...form, reference: e.target.value })} /></Field></div>
            <div className="flex justify-end sm:col-span-2"><Button onClick={save}>Save</Button></div>
          </div>
        )}
      </Modal>
    </>
  );
}

// ---------- Energy data (bills, fuel deliveries, green power) ----------
function DataTab({ edit, onError }: TabProps) {
  const [list, load] = useLoad<Entry[]>("/api/v1/enms/entries", onError);
  const [form, setForm] = useState<{ source: string; periodFrom: string; periodTo: string; quantity: string; unit: string; cost: string; instrument: string; reference: string; note: string } | null>(null);

  function save() {
    if (!form) return;
    const body = { source: form.source, periodFrom: form.periodFrom, periodTo: form.periodTo, quantity: form.quantity === "" ? 0 : Number(form.quantity), unit: form.unit || null, cost: num(form.cost), instrument: form.instrument || null, reference: form.reference || null, note: form.note || null };
    run(onError, () => api("/api/v1/enms/entries", { method: "POST", json: body }), () => { setForm(null); load(); });
  }

  return (
    <>
      <Card className="mb-4"><p className="text-sm">Energy that no connected meter reads: fuel deliveries (forklift and generator diesel, boiler LPG), bills for unmetered buildings, purchased steam, and green power bought with T-REC certificates or a PPA (lowers market-based Scope 2). Each entry counts in reports for the part of its period that falls inside the report.</p></Card>
      <Card title="Energy data entries" actions={edit && <Button className="py-1" onClick={() => setForm({ source: "diesel", periodFrom: todayIso().slice(0, 8) + "01", periodTo: todayIso(), quantity: "", unit: "", cost: "", instrument: "", reference: "", note: "" })}><Plus size={14} /> Entry</Button>}>
        <Table head={["Source", "Period", "Quantity", "Cost", "Instrument", "Reference", ""]} empty={(list ?? []).length === 0}>
          {(list ?? []).map((e) => (
            <tr key={e.entryId}>
              <td className="px-3 py-2">{SOURCES.find(([s]) => s === e.source)?.[1] ?? e.source}</td>
              <td className="px-3 py-2 text-xs">{day(e.periodFrom)} → {day(e.periodTo)}</td>
              <td className="px-3 py-2 tabular-nums">{fmt(e.quantity)} {e.unit}</td>
              <td className="px-3 py-2 tabular-nums">{e.cost === null ? "—" : fmt(e.cost)}</td>
              <td className="px-3 py-2">{e.instrument ?? "—"}</td>
              <td className="px-3 py-2 text-xs">{e.reference ?? "—"}{e.note && <div className="text-muted">{e.note}</div>}</td>
              <td className="px-3 py-2 text-right">{edit && <Button variant="ghost" className="py-1" aria-label="Delete" onClick={() => run(onError, () => api(`/api/v1/enms/entries/${e.entryId}`, { method: "DELETE" }), load)}><Trash2 size={14} /></Button>}</td>
            </tr>
          ))}
        </Table>
      </Card>
      <Modal open={!!form} title="New energy data entry" onClose={() => setForm(null)}>
        {form && (
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="sm:col-span-2"><Field label="Source"><Select value={form.source} onChange={(e) => setForm({ ...form, source: e.target.value })}>{SOURCES.map(([s, l]) => <option key={s} value={s}>{l}</option>)}</Select></Field></div>
            <Field label="From"><Input type="date" value={form.periodFrom} onChange={(e) => setForm({ ...form, periodFrom: e.target.value })} /></Field>
            <Field label="To"><Input type="date" value={form.periodTo} onChange={(e) => setForm({ ...form, periodTo: e.target.value })} /></Field>
            <Field label="Quantity"><Input type="number" step="0.001" min="0.001" required value={form.quantity} onChange={(e) => setForm({ ...form, quantity: e.target.value })} /></Field>
            <Field label="Unit" hint="Empty = the factor's unit (L, Nm3, kWh)."><Input value={form.unit} onChange={(e) => setForm({ ...form, unit: e.target.value })} /></Field>
            <Field label="Cost"><Input type="number" value={form.cost} onChange={(e) => setForm({ ...form, cost: e.target.value })} /></Field>
            {form.source === "green_power" && <Field label="Instrument"><Select value={form.instrument} onChange={(e) => setForm({ ...form, instrument: e.target.value })}><option value="">—</option><option>T-REC</option><option>PPA</option><option>Self</option></Select></Field>}
            <Field label="Bill / certificate no."><Input value={form.reference} onChange={(e) => setForm({ ...form, reference: e.target.value })} /></Field>
            <div className="sm:col-span-2"><Field label="Note"><Input value={form.note} onChange={(e) => setForm({ ...form, note: e.target.value })} /></Field></div>
            <div className="flex justify-end sm:col-span-2"><Button onClick={save}>Save</Button></div>
          </div>
        )}
      </Modal>
    </>
  );
}

// ---------- Documented information ----------
function DocumentsTab({ edit, onError }: TabProps) {
  const [list, load] = useLoad<Doc[]>("/api/v1/enms/documents", onError);
  const [form, setForm] = useState<Partial<Doc> | null>(null);
  const [view, setView] = useState<Doc | null>(null);

  function save() {
    if (!form) return;
    const body = { kind: form.kind ?? "Policy", title: form.title ?? "", body: form.body || null, owner: form.owner || null, approvedBy: form.approvedBy || null, approvedOn: form.approvedOn ? day(form.approvedOn) : null, reviewDue: form.reviewDue ? day(form.reviewDue) : null };
    run(onError, () => form.documentId ? api(`/api/v1/enms/documents/${form.documentId}`, { method: "PUT", json: body }) : api("/api/v1/enms/documents", { method: "POST", json: body }), () => { setForm(null); load(); });
  }

  return (
    <>
      <Card title="Documented information (7.5)" actions={edit && <Button className="py-1" onClick={() => setForm({ kind: "Policy" })}><Plus size={14} /> Document</Button>}>
        <Table head={["Clause", "Title", "Owner", "Approved", "Review due", ""]} empty={(list ?? []).length === 0}>
          {(list ?? []).map((d) => (
            <tr key={d.documentId}>
              <td className="px-3 py-2 text-xs">{DOC_KINDS.find(([k]) => k === d.kind)?.[1] ?? d.kind}</td>
              <td className="px-3 py-2"><button className="font-medium text-brand hover:underline" onClick={() => setView(d)}>{d.title}</button></td>
              <td className="px-3 py-2">{d.owner ?? "—"}</td>
              <td className="px-3 py-2 text-xs">{d.approvedOn ? `${d.approvedBy ?? ""} ${day(d.approvedOn)}` : <Badge tone="warn">not approved</Badge>}</td>
              <td className="px-3 py-2 text-xs">{d.reviewDue ? <span className={d.reviewDue < todayIso() ? "text-bad" : ""}>{day(d.reviewDue)}</span> : "—"}</td>
              <td className="px-3 py-2 text-right">{edit && <span className="flex justify-end gap-1"><Button variant="ghost" className="py-1" onClick={() => setForm(d)}>Edit</Button><Button variant="ghost" className="py-1" aria-label="Delete" onClick={() => run(onError, () => api(`/api/v1/enms/documents/${d.documentId}`, { method: "DELETE" }), load)}><Trash2 size={14} /></Button></span>}</td>
            </tr>
          ))}
        </Table>
      </Card>
      <Modal open={!!view} title={view?.title ?? ""} onClose={() => setView(null)} wide>
        {view && <div className="whitespace-pre-wrap text-sm">{view.body ?? "—"}</div>}
      </Modal>
      <Modal open={!!form} title={form?.documentId ? "Edit document" : "New document"} onClose={() => setForm(null)} wide>
        {form && (
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Kind"><Select value={form.kind} onChange={(e) => setForm({ ...form, kind: e.target.value })}>{DOC_KINDS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</Select></Field>
            <Field label="Title"><Input value={form.title ?? ""} onChange={(e) => setForm({ ...form, title: e.target.value })} /></Field>
            <div className="sm:col-span-2"><Field label="Text"><textarea className="h-48 w-full rounded-lg border border-line bg-panel px-3 py-2 text-sm" value={form.body ?? ""} onChange={(e) => setForm({ ...form, body: e.target.value })} /></Field></div>
            <Field label="Owner"><Input value={form.owner ?? ""} onChange={(e) => setForm({ ...form, owner: e.target.value })} /></Field>
            <Field label="Approved by"><Input value={form.approvedBy ?? ""} onChange={(e) => setForm({ ...form, approvedBy: e.target.value })} /></Field>
            <Field label="Approved on"><Input type="date" value={form.approvedOn ? day(form.approvedOn) : ""} onChange={(e) => setForm({ ...form, approvedOn: e.target.value || null })} /></Field>
            <Field label="Review due"><Input type="date" value={form.reviewDue ? day(form.reviewDue) : ""} onChange={(e) => setForm({ ...form, reviewDue: e.target.value || null })} /></Field>
            <div className="flex justify-end sm:col-span-2"><Button onClick={save}>Save</Button></div>
          </div>
        )}
      </Modal>
    </>
  );
}

// ---------- Audits, management reviews, nonconformities ----------
function AuditsTab({ edit, onError }: TabProps) {
  const [list, load] = useLoad<Finding[]>("/api/v1/enms/findings", onError);
  const [form, setForm] = useState<Partial<Finding> | null>(null);

  function save() {
    if (!form) return;
    const body = { kind: form.kind ?? "Audit", clause: form.clause || null, title: form.title ?? "", detail: form.detail || null, rootCause: form.rootCause || null, correctiveAction: form.correctiveAction || null, owner: form.owner || null, heldOn: day(form.heldOn) || todayIso(), dueDate: form.dueDate ? day(form.dueDate) : null, closedOn: form.closedOn ? day(form.closedOn) : null };
    run(onError, () => form.findingId ? api(`/api/v1/enms/findings/${form.findingId}`, { method: "PUT", json: body }) : api("/api/v1/enms/findings", { method: "POST", json: body }), () => { setForm(null); load(); });
  }
  const label = (k: string) => (k === "ManagementReview" ? "Management review (9.3)" : k === "Audit" ? "Internal audit (9.2)" : "Nonconformity (10.1)");

  return (
    <>
      <Card title="Internal audits, management reviews and nonconformities" actions={edit && <Button className="py-1" onClick={() => setForm({ kind: "Nonconformity", heldOn: todayIso() })}><Plus size={14} /> Record</Button>}>
        <Table head={["Type", "Clause", "Title", "Date", "Owner", "Due", "Status", ""]} empty={(list ?? []).length === 0}>
          {(list ?? []).map((f) => (
            <tr key={f.findingId}>
              <td className="px-3 py-2 text-xs">{label(f.kind)}</td>
              <td className="px-3 py-2 font-mono text-xs">{f.clause ?? "—"}</td>
              <td className="px-3 py-2">{f.title}{f.correctiveAction && <div className="text-xs text-muted">Action: {f.correctiveAction}</div>}</td>
              <td className="px-3 py-2 text-xs">{day(f.heldOn)}</td>
              <td className="px-3 py-2">{f.owner ?? "—"}</td>
              <td className="px-3 py-2 text-xs">{day(f.dueDate)}</td>
              <td className="px-3 py-2">{f.kind !== "Nonconformity" ? <Badge tone="ok">held</Badge> : f.closedOn ? <Badge tone="ok">closed {day(f.closedOn)}</Badge> : <Badge tone={f.dueDate && f.dueDate.slice(0, 10) < todayIso() ? "bad" : "warn"}>open</Badge>}</td>
              <td className="px-3 py-2 text-right">{edit && <span className="flex justify-end gap-1"><Button variant="ghost" className="py-1" onClick={() => setForm(f)}>Edit</Button><Button variant="ghost" className="py-1" aria-label="Delete" onClick={() => run(onError, () => api(`/api/v1/enms/findings/${f.findingId}`, { method: "DELETE" }), load)}><Trash2 size={14} /></Button></span>}</td>
            </tr>
          ))}
        </Table>
      </Card>
      <Modal open={!!form} title={form?.findingId ? "Edit record" : "New record"} onClose={() => setForm(null)} wide>
        {form && (
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Type"><Select value={form.kind} onChange={(e) => setForm({ ...form, kind: e.target.value })}><option value="Audit">Internal audit (9.2)</option><option value="ManagementReview">Management review (9.3)</option><option value="Nonconformity">Nonconformity (10.1)</option></Select></Field>
            <Field label="Clause"><Input value={form.clause ?? ""} onChange={(e) => setForm({ ...form, clause: e.target.value })} placeholder="9.1" /></Field>
            <div className="sm:col-span-2"><Field label="Title"><Input value={form.title ?? ""} onChange={(e) => setForm({ ...form, title: e.target.value })} /></Field></div>
            <div className="sm:col-span-2"><Field label="Detail / minutes"><textarea className="h-28 w-full rounded-lg border border-line bg-panel px-3 py-2 text-sm" value={form.detail ?? ""} onChange={(e) => setForm({ ...form, detail: e.target.value })} /></Field></div>
            {form.kind === "Nonconformity" && (
              <>
                <Field label="Root cause"><Input value={form.rootCause ?? ""} onChange={(e) => setForm({ ...form, rootCause: e.target.value })} /></Field>
                <Field label="Corrective action"><Input value={form.correctiveAction ?? ""} onChange={(e) => setForm({ ...form, correctiveAction: e.target.value })} /></Field>
              </>
            )}
            <Field label="Owner"><Input value={form.owner ?? ""} onChange={(e) => setForm({ ...form, owner: e.target.value })} /></Field>
            <Field label="Date"><Input type="date" value={day(form.heldOn)} onChange={(e) => setForm({ ...form, heldOn: e.target.value })} /></Field>
            <Field label="Due"><Input type="date" value={form.dueDate ? day(form.dueDate) : ""} onChange={(e) => setForm({ ...form, dueDate: e.target.value || null })} /></Field>
            {form.kind === "Nonconformity" && <Field label="Closed on"><Input type="date" value={form.closedOn ? day(form.closedOn) : ""} onChange={(e) => setForm({ ...form, closedOn: e.target.value || null })} /></Field>}
            <div className="flex justify-end sm:col-span-2"><Button onClick={save}>Save</Button></div>
          </div>
        )}
      </Modal>
    </>
  );
}
