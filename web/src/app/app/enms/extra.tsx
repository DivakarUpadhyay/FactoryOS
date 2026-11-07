"use client";

import { useCallback, useEffect, useState } from "react";
import { CheckCircle2, CircleAlert, CircleX, Plus, RefreshCw, Trash2 } from "lucide-react";
import { api } from "@/lib/api";
import { BarChart, fmt } from "@/components/charts";
import { Badge, Button, Card, Field, Input, Modal, Select, Stat, Table, errorText } from "@/components/ui";

type TabProps = { edit?: boolean; onError: (e: string | null) => void };

function useLoad<T>(url: string | null, onError: (e: string | null) => void): [T | null, () => void] {
  const [data, setData] = useState<T | null>(null);
  const load = useCallback(() => {
    if (!url) { setData(null); return; }
    api<T>(url).then(setData).catch((e) => onError(errorText(e)));
  }, [url, onError]);
  useEffect(load, [load]);
  return [data, load];
}

async function run(onError: (e: string | null) => void, fn: () => Promise<unknown>, after: () => void) {
  onError(null);
  try { await fn(); after(); } catch (e) { onError(errorText(e)); }
}

const day = (s: string | null | undefined) => (s ? s.slice(0, 10) : "—");
const isoDay = (offset = 0) => { const d = new Date(); d.setDate(d.getDate() + offset); return d.toISOString().slice(0, 10); };
const num = (v: string) => (v === "" ? null : Number(v));
const stateTone = { ok: "ok", due: "warn", overdue: "bad", failed: "bad", never: "bad", untracked: "muted", virtual: "muted" } as const;
const stateLabel: Record<string, string> = { ok: "calibrated", due: "due within 30 days", overdue: "overdue", failed: "failed", never: "no record", untracked: "no interval", virtual: "virtual" };

// ---------- Meters & calibration (ISO 50001 9.1) ----------
type Meter = {
  meterId: string; name: string; assetId: string | null; assetName: string | null; measures: string; kind: string; serialNo: string | null; make: string | null;
  accuracyClass: string | null; uncertaintyPct: number | null; intervalMonths: number | null; lastCalibratedOn: string | null; location: string | null; isActive: boolean;
  lastResult: string | null;
};
type MeterRow = { meter: Meter; nextDueOn: string | null; state: keyof typeof stateTone };
type Calibration = { calibrationId: string; calibratedOn: string; result: string; asFoundErrorPct: number | null; asLeftErrorPct: number | null; lab: string | null; certificateNo: string | null; note: string | null };
type Asset = { assetId: string; name: string; code: string };

const MEASURES = ["electricity", "gas", "water", "air", "steam", "fuel", "temperature", "production", "other"];

export function MetersTab({ edit, onError }: TabProps) {
  const [rows, load] = useLoad<MeterRow[]>("/api/v1/enms/meters", onError);
  const [assets] = useLoad<Asset[]>("/api/v1/plant/floor", onError);
  const [form, setForm] = useState<Partial<Meter> | null>(null);
  const [sel, setSel] = useState<string | null>(null);
  const [cals, loadCals] = useLoad<Calibration[]>(sel ? `/api/v1/enms/meters/${sel}/calibrations` : null, onError);
  const [cal, setCal] = useState<{ calibratedOn: string; result: string; asFoundErrorPct: string; asLeftErrorPct: string; lab: string; certificateNo: string; note: string } | null>(null);
  const list = rows ?? [];
  const bad = list.filter((r) => ["overdue", "failed", "never"].includes(r.state)).length;

  function save() {
    if (!form) return;
    const body = {
      name: form.name ?? "", assetId: form.assetId || null, measures: form.measures ?? "electricity", kind: form.kind ?? "Sub", serialNo: form.serialNo ?? null, make: form.make ?? null,
      accuracyClass: form.accuracyClass ?? null, uncertaintyPct: form.uncertaintyPct ?? null, intervalMonths: form.intervalMonths ?? null, location: form.location ?? null, isActive: form.isActive ?? true,
    };
    run(onError, () => form.meterId ? api(`/api/v1/enms/meters/${form.meterId}`, { method: "PUT", json: body }) : api("/api/v1/enms/meters", { method: "POST", json: body }), () => { setForm(null); load(); });
  }

  function saveCal() {
    if (!cal || !sel) return;
    run(onError, () => api(`/api/v1/enms/meters/${sel}/calibrations`, { method: "POST", json: { ...cal, asFoundErrorPct: num(cal.asFoundErrorPct), asLeftErrorPct: num(cal.asLeftErrorPct) } }),
      () => { setCal(null); load(); loadCals(); });
  }

  return (
    <>
      <div className="mb-6 grid gap-4 sm:grid-cols-3">
        <Stat label="Meters in the register" value={list.length} sub={`${list.filter((r) => r.meter.kind === "Main").length} main · ${list.filter((r) => r.meter.kind === "Sub").length} sub-meters`} />
        <Stat label="Valid calibration" value={list.filter((r) => r.state === "ok" || r.state === "due").length} sub={`${list.filter((r) => r.state === "due").length} due within 30 days`} />
        <Stat label="Need attention" value={bad} sub="overdue, failed or never calibrated — ISO 50001 9.1" />
      </div>
      <Card title="Meter register (accuracy, calibration interval, records)" className="mb-6" actions={edit && (
        <span className="flex gap-2">
          <Button variant="outline" className="py-1" onClick={() => run(onError, () => api("/api/v1/enms/meters/register-connected", { method: "POST" }), load)}><RefreshCw size={14} /> Add connected meters</Button>
          <Button className="py-1" onClick={() => setForm({ kind: "Sub", measures: "electricity", intervalMonths: 24, isActive: true })}><Plus size={14} /> Meter</Button>
        </span>
      )}>
        <Table head={["Meter", "Measures", "Kind", "Accuracy", "Interval", "Last calibrated", "Next due", "State", ""]} empty={list.length === 0}>
          {list.map((r) => (
            <tr key={r.meter.meterId} className={sel === r.meter.meterId ? "bg-panel-2" : "cursor-pointer"} onClick={() => setSel(r.meter.meterId)}>
              <td className="px-3 py-2 font-medium">{r.meter.name}<div className="text-xs text-muted">{r.meter.assetName ?? r.meter.location ?? ""}{r.meter.serialNo ? ` · S/N ${r.meter.serialNo}` : ""}</div></td>
              <td className="px-3 py-2">{r.meter.measures}</td>
              <td className="px-3 py-2">{r.meter.kind}</td>
              <td className="px-3 py-2 text-xs">{r.meter.accuracyClass ?? "—"}{r.meter.uncertaintyPct !== null ? ` · ±${r.meter.uncertaintyPct} %` : ""}</td>
              <td className="px-3 py-2 tabular-nums">{r.meter.intervalMonths ? `${r.meter.intervalMonths} mo` : "—"}</td>
              <td className="px-3 py-2">{day(r.meter.lastCalibratedOn)}{r.meter.lastResult ? <span className="text-xs text-muted"> · {r.meter.lastResult}</span> : null}</td>
              <td className="px-3 py-2">{day(r.nextDueOn)}</td>
              <td className="px-3 py-2"><Badge tone={stateTone[r.state]}>{stateLabel[r.state]}</Badge></td>
              <td className="px-3 py-2 text-right">{edit && (
                <span className="flex justify-end gap-1">
                  <Button variant="ghost" className="py-1" onClick={(e) => { e.stopPropagation(); setForm(r.meter); }}>Edit</Button>
                  <Button variant="ghost" className="py-1" aria-label="Delete" onClick={(e) => { e.stopPropagation(); run(onError, () => api(`/api/v1/enms/meters/${r.meter.meterId}`, { method: "DELETE" }), () => { setSel(null); load(); }); }}><Trash2 size={14} /></Button>
                </span>
              )}</td>
            </tr>
          ))}
        </Table>
      </Card>
      {sel && (
        <Card title={`Calibration records — ${list.find((r) => r.meter.meterId === sel)?.meter.name ?? ""}`} actions={edit && <Button className="py-1" onClick={() => setCal({ calibratedOn: isoDay(), result: "Pass", asFoundErrorPct: "", asLeftErrorPct: "", lab: "", certificateNo: "", note: "" })}><Plus size={14} /> Record</Button>}>
          <Table head={["Date", "Result", "As found", "As left", "Laboratory", "Certificate", "Note"]} empty={(cals ?? []).length === 0}>
            {(cals ?? []).map((c) => (
              <tr key={c.calibrationId}>
                <td className="px-3 py-2">{day(c.calibratedOn)}</td>
                <td className="px-3 py-2"><Badge tone={c.result === "Fail" ? "bad" : c.result === "Adjusted" ? "warn" : "ok"}>{c.result}</Badge></td>
                <td className="px-3 py-2 tabular-nums">{c.asFoundErrorPct !== null ? `${c.asFoundErrorPct} %` : "—"}</td>
                <td className="px-3 py-2 tabular-nums">{c.asLeftErrorPct !== null ? `${c.asLeftErrorPct} %` : "—"}</td>
                <td className="px-3 py-2">{c.lab ?? "—"}</td>
                <td className="px-3 py-2 font-mono text-xs">{c.certificateNo ?? "—"}</td>
                <td className="px-3 py-2 text-xs text-muted">{c.note ?? ""}</td>
              </tr>
            ))}
          </Table>
        </Card>
      )}
      <Modal open={!!form} title={form?.meterId ? "Edit meter" : "New meter"} onClose={() => setForm(null)} wide>
        {form && (
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Name"><Input value={form.name ?? ""} onChange={(e) => setForm({ ...form, name: e.target.value })} placeholder="Main incomer MV meter" /></Field>
            <Field label="Asset (optional)"><Select value={form.assetId ?? ""} onChange={(e) => setForm({ ...form, assetId: e.target.value || null })}><option value="">—</option>{(assets ?? []).map((a) => <option key={a.assetId} value={a.assetId}>{a.name} ({a.code})</option>)}</Select></Field>
            <Field label="Measures"><Select value={form.measures ?? "electricity"} onChange={(e) => setForm({ ...form, measures: e.target.value })}>{MEASURES.map((m) => <option key={m}>{m}</option>)}</Select></Field>
            <Field label="Kind"><Select value={form.kind ?? "Sub"} onChange={(e) => setForm({ ...form, kind: e.target.value })}><option>Main</option><option>Sub</option><option>Portable</option><option>Virtual</option></Select></Field>
            <Field label="Make / model"><Input value={form.make ?? ""} onChange={(e) => setForm({ ...form, make: e.target.value })} /></Field>
            <Field label="Serial number"><Input value={form.serialNo ?? ""} onChange={(e) => setForm({ ...form, serialNo: e.target.value })} /></Field>
            <Field label="Accuracy class" hint="e.g. IEC 62053-22 class 0.5S, MID class B"><Input value={form.accuracyClass ?? ""} onChange={(e) => setForm({ ...form, accuracyClass: e.target.value })} /></Field>
            <Field label="Uncertainty ± % of reading" hint="Expanded (95 %); used in savings uncertainty."><Input type="number" step="0.01" value={form.uncertaintyPct ?? ""} onChange={(e) => setForm({ ...form, uncertaintyPct: num(e.target.value) })} /></Field>
            <Field label="Calibration interval (months)"><Input type="number" value={form.intervalMonths ?? ""} onChange={(e) => setForm({ ...form, intervalMonths: num(e.target.value) })} /></Field>
            <Field label="Location"><Input value={form.location ?? ""} onChange={(e) => setForm({ ...form, location: e.target.value })} /></Field>
            <div className="flex justify-end sm:col-span-2"><Button onClick={save}>Save</Button></div>
          </div>
        )}
      </Modal>
      <Modal open={!!cal} title="Calibration or verification record" onClose={() => setCal(null)}>
        {cal && (
          <div className="space-y-3">
            <div className="grid gap-3 sm:grid-cols-2">
              <Field label="Date"><Input type="date" value={cal.calibratedOn} onChange={(e) => setCal({ ...cal, calibratedOn: e.target.value })} /></Field>
              <Field label="Result"><Select value={cal.result} onChange={(e) => setCal({ ...cal, result: e.target.value })}><option>Pass</option><option>Adjusted</option><option>Fail</option></Select></Field>
              <Field label="Error as found (%)"><Input type="number" step="0.01" value={cal.asFoundErrorPct} onChange={(e) => setCal({ ...cal, asFoundErrorPct: e.target.value })} /></Field>
              <Field label="Error as left (%)"><Input type="number" step="0.01" value={cal.asLeftErrorPct} onChange={(e) => setCal({ ...cal, asLeftErrorPct: e.target.value })} /></Field>
            </div>
            <Field label="Laboratory" hint="Accredited (ISO/IEC 17025) or in-house check against a reference meter."><Input value={cal.lab} onChange={(e) => setCal({ ...cal, lab: e.target.value })} /></Field>
            <Field label="Certificate number"><Input value={cal.certificateNo} onChange={(e) => setCal({ ...cal, certificateNo: e.target.value })} /></Field>
            <Field label="Note"><Input value={cal.note} onChange={(e) => setCal({ ...cal, note: e.target.value })} /></Field>
            <div className="flex justify-end"><Button onClick={saveCal}>Save</Button></div>
          </div>
        )}
      </Modal>
    </>
  );
}

// ---------- Savings M&V (ISO 50015, IPMVP, ASHRAE Guideline 14) ----------
type Plan = {
  planId: string; name: string; actionId: string | null; actionTitle: string | null; enpiId: string | null; enpiName: string | null; mvOption: string; boundary: string | null;
  reportingFrom: string; reportingTo: string; nonRoutineKwh: number; staticFactors: string | null; confidencePct: number; manualSavingsKwh: number | null; manualUncertaintyPct: number | null;
};
type Check = { name: string; state: "ok" | "warn" | "bad"; detail: string };
type Result = {
  planId: string; name: string; mvOption: string; method: string; model: string | null;
  fit: { n: number; parameters: number; meanKwh: number; cvRmsePct: number; nmbePct: number; autocorrelation: number; effectiveN: number } | null;
  savings: { reportingDays: number; adjustedBaselineKwh: number; actualKwh: number; savingsKwh: number; savingsFraction: number; confidencePct: number; tValue: number; modelUncertaintyKwh: number; meterUncertaintyKwh: number; combinedUncertaintyKwh: number; fractionalUncertaintyPct: number | null; fsu68Pct: number | null } | null;
  annualisedKwh: number | null; annualisedUncertaintyKwh: number | null; meterUncertaintyPct: number; checks: Check[];
  days: { day: string; kwh: number; expected: number; cusum: number }[]; notes: string[];
};
type Enpi = { enpiId: string; name: string; seuName: string | null };
type Action = { actionId: string; title: string; status: string };

const OPTIONS: [string, string][] = [
  ["C", "C — whole facility (main meters)"], ["B", "B — retrofit isolation, all parameters measured (SEU)"],
  ["A", "A — retrofit isolation, key parameter measured"], ["D", "D — calibrated simulation"],
];

function CheckIcon({ state }: { state: string }) {
  if (state === "ok") return <CheckCircle2 size={16} className="text-ok" />;
  if (state === "warn") return <CircleAlert size={16} className="text-warn" />;
  return <CircleX size={16} className="text-bad" />;
}

export function MvTab({ edit, onError }: TabProps) {
  const [plans, load] = useLoad<Plan[]>("/api/v1/enms/mv", onError);
  const [enpis] = useLoad<Enpi[]>("/api/v1/enms/enpis", onError);
  const [actions, loadActions] = useLoad<Action[]>("/api/v1/enms/actions", onError);
  const [sel, setSel] = useState<string | null>(null);
  const current = sel ?? plans?.[0]?.planId ?? null;
  const [res, loadRes] = useLoad<Result>(current ? `/api/v1/enms/mv/${current}/result` : null, onError);
  const [form, setForm] = useState<(Omit<Partial<Plan>, "manualSavingsKwh" | "manualUncertaintyPct" | "nonRoutineKwh"> & { manualSavingsKwh?: string; manualUncertaintyPct?: string; nonRoutineKwh?: string }) | null>(null);

  function save() {
    if (!form) return;
    const body = {
      name: form.name ?? "", actionId: form.actionId || null, enpiId: form.enpiId || null, mvOption: form.mvOption ?? "C", boundary: form.boundary ?? null,
      reportingFrom: form.reportingFrom, reportingTo: form.reportingTo, nonRoutineKwh: Number(form.nonRoutineKwh || 0), staticFactors: form.staticFactors ?? null,
      confidencePct: Number(form.confidencePct ?? 90), manualSavingsKwh: num(form.manualSavingsKwh ?? ""), manualUncertaintyPct: num(form.manualUncertaintyPct ?? ""),
    };
    run(onError, () => form.planId ? api(`/api/v1/enms/mv/${form.planId}`, { method: "PUT", json: body }) : api("/api/v1/enms/mv", { method: "POST", json: body }),
      () => { setForm(null); load(); loadRes(); });
  }

  const s = res?.savings;
  const plan = (plans ?? []).find((p) => p.planId === current);
  return (
    <>
      <Card className="mb-4">
        <p className="text-sm">Measurement and verification of savings (ISO 50015, IPMVP). Options B and C use an EnPI&apos;s baseline model and the metered data: the model is checked with ASHRAE Guideline 14 statistics (CV(RMSE), NMBE, residual autocorrelation) and the savings carry a stated uncertainty, with the meter accuracy from the register added in quadrature. Verified savings can be written to the action.</p>
      </Card>
      <Card title="M&V plans" className="mb-6" actions={edit && <Button className="py-1" onClick={() => setForm({ mvOption: "C", reportingFrom: isoDay(-7), reportingTo: isoDay(0), confidencePct: 90, nonRoutineKwh: "0" })}><Plus size={14} /> Plan</Button>}>
        <Table head={["Plan", "Option", "Model / boundary", "Reporting period", "Action", ""]} empty={(plans ?? []).length === 0}>
          {(plans ?? []).map((p) => (
            <tr key={p.planId} className={current === p.planId ? "bg-panel-2" : "cursor-pointer"} onClick={() => setSel(p.planId)}>
              <td className="px-3 py-2 font-medium">{p.name}</td>
              <td className="px-3 py-2"><Badge tone="brand">IPMVP {p.mvOption}</Badge></td>
              <td className="px-3 py-2 text-xs">{p.enpiName ?? p.boundary ?? "—"}</td>
              <td className="px-3 py-2 text-xs">{day(p.reportingFrom)} → {day(p.reportingTo)}</td>
              <td className="px-3 py-2 text-xs">{p.actionTitle ?? "—"}</td>
              <td className="px-3 py-2 text-right">{edit && (
                <span className="flex justify-end gap-1">
                  <Button variant="ghost" className="py-1" onClick={(e) => { e.stopPropagation(); setForm({ ...p, manualSavingsKwh: p.manualSavingsKwh?.toString() ?? "", manualUncertaintyPct: p.manualUncertaintyPct?.toString() ?? "", nonRoutineKwh: String(p.nonRoutineKwh), reportingFrom: day(p.reportingFrom), reportingTo: day(p.reportingTo) }); }}>Edit</Button>
                  <Button variant="ghost" className="py-1" aria-label="Delete" onClick={(e) => { e.stopPropagation(); run(onError, () => api(`/api/v1/enms/mv/${p.planId}`, { method: "DELETE" }), () => { setSel(null); load(); }); }}><Trash2 size={14} /></Button>
                </span>
              )}</td>
            </tr>
          ))}
        </Table>
      </Card>
      {res && (
        <>
          <div className="mb-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
            <Stat label="Savings in the period" value={s ? `${fmt(s.savingsKwh)} kWh` : "—"} sub={s ? `${s.savingsFraction}% of the adjusted baseline · ${s.reportingDays} days` : res.method} />
            <Stat label={`Uncertainty (${s?.confidencePct ?? plan?.confidencePct ?? 90} % confidence)`} value={s ? `± ${fmt(s.combinedUncertaintyKwh)} kWh` : "—"} sub={s ? `model ±${fmt(s.modelUncertaintyKwh)} · meters ±${fmt(s.meterUncertaintyKwh)} (t = ${s.tValue})` : ""} />
            <Stat label="Fractional savings uncertainty" value={s?.fsu68Pct !== null && s?.fsu68Pct !== undefined ? `${s.fsu68Pct} %` : "—"} sub="at 68 % — ASHRAE 14 asks for < 50 %" />
            <Stat label="Annualised" value={res.annualisedKwh !== null ? `${fmt(res.annualisedKwh)} kWh/yr` : "—"} sub={res.annualisedUncertaintyKwh !== null ? `± ${fmt(res.annualisedUncertaintyKwh)} kWh/yr (extrapolated)` : ""} />
          </div>
          <div className="mb-6 grid gap-4 lg:grid-cols-2">
            <Card title={res.method} actions={edit && plan?.actionId && <Button className="py-1" onClick={() => run(onError, () => api(`/api/v1/enms/mv/${current}/apply`, { method: "POST" }), () => { loadActions(); loadRes(); })}>Record as verified savings</Button>}>
              <ul className="space-y-2 text-sm">
                {res.checks.map((c) => <li key={c.name} className="flex gap-2"><CheckIcon state={c.state} /><span><b>{c.name}.</b> {c.detail}</span></li>)}
              </ul>
              {res.model && <p className="mt-3 text-xs text-muted">Baseline model: {res.model}{res.fit ? ` · ${res.fit.n} baseline days, mean ${fmt(res.fit.meanKwh)} kWh/day` : ""}</p>}
              {plan?.actionId && <p className="mt-1 text-xs text-muted">Action: {(actions ?? []).find((a) => a.actionId === plan.actionId)?.title} ({(actions ?? []).find((a) => a.actionId === plan.actionId)?.status})</p>}
            </Card>
            <Card title="Reporting period: actual vs adjusted baseline (kWh/day)">
              {res.days.length === 0 ? <p className="text-sm text-muted">Options A and D are not computed from the meters.</p> : (
                <BarChart bars={res.days.map((d) => ({ label: d.day.slice(5, 10), value: d.kwh, title: `${day(d.day)}: actual ${fmt(d.kwh)}, expected ${fmt(d.expected)}`, tone: d.kwh <= d.expected ? "ok" as const : "warn" as const }))} unit=" kWh" />
              )}
            </Card>
          </div>
          {res.notes.length > 0 && <Card className="mb-6"><ul className="list-disc space-y-1 pl-5 text-sm">{res.notes.map((n) => <li key={n}>{n}</li>)}</ul></Card>}
        </>
      )}
      <Modal open={!!form} title={form?.planId ? "Edit M&V plan" : "New M&V plan"} onClose={() => setForm(null)} wide>
        {form && (
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="sm:col-span-2"><Field label="Name"><Input value={form.name ?? ""} onChange={(e) => setForm({ ...form, name: e.target.value })} placeholder="Compressor VSD retrofit" /></Field></div>
            <Field label="IPMVP option"><Select value={form.mvOption ?? "C"} onChange={(e) => setForm({ ...form, mvOption: e.target.value })}>{OPTIONS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</Select></Field>
            <Field label="Action it verifies (optional)"><Select value={form.actionId ?? ""} onChange={(e) => setForm({ ...form, actionId: e.target.value || null })}><option value="">—</option>{(actions ?? []).map((a) => <option key={a.actionId} value={a.actionId}>{a.title}</option>)}</Select></Field>
            {(form.mvOption === "B" || form.mvOption === "C") ? (
              <Field label="Baseline model (EnPI)" hint={form.mvOption === "B" ? "An EnPI with an SEU boundary." : "A whole-site EnPI."}><Select value={form.enpiId ?? ""} onChange={(e) => setForm({ ...form, enpiId: e.target.value || null })}><option value="">—</option>{(enpis ?? []).map((e) => <option key={e.enpiId} value={e.enpiId}>{e.name} ({e.seuName ?? "site"})</option>)}</Select></Field>
            ) : (
              <>
                <Field label="Savings measured / simulated (kWh)"><Input type="number" value={form.manualSavingsKwh ?? ""} onChange={(e) => setForm({ ...form, manualSavingsKwh: e.target.value })} /></Field>
                <Field label="Their uncertainty (± %)"><Input type="number" value={form.manualUncertaintyPct ?? ""} onChange={(e) => setForm({ ...form, manualUncertaintyPct: e.target.value })} /></Field>
              </>
            )}
            <Field label="Measurement boundary"><Input value={form.boundary ?? ""} onChange={(e) => setForm({ ...form, boundary: e.target.value })} placeholder="Compressor room MCC feeder" /></Field>
            <Field label="Reporting from"><Input type="date" value={form.reportingFrom ?? ""} onChange={(e) => setForm({ ...form, reportingFrom: e.target.value })} /></Field>
            <Field label="Reporting to"><Input type="date" value={form.reportingTo ?? ""} onChange={(e) => setForm({ ...form, reportingTo: e.target.value })} /></Field>
            <Field label="Non-routine adjustment (kWh)" hint="Added to the baseline: e.g. a new line installed."><Input type="number" value={form.nonRoutineKwh ?? "0"} onChange={(e) => setForm({ ...form, nonRoutineKwh: e.target.value })} /></Field>
            <Field label="Confidence"><Select value={form.confidencePct ?? 90} onChange={(e) => setForm({ ...form, confidencePct: Number(e.target.value) })}>{[68, 80, 90, 95].map((c) => <option key={c} value={c}>{c} %</option>)}</Select></Field>
            <div className="sm:col-span-2"><Field label="Static factors"><Input value={form.staticFactors ?? ""} onChange={(e) => setForm({ ...form, staticFactors: e.target.value })} placeholder="Two shifts, 5 days, same product mix" /></Field></div>
            <div className="flex justify-end sm:col-span-2"><Button onClick={save}>Save</Button></div>
          </div>
        )}
      </Modal>
    </>
  );
}

// ---------- GHG activities: refrigerant losses, process emissions, Scope 3 ----------
type Factor = { factorId: string; source: string; name: string; scope: number; method: string; unit: string; co2e: number; isActive: boolean; scope3Category: number | null; reference: string };
type Activity = { activityId: string; factorId: string; source: string; factorName: string; scope: number; scope3Category: number | null; method: string; periodFrom: string; periodTo: string; quantity: number; unit: string; kgCo2ePerUnit: number; reference: string | null; note: string | null; co2eKg: number };

export function ActivitiesTab({ edit, onError }: TabProps) {
  const [list, load] = useLoad<Activity[]>("/api/v1/enms/activities", onError);
  const [factors] = useLoad<Factor[]>("/api/v1/enms/factors", onError);
  const [form, setForm] = useState<{ factorId: string; periodFrom: string; periodTo: string; quantity: string; reference: string; note: string } | null>(null);
  const choices = (factors ?? []).filter((f) => f.isActive && /^(ref|proc|s3)_/.test(f.source));
  const chosen = choices.find((f) => f.factorId === form?.factorId);
  const rows = list ?? [];
  const sum = (pred: (a: Activity) => boolean) => rows.filter(pred).reduce((t, a) => t + a.co2eKg, 0);

  function save() {
    if (!form) return;
    run(onError, () => api("/api/v1/enms/activities", { method: "POST", json: { ...form, quantity: Number(form.quantity) } }), () => { setForm(null); load(); });
  }

  return (
    <>
      <div className="mb-6 grid gap-4 sm:grid-cols-3">
        <Stat label="Refrigerant and SF₆ losses" value={`${fmt(sum((a) => a.method === "Fugitive"))} kg CO₂e`} sub="Scope 1 fugitive — mass balance of top-ups" />
        <Stat label="Process emissions" value={`${fmt(sum((a) => a.method === "Process"))} kg CO₂e`} sub="Scope 1 — e.g. carbonate calcination" />
        <Stat label="Scope 3 activities" value={`${fmt(sum((a) => a.scope === 3))} kg CO₂e`} sub="materials, freight, waste, travel, commuting" />
      </div>
      <Card title="Activity data (all entries)" actions={edit && <Button className="py-1" onClick={() => setForm({ factorId: choices[0]?.factorId ?? "", periodFrom: isoDay(-30), periodTo: isoDay(0), quantity: "", reference: "", note: "" })}><Plus size={14} /> Activity</Button>}>
        <Table head={["Period", "What", "Scope", "Quantity", "kg CO₂e / unit", "kg CO₂e", "Reference", ""]} empty={rows.length === 0}>
          {rows.map((a) => (
            <tr key={a.activityId}>
              <td className="px-3 py-2 text-xs">{day(a.periodFrom)} → {day(a.periodTo)}</td>
              <td className="px-3 py-2">{a.factorName}<div className="font-mono text-[11px] text-muted">{a.source}</div></td>
              <td className="px-3 py-2">{a.scope}{a.scope3Category ? ` · cat ${a.scope3Category}` : ""} <span className="text-xs text-muted">{a.method}</span></td>
              <td className="px-3 py-2 tabular-nums">{fmt(a.quantity)} {a.unit}</td>
              <td className="px-3 py-2 tabular-nums">{Number(a.kgCo2ePerUnit).toLocaleString()}</td>
              <td className="px-3 py-2 tabular-nums font-medium">{fmt(a.co2eKg)}</td>
              <td className="px-3 py-2 text-xs text-muted">{a.reference ?? ""}</td>
              <td className="px-3 py-2 text-right">{edit && <Button variant="ghost" className="py-1" aria-label="Delete" onClick={() => run(onError, () => api(`/api/v1/enms/activities/${a.activityId}`, { method: "DELETE" }), load)}><Trash2 size={14} /></Button>}</td>
            </tr>
          ))}
        </Table>
      </Card>
      <Modal open={!!form} title="New activity" onClose={() => setForm(null)}>
        {form && (
          <div className="space-y-3">
            <Field label="Factor" hint="Refrigerants: kg topped up (or charged minus recovered). Scope 3: the factor's unit (t, tkm, pkm, km, m³).">
              <Select value={form.factorId} onChange={(e) => setForm({ ...form, factorId: e.target.value })}>
                {choices.map((f) => <option key={f.factorId} value={f.factorId}>{f.scope === 3 ? `S3 cat ${f.scope3Category}` : f.method} — {f.name} ({f.co2e} kg/{f.unit})</option>)}
              </Select>
            </Field>
            {chosen && <p className="text-xs text-muted">{chosen.reference}</p>}
            <div className="grid gap-3 sm:grid-cols-2">
              <Field label="From"><Input type="date" value={form.periodFrom} onChange={(e) => setForm({ ...form, periodFrom: e.target.value })} /></Field>
              <Field label="To"><Input type="date" value={form.periodTo} onChange={(e) => setForm({ ...form, periodTo: e.target.value })} /></Field>
            </div>
            <Field label={`Quantity${chosen ? ` (${chosen.unit})` : ""}`}><Input type="number" step="any" value={form.quantity} onChange={(e) => setForm({ ...form, quantity: e.target.value })} /></Field>
            <Field label="Reference" hint="Service log, invoice, waste transfer note…"><Input value={form.reference} onChange={(e) => setForm({ ...form, reference: e.target.value })} /></Field>
            <Field label="Note"><Input value={form.note} onChange={(e) => setForm({ ...form, note: e.target.value })} /></Field>
            <div className="flex justify-end"><Button onClick={save}>Save</Button></div>
          </div>
        )}
      </Modal>
    </>
  );
}
