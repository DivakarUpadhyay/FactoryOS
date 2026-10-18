"use client";

import { useCallback, useEffect, useState } from "react";
import { Plus, Trash2 } from "lucide-react";
import { api } from "@/lib/api";
import { BarChart, fmt } from "./charts";
import { Badge, Button, Card, ErrorNote, Field, Input, Modal, Select, Stat, Table, errorText } from "./ui";

type Forecast = {
  currency: string; monthStart: string; daysElapsed: number; daysInMonth: number; kwhToDate: number; energyCostToDate: number; forecastKwh: number; forecastEnergyCost: number;
  basicCharge: number; peakKw: number; contractKw: number | null; excessKw: number; excessPenalty: number; forecastTotal: number; demandChargePerKw: number | null;
  days: { day: string; kwh: number; cost: number; forecast: boolean }[]; notes: string[];
};
type ShiftEnergy = { shift: string; from: string; to: string; kwh: number; cost: number; peakKwh: number; topMachines: { assetId: string; name: string; kwh: number; sharePct: number }[] };
type Rule = { ruleId: string; name: string; assetId: string; assetName: string; priority: number; shedAboveKw: number; restoreBelowKw: number; action: "Notify" | "Write";
  writeTagId: string | null; writeTagName: string | null; shedValue: number | null; restoreValue: number | null; isActive: boolean; shedNow: boolean };
type ShedEvent = { eventId: number; ruleName: string; assetName: string; demandKw: number; shedAt: string; restoredAt: string | null; result: string | null };
const when = (iso: string | null) => (iso ? new Date(iso + (iso.endsWith("Z") ? "" : "Z")).toLocaleString([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" }) : "—");

export function BillForecastTab() {
  const [f, setF] = useState<Forecast | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { api<Forecast>("/api/v1/energy/forecast").then(setF).catch((e) => setError(errorText(e))); }, []);
  if (!f) return <ErrorNote error={error} />;
  const m = (n: number) => `${f.currency} ${Math.round(n).toLocaleString()}`;
  return (
    <>
      <div className="mb-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <Stat label="Forecast bill this month" value={m(f.forecastTotal)} sub={`day ${f.daysElapsed} of ${f.daysInMonth}`} />
        <Stat label="Energy so far" value={m(f.energyCostToDate)} sub={`${fmt(f.kwhToDate)} kWh`} />
        <Stat label="Basic charge" value={m(f.basicCharge)} sub={f.contractKw ? `${f.contractKw} kW contract × ${f.demandChargePerKw ?? "—"}` : "Set contract in Settings"} />
        <Stat label="Excess-demand penalty" value={m(f.excessPenalty)} sub={f.excessKw > 0 ? `peak ${fmt(f.peakKw)} kW, ${fmt(f.excessKw)} kW over` : `peak ${fmt(f.peakKw)} kW`} />
      </div>
      {f.notes.length > 0 && <Card className="mb-6"><ul className="list-disc space-y-1 pl-5 text-sm">{f.notes.map((n) => <li key={n}>{n}</li>)}</ul></Card>}
      <Card title="Energy cost per day (grey = forecast)">
        <BarChart bars={f.days.map((d) => ({ label: d.day.slice(8, 10), value: d.cost, tone: d.forecast ? "muted" as const : "brand" as const, title: `${d.day.slice(0, 10)}: ${m(d.cost)}${d.forecast ? " (forecast)" : ""}` }))} />
      </Card>
    </>
  );
}

export function EnergyShiftsTab() {
  const [date, setDate] = useState(() => new Date().toISOString().slice(0, 10));
  const [rows, setRows] = useState<ShiftEnergy[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { setRows(null); api<ShiftEnergy[]>(`/api/v1/energy/shifts?date=${date}`).then(setRows).catch((e) => setError(errorText(e))); }, [date]);
  return (
    <>
      <ErrorNote error={error} />
      <div className="mb-4 flex justify-end"><Input type="date" value={date} onChange={(e) => setDate(e.target.value)} className="w-44 py-1" aria-label="Day" /></div>
      <div className="grid gap-4 lg:grid-cols-2">
        {(rows ?? []).map((s) => (
          <Card key={s.shift} title={`${s.shift} shift · ${s.from.slice(11, 16)}–${s.to.slice(11, 16)}`}>
            <div className="mb-3 grid grid-cols-3 gap-2 text-center">
              <div className="rounded-lg bg-panel-2 p-2"><div className="text-xs text-muted">Energy</div><div className="font-semibold">{fmt(s.kwh)} kWh</div></div>
              <div className="rounded-lg bg-panel-2 p-2"><div className="text-xs text-muted">Cost</div><div className="font-semibold">{s.cost.toLocaleString()}</div></div>
              <div className="rounded-lg bg-panel-2 p-2"><div className="text-xs text-muted">Busiest hour</div><div className="font-semibold">{fmt(s.peakKwh)} kWh</div></div>
            </div>
            <ul className="space-y-1 text-sm">{s.topMachines.map((m) => <li key={m.assetId} className="flex justify-between"><span>{m.name}</span><span className="tabular-nums text-muted">{fmt(m.kwh)} kWh · {m.sharePct}%</span></li>)}</ul>
          </Card>
        ))}
      </div>
    </>
  );
}

export function LoadShedTab({ canManage }: { canManage: boolean }) {
  const [rules, setRules] = useState<Rule[]>([]);
  const [events, setEvents] = useState<ShedEvent[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [form, setForm] = useState<Record<string, string> | null>(null);
  const [assets, setAssets] = useState<{ assetId: string; name: string }[]>([]);
  const [tags, setTags] = useState<{ tagId: string; name: string; address: string | null }[]>([]);
  const load = useCallback(() => {
    Promise.all([api<Rule[]>("/api/v1/energy/load-shed/rules"), api<ShedEvent[]>("/api/v1/energy/load-shed/events")])
      .then(([r, e]) => { setRules(r); setEvents(e); }).catch((e) => setError(errorText(e)));
  }, []);
  useEffect(() => { load(); const t = setInterval(load, 15_000); return () => clearInterval(t); }, [load]);
  useEffect(() => { if (form) api<{ assetId: string; name: string }[]>("/api/v1/plant/floor").then(setAssets).catch(() => undefined); }, [form !== null]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (form?.assetId) api<typeof tags>(`/api/v1/plant/assets/${form.assetId}/tags`).then(setTags).catch(() => setTags([])); }, [form?.assetId]); // eslint-disable-line react-hooks/exhaustive-deps

  async function save(e: React.FormEvent) {
    e.preventDefault();
    if (!form) return;
    const n = (k: string) => (form[k] === undefined || form[k] === "" ? null : Number(form[k]));
    try {
      await api(form.ruleId ? `/api/v1/energy/load-shed/rules/${form.ruleId}` : "/api/v1/energy/load-shed/rules", { method: form.ruleId ? "PUT" : "POST", json: {
        name: form.name, assetId: form.assetId, priority: n("priority"), shedAboveKw: n("shedAboveKw"), restoreBelowKw: n("restoreBelowKw"), action: form.action ?? "Notify",
        writeTagId: form.action === "Write" ? form.writeTagId || null : null, shedValue: n("shedValue"), restoreValue: n("restoreValue"), isActive: true,
      } });
      setForm(null);
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }
  async function remove(r: Rule) {
    if (!confirm(`Delete "${r.name}"?`)) return;
    await api(`/api/v1/energy/load-shed/rules/${r.ruleId}`, { method: "DELETE" }).catch((e) => setError(errorText(e)));
    load();
  }
  const F = (k: string, label: string, props: React.InputHTMLAttributes<HTMLInputElement> = {}) =>
    <Field label={label}><Input value={form?.[k] ?? ""} onChange={(e) => setForm({ ...form!, [k]: e.target.value })} {...props} /></Field>;

  return (
    <>
      <ErrorNote error={error} />
      <Card title="Load-shedding rules" className="mb-6" actions={canManage && <Button className="py-1" onClick={() => setForm({ action: "Notify", priority: String(rules.length + 1) })}><Plus size={14} /> Rule</Button>}>
        <p className="mb-3 text-sm text-muted">When the main meter&apos;s demand passes a rule&apos;s level, loads are shed one at a time in priority order (1 first), and brought back in reverse once demand stays below the restore level (each load stays off at least 2 minutes). Switching works over Modbus TCP and MQTT; other loads get a notification.</p>
        <Table head={["#", "Rule", "Load", "Shed above", "Restore below", "Action", "Now", ""]} empty={rules.length === 0}>
          {rules.map((r) => (
            <tr key={r.ruleId}>
              <td className="px-3 py-2 tabular-nums">{r.priority}</td>
              <td className="px-3 py-2 font-medium">{r.name}</td>
              <td className="px-3 py-2 text-sm">{r.assetName}</td>
              <td className="px-3 py-2 tabular-nums">{r.shedAboveKw} kW</td>
              <td className="px-3 py-2 tabular-nums">{r.restoreBelowKw} kW</td>
              <td className="px-3 py-2 text-xs">{r.action === "Write" ? `write ${r.shedValue} / ${r.restoreValue} to ${r.writeTagName}` : "notify"}</td>
              <td className="px-3 py-2">{r.shedNow ? <Badge tone="warn">shed</Badge> : <Badge tone="ok">on</Badge>}</td>
              <td className="px-3 py-2 text-right">{canManage && <Button variant="ghost" className="py-1 text-bad" onClick={() => remove(r)} aria-label="Delete rule"><Trash2 size={14} /></Button>}</td>
            </tr>
          ))}
        </Table>
      </Card>
      <Card title="Shed events">
        <Table head={["Shed", "Restored", "Rule", "Demand", "Result"]} empty={events.length === 0}>
          {events.map((e) => (
            <tr key={e.eventId}><td className="whitespace-nowrap px-3 py-2 text-xs">{when(e.shedAt)}</td><td className="whitespace-nowrap px-3 py-2 text-xs">{when(e.restoredAt)}</td>
              <td className="px-3 py-2 text-sm">{e.ruleName} · {e.assetName}</td><td className="px-3 py-2 tabular-nums">{fmt(e.demandKw)} kW</td><td className="px-3 py-2 text-xs">{e.result}</td></tr>
          ))}
        </Table>
      </Card>
      <Modal open={!!form} title="Load-shedding rule" onClose={() => setForm(null)} wide>
        {form && (
          <form onSubmit={save} className="grid gap-4 sm:grid-cols-2">
            {F("name", "Name", { required: true, placeholder: "Stop compressor 2 at peak" })}
            <Field label="Load to shed"><Select required value={form.assetId ?? ""} onChange={(e) => setForm({ ...form, assetId: e.target.value, writeTagId: "" })}><option value="">Choose…</option>{assets.map((a) => <option key={a.assetId} value={a.assetId}>{a.name}</option>)}</Select></Field>
            {F("priority", "Priority (1 = first)", { required: true, type: "number", min: "1", max: "99" })}
            {F("shedAboveKw", "Shed when demand above (kW)", { required: true, type: "number", step: "any", min: "0" })}
            {F("restoreBelowKw", "Restore when demand below (kW)", { required: true, type: "number", step: "any", min: "0" })}
            <Field label="Action"><Select value={form.action} onChange={(e) => setForm({ ...form, action: e.target.value })}><option value="Notify">Notify only</option><option value="Write">Switch the load (write a tag)</option></Select></Field>
            {form.action === "Write" && <>
              <Field label="Tag to write"><Select required value={form.writeTagId ?? ""} onChange={(e) => setForm({ ...form, writeTagId: e.target.value })}><option value="">Choose…</option>{tags.map((t) => <option key={t.tagId} value={t.tagId}>{t.name} ({t.address ?? "no address"})</option>)}</Select></Field>
              {F("shedValue", "Value to switch off", { required: true, type: "number", step: "any" })}
              {F("restoreValue", "Value to switch back on", { required: true, type: "number", step: "any" })}
            </>}
            <div className="flex justify-end gap-2 sm:col-span-2"><Button type="button" variant="ghost" onClick={() => setForm(null)}>Cancel</Button><Button type="submit">Save</Button></div>
          </form>
        )}
      </Modal>
    </>
  );
}
