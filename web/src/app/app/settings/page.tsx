"use client";

import { useEffect, useState } from "react";
import { Plus, Trash2 } from "lucide-react";
import { api } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { LineSettings } from "@/components/line-settings";
import { Button, Card, ErrorNote, Field, Input, PageHeader, Select, errorText } from "@/components/ui";

type Shift = { name: string; start: string; end: string; breakMinutes: number };
type Settings = {
  timeZone: string; currency: string; peakRate: number; offPeakRate: number; peakStartHour: number; peakEndHour: number; contractKw: number | null;
  electricityFactor: number; gasFactor: number; mainMeterAssetId: string | null; shifts: Shift[]; alertEmails: string | null; alertWebhookUrl: string | null; demandChargePerKw: number | null;
};
type Meter = { assetId: string; name: string; assetTypeCode: string };

/** Company settings used by energy, carbon, OEE (shifts) and alarm e-mails. */
export default function SettingsPage() {
  const { can } = useAuth();
  const edit = can("settings.manage");
  const [s, setS] = useState<Settings | null>(null);
  const [meters, setMeters] = useState<Meter[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api<Settings>("/api/v1/settings").then(setS).catch((e) => setError(errorText(e)));
    api<Meter[]>("/api/v1/plant/floor").then((f) => setMeters(f.filter((a) => a.assetTypeCode === "energy-meter"))).catch(() => undefined);
  }, []);

  if (!s) return <><PageHeader title="Settings" /><ErrorNote error={error} /></>;
  const set = <K extends keyof Settings>(k: K, v: Settings[K]) => { setS({ ...s, [k]: v }); setSaved(false); };
  const num = (k: keyof Settings) => (e: React.ChangeEvent<HTMLInputElement>) => set(k, (e.target.value === "" ? null : Number(e.target.value)) as never);
  const setShift = (i: number, patch: Partial<Shift>) => set("shifts", s.shifts.map((x, j) => (j === i ? { ...x, ...patch } : x)));

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await api("/api/v1/settings", { method: "PUT", json: s });
      setSaved(true);
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <PageHeader title="Settings" sub={edit ? "Used by Energy, Carbon, Production (shifts) and Alarms (e-mails)." : "Read only — ask a factory admin to change these."} />
      <ErrorNote error={error} />
      {saved && <div className="mb-4 rounded-lg bg-ok/10 px-3 py-2 text-sm text-ok">Settings saved.</div>}
      <form onSubmit={save} className="space-y-4">
        <fieldset disabled={!edit} className="space-y-4">
          <Card title="Region">
            <div className="grid gap-4 sm:grid-cols-3">
              <Field label="Time zone" hint="IANA name, e.g. Asia/Taipei"><Input value={s.timeZone} onChange={(e) => set("timeZone", e.target.value)} required /></Field>
              <Field label="Currency"><Input value={s.currency} maxLength={3} onChange={(e) => set("currency", e.target.value)} required /></Field>
            </div>
          </Card>
          <Card title="Electricity tariff and contract">
            <div className="grid gap-4 sm:grid-cols-3">
              <Field label="Peak rate" hint={`${s.currency} per kWh`}><Input type="number" step="0.01" min="0" value={s.peakRate} onChange={num("peakRate")} required /></Field>
              <Field label="Off-peak rate" hint={`${s.currency} per kWh`}><Input type="number" step="0.01" min="0" value={s.offPeakRate} onChange={num("offPeakRate")} required /></Field>
              <Field label="Contract capacity" hint="kW; peak-demand alerts compare against it"><Input type="number" step="1" min="0" value={s.contractKw ?? ""} onChange={num("contractKw")} /></Field>
              <Field label="Basic charge per contract kW" hint={`${s.currency} per kW per month; used by the bill forecast`}><Input type="number" step="0.01" min="0" value={s.demandChargePerKw ?? ""} onChange={num("demandChargePerKw")} /></Field>
              <Field label="Peak starts (hour)" hint="Weekdays; weekends are off-peak"><Input type="number" min="0" max="23" value={s.peakStartHour} onChange={num("peakStartHour")} required /></Field>
              <Field label="Peak ends (hour)"><Input type="number" min="1" max="24" value={s.peakEndHour} onChange={num("peakEndHour")} required /></Field>
              <Field label="Main meter" hint="Leave on automatic to use plant-level meters">
                <Select value={s.mainMeterAssetId ?? ""} onChange={(e) => set("mainMeterAssetId", e.target.value || null)}>
                  <option value="">Automatic</option>
                  {meters.map((m) => <option key={m.assetId} value={m.assetId}>{m.name}</option>)}
                </Select>
              </Field>
            </div>
          </Card>
          <Card title="Emission factors">
            <div className="grid gap-4 sm:grid-cols-3">
              <Field label="Grid electricity" hint="kg CO₂e per kWh (Taiwan 2024: 0.474)"><Input type="number" step="0.001" min="0" value={s.electricityFactor} onChange={num("electricityFactor")} required /></Field>
              <Field label="Natural gas" hint="kg CO₂e per Nm³"><Input type="number" step="0.01" min="0" value={s.gasFactor} onChange={num("gasFactor")} required /></Field>
            </div>
          </Card>
          <Card title="Shifts" actions={edit && <Button type="button" variant="outline" className="py-1" onClick={() => set("shifts", [...s.shifts, { name: "", start: "08:00", end: "16:00", breakMinutes: 30 }])}><Plus size={14} /> Shift</Button>}>
            <div className="space-y-3">
              {s.shifts.map((sh, i) => (
                <div key={i} className="grid grid-cols-2 items-end gap-3 sm:grid-cols-5">
                  <Field label="Name"><Input value={sh.name} onChange={(e) => setShift(i, { name: e.target.value })} required /></Field>
                  <Field label="Start"><Input type="time" value={sh.start} onChange={(e) => setShift(i, { start: e.target.value })} required /></Field>
                  <Field label="End"><Input type="time" value={sh.end} onChange={(e) => setShift(i, { end: e.target.value })} required /></Field>
                  <Field label="Break (min)"><Input type="number" min="0" max="240" value={sh.breakMinutes} onChange={(e) => setShift(i, { breakMinutes: Number(e.target.value) })} /></Field>
                  {edit && s.shifts.length > 1 && <Button type="button" variant="ghost" className="mb-0.5 text-bad" onClick={() => set("shifts", s.shifts.filter((_, j) => j !== i))} aria-label="Remove shift"><Trash2 size={14} /></Button>}
                </div>
              ))}
            </div>
          </Card>
          <Card title="Alarm notifications">
            <Field label="E-mail addresses" hint="Comma separated; critical alarms are sent here">
              <Input value={s.alertEmails ?? ""} onChange={(e) => set("alertEmails", e.target.value || null)} placeholder="plant.manager@company.com, maintenance@company.com" />
            </Field>
            <div className="mt-4">
              <Field label="Webhook" hint="Every alarm is posted here as JSON — connect Teams, Slack or your own system">
                <Input type="url" value={s.alertWebhookUrl ?? ""} onChange={(e) => set("alertWebhookUrl", e.target.value || null)} placeholder="https://example.com/hooks/factory-alarms" />
              </Field>
            </div>
          </Card>
        </fieldset>
        {edit && <Button type="submit" disabled={busy}>{busy ? "Saving…" : "Save settings"}</Button>}
      </form>
      {edit && <div className="mt-6"><LineSettings /></div>}
    </>
  );
}
