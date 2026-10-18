"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { BellOff, Check, Pencil, Plus, Trash2 } from "lucide-react";
import { api } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { useLive } from "@/lib/live";
import { SensorType } from "@/lib/types";
import { FixSuggestions } from "@/components/fix-knowledge";
import { AuthImage } from "@/components/camera";
import { Badge, Button, Card, ErrorNote, Field, Input, Modal, PageHeader, Select, Stat, Table, cx, errorText } from "@/components/ui";

type Alarm = {
  alarmId: number; kind: "Rule" | "Anomaly"; ruleId: string | null; ruleName: string | null; assetId: string; assetName: string; severity: "Info" | "Warning" | "Critical";
  message: string; value: number | null; state: "Active" | "Acknowledged" | "Cleared"; raisedAt: string; ackAt: string | null; ackByName: string | null; ackComment: string | null; clearedAt: string | null;
};
type Summary = { active: number; critical: number; warning: number; info: number; unacknowledged: number };
type Rule = {
  ruleId: string; name: string; sensorTypeCode: string; assetId: string | null; assetName: string | null; assetTypeCode: string | null; operator: string; threshold: number;
  durationSec: number; severity: string; notify: boolean; createWorkOrder: boolean; isActive: boolean; matches: number; openAlarms: number;
};
type Floor = { assetId: string; name: string; assetTypeCode: string };
type RuleForm = { ruleId?: string; name: string; sensorTypeCode: string; scope: "all" | "type" | "asset"; assetId: string; assetTypeCode: string; operator: string; threshold: string; durationSec: string; severity: string; notify: boolean; createWorkOrder: boolean; isActive: boolean };

const sevTone = { Critical: "bad", Warning: "warn", Info: "info" } as const;
const when = (iso: string | null) => (iso ? new Date(iso.endsWith("Z") ? iso : iso + "Z").toLocaleString([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" }) : "—");
const empty: RuleForm = { name: "", sensorTypeCode: "", scope: "all", assetId: "", assetTypeCode: "", operator: ">", threshold: "", durationSec: "60", severity: "Warning", notify: true, createWorkOrder: false, isActive: true };

export default function AlarmsPage() {
  const { can } = useAuth();
  const [tab, setTab] = useState<"active" | "history" | "rules">("active");
  const [summary, setSummary] = useState<Summary | null>(null);
  const [error, setError] = useState<string | null>(null);
  const loadSummary = useCallback(() => api<Summary>("/api/v1/alarms/summary").then(setSummary).catch((e) => setError(errorText(e))), []);
  useEffect(() => {
    loadSummary();
    const t = setInterval(loadSummary, 15_000);
    return () => clearInterval(t);
  }, [loadSummary]);
  useLive({ alarms: () => loadSummary() });

  return (
    <>
      <PageHeader title="Alarms" sub="Rules raise alarms when a value stays out of range; predictive anomalies flag machines drifting from their normal behaviour." />
      <ErrorNote error={error} />
      {summary && (
        <div className="mb-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          <Stat label="Open alarms" value={summary.active} sub={`${summary.unacknowledged} not acknowledged`} />
          <Stat label="Critical" value={summary.critical} />
          <Stat label="Warning" value={summary.warning} />
          <Stat label="Info" value={summary.info} />
        </div>
      )}
      <div className="mb-4 flex gap-1 border-b border-line">
        {([["active", "Open"], ["history", "History"], ["rules", "Rules"]] as const).map(([t, label]) => (
          <button key={t} onClick={() => setTab(t)} className={cx("-mb-px border-b-2 px-4 py-2 text-sm", tab === t ? "border-brand font-medium text-brand" : "border-transparent text-muted hover:text-ink")}>{label}</button>
        ))}
      </div>
      {tab === "rules" ? <Rules manage={can("alarms.manage")} /> : <AlarmList open={tab === "active"} canAck={can("alarms.acknowledge")} onChange={loadSummary} />}
    </>
  );
}

function AlarmList({ open, canAck, onChange }: { open: boolean; canAck: boolean; onChange: () => void }) {
  const [rows, setRows] = useState<Alarm[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [ack, setAck] = useState<Alarm | null>(null);
  const [comment, setComment] = useState("");
  const load = useCallback(() => {
    const from = new Date(Date.now() - 7 * 86400_000).toISOString();
    api<Alarm[]>(open ? "/api/v1/alarms?state=open" : `/api/v1/alarms?from=${from}`).then(setRows).catch((e) => setError(errorText(e)));
  }, [open]);
  useEffect(() => {
    load();
    const t = setInterval(load, 10_000);
    return () => clearInterval(t);
  }, [load]);
  useLive({ alarms: () => load() });

  async function acknowledge(e: React.FormEvent) {
    e.preventDefault();
    if (!ack) return;
    try {
      await api(`/api/v1/alarms/${ack.alarmId}/ack`, { method: "POST", json: { comment: comment || null } });
      setAck(null);
      setComment("");
      load();
      onChange();
    } catch (err) {
      setError(errorText(err));
    }
  }

  return (
    <>
      <ErrorNote error={error} />
      <Card>
        {rows.length === 0 ? (
          <p className="flex items-center gap-2 py-6 text-sm text-muted"><BellOff size={16} /> {open ? "No open alarms. All quiet." : "No alarms in the last 7 days."}</p>
        ) : (
          <Table head={["Severity", "Alarm", "Machine", "Raised", "State", ""]}>
            {rows.map((a) => (
              <tr key={a.alarmId} className={a.state === "Active" && a.severity === "Critical" ? "bg-bad/5" : ""}>
                <td className="px-3 py-2"><Badge tone={sevTone[a.severity]}>{a.severity}</Badge>{a.kind === "Anomaly" && <div className="mt-1 text-[11px] text-muted">predictive</div>}</td>
                <td className="max-w-md px-3 py-2 text-sm">{a.message}{a.ackComment && <div className="text-xs text-muted">“{a.ackComment}” — {a.ackByName}</div>}</td>
                <td className="px-3 py-2"><Link href={`/app/plant/assets/${a.assetId}`} className="hover:text-brand">{a.assetName}</Link></td>
                <td className="whitespace-nowrap px-3 py-2 text-xs">{when(a.raisedAt)}{a.clearedAt && <div className="text-muted">cleared {when(a.clearedAt)}</div>}</td>
                <td className="px-3 py-2"><Badge tone={a.state === "Active" ? "bad" : a.state === "Acknowledged" ? "warn" : "muted"}>{a.state}</Badge></td>
                <td className="px-3 py-2 text-right">{canAck && a.state === "Active" && <Button variant="outline" className="py-1 text-xs" onClick={() => setAck(a)}><Check size={12} /> Acknowledge</Button>}</td>
              </tr>
            ))}
          </Table>
        )}
      </Card>
      <Modal open={!!ack} wide title="Acknowledge alarm" onClose={() => setAck(null)}>
        <form onSubmit={acknowledge} className="space-y-4">
          <p className="text-sm">{ack?.message}</p>
          {ack && <AuthImage path={`/api/v1/alarms/${ack.alarmId}/snapshot`} alt="Camera picture when the alarm was raised" className="max-h-72 w-full rounded-lg border border-line bg-black object-contain" />}
          {ack && <FixSuggestions path={`/api/v1/maintenance/alarms/${ack.alarmId}/suggestions`} onUse={(f) => setComment(`Trying the earlier fix: ${f.fix}`.slice(0, 300))} />}
          <Field label="What is being done?" hint="Optional; shown to the team"><Input value={comment} maxLength={300} onChange={(e) => setComment(e.target.value)} /></Field>
          <div className="flex justify-end gap-2"><Button type="button" variant="ghost" onClick={() => setAck(null)}>Cancel</Button><Button type="submit">Acknowledge</Button></div>
        </form>
      </Modal>
    </>
  );
}

function Rules({ manage }: { manage: boolean }) {
  const [rules, setRules] = useState<Rule[]>([]);
  const [sensors, setSensors] = useState<SensorType[]>([]);
  const [assets, setAssets] = useState<Floor[]>([]);
  const [form, setForm] = useState<RuleForm | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(() => api<Rule[]>("/api/v1/alarms/rules").then(setRules).catch((e) => setError(errorText(e))), []);
  useEffect(() => {
    load();
    api<SensorType[]>("/api/v1/plant/sensor-types").then(setSensors).catch(() => undefined);
    api<Floor[]>("/api/v1/plant/floor").then(setAssets).catch(() => undefined);
  }, [load]);
  const types = [...new Set(assets.map((a) => a.assetTypeCode))].sort();
  const unit = (code: string) => sensors.find((s) => s.sensorTypeCode === code)?.unit ?? "";

  function edit(r: Rule) {
    setForm({ ruleId: r.ruleId, name: r.name, sensorTypeCode: r.sensorTypeCode, scope: r.assetId ? "asset" : r.assetTypeCode ? "type" : "all", assetId: r.assetId ?? "",
      assetTypeCode: r.assetTypeCode ?? "", operator: r.operator, threshold: String(r.threshold), durationSec: String(r.durationSec), severity: r.severity, notify: r.notify,
      createWorkOrder: r.createWorkOrder, isActive: r.isActive });
  }
  async function save(e: React.FormEvent) {
    e.preventDefault();
    if (!form) return;
    const body = {
      name: form.name, sensorTypeCode: form.sensorTypeCode, assetId: form.scope === "asset" ? form.assetId : null, assetTypeCode: form.scope === "type" ? form.assetTypeCode : null,
      operator: form.operator, threshold: Number(form.threshold), durationSec: Number(form.durationSec), severity: form.severity, notify: form.notify,
      createWorkOrder: form.createWorkOrder, isActive: form.isActive,
    };
    try {
      await api(form.ruleId ? `/api/v1/alarms/rules/${form.ruleId}` : "/api/v1/alarms/rules", { method: form.ruleId ? "PUT" : "POST", json: body });
      setForm(null);
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }
  async function remove(r: Rule) {
    if (!confirm(`Delete the rule "${r.name}"? Its open alarms are cleared.`)) return;
    try {
      await api(`/api/v1/alarms/rules/${r.ruleId}`, { method: "DELETE" });
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }

  return (
    <>
      <ErrorNote error={error} />
      <Card title={`${rules.length} rules`} actions={manage && <Button className="py-1" onClick={() => setForm({ ...empty })}><Plus size={14} /> Rule</Button>}>
        <Table head={["Rule", "Condition", "Applies to", "Severity", "Actions", "Open", ""]} empty={rules.length === 0}>
          {rules.map((r) => (
            <tr key={r.ruleId} className={r.isActive ? "" : "opacity-50"}>
              <td className="px-3 py-2 font-medium">{r.name}</td>
              <td className="px-3 py-2 font-mono text-xs">{r.sensorTypeCode} {r.operator} {r.threshold} {unit(r.sensorTypeCode)}{r.durationSec > 0 && ` for ${r.durationSec}s`}</td>
              <td className="px-3 py-2 text-sm">{r.assetName ?? (r.assetTypeCode ? `every ${r.assetTypeCode}` : "every asset")} <span className="text-xs text-muted">({r.matches} tags)</span></td>
              <td className="px-3 py-2"><Badge tone={sevTone[r.severity as keyof typeof sevTone] ?? "muted"}>{r.severity}</Badge></td>
              <td className="px-3 py-2 text-xs">{[r.notify && "notify", r.createWorkOrder && "work order"].filter(Boolean).join(", ") || "—"}</td>
              <td className="px-3 py-2 tabular-nums">{r.openAlarms}</td>
              <td className="whitespace-nowrap px-3 py-2 text-right">{manage && <>
                <Button variant="ghost" className="py-1" onClick={() => edit(r)} aria-label="Edit rule"><Pencil size={14} /></Button>
                <Button variant="ghost" className="py-1 text-bad" onClick={() => remove(r)} aria-label="Delete rule"><Trash2 size={14} /></Button></>}</td>
            </tr>
          ))}
        </Table>
      </Card>
      <Modal open={!!form} title={form?.ruleId ? "Edit rule" : "New rule"} onClose={() => setForm(null)}>
        {form && (
          <form onSubmit={save} className="grid gap-4 sm:grid-cols-2">
            <div className="sm:col-span-2"><Field label="Name"><Input required value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} placeholder="Oven temperature too high" /></Field></div>
            <Field label="Sensor"><Select required value={form.sensorTypeCode} onChange={(e) => setForm({ ...form, sensorTypeCode: e.target.value })}>
              <option value="">Choose…</option>{sensors.map((s) => <option key={s.sensorTypeCode} value={s.sensorTypeCode}>{s.name}{s.unit ? ` (${s.unit})` : ""}</option>)}</Select></Field>
            <Field label="Applies to"><Select value={form.scope} onChange={(e) => setForm({ ...form, scope: e.target.value as RuleForm["scope"] })}>
              <option value="all">Every asset with this sensor</option><option value="type">Every asset of a type</option><option value="asset">One asset</option></Select></Field>
            {form.scope === "type" && <Field label="Asset type"><Select required value={form.assetTypeCode} onChange={(e) => setForm({ ...form, assetTypeCode: e.target.value })}>
              <option value="">Choose…</option>{types.map((t) => <option key={t}>{t}</option>)}</Select></Field>}
            {form.scope === "asset" && <Field label="Asset"><Select required value={form.assetId} onChange={(e) => setForm({ ...form, assetId: e.target.value })}>
              <option value="">Choose…</option>{assets.map((a) => <option key={a.assetId} value={a.assetId}>{a.name}</option>)}</Select></Field>}
            <Field label="Condition"><div className="flex gap-2">
              <Select value={form.operator} onChange={(e) => setForm({ ...form, operator: e.target.value })} className="w-20">{[">", ">=", "<", "<=", "=", "!="].map((o) => <option key={o}>{o}</option>)}</Select>
              <Input required type="number" step="any" value={form.threshold} onChange={(e) => setForm({ ...form, threshold: e.target.value })} placeholder={unit(form.sensorTypeCode)} /></div></Field>
            <Field label="For at least (seconds)" hint="0 = immediately"><Input type="number" min="0" max="86400" value={form.durationSec} onChange={(e) => setForm({ ...form, durationSec: e.target.value })} /></Field>
            <Field label="Severity"><Select value={form.severity} onChange={(e) => setForm({ ...form, severity: e.target.value })}><option>Info</option><option>Warning</option><option>Critical</option></Select></Field>
            <div className="space-y-2 text-sm sm:col-span-2">
              <label className="flex items-center gap-2"><input type="checkbox" checked={form.notify} onChange={(e) => setForm({ ...form, notify: e.target.checked })} /> Send e-mail / webhook (addresses in Settings)</label>
              <label className="flex items-center gap-2"><input type="checkbox" checked={form.createWorkOrder} onChange={(e) => setForm({ ...form, createWorkOrder: e.target.checked })} /> Open a maintenance work order</label>
              <label className="flex items-center gap-2"><input type="checkbox" checked={form.isActive} onChange={(e) => setForm({ ...form, isActive: e.target.checked })} /> Active</label>
            </div>
            <div className="flex justify-end gap-2 sm:col-span-2"><Button type="button" variant="ghost" onClick={() => setForm(null)}>Cancel</Button><Button type="submit">Save</Button></div>
          </form>
        )}
      </Modal>
    </>
  );
}
