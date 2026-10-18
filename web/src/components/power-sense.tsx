"use client";

import { useCallback, useEffect, useState } from "react";
import { Sparkles } from "lucide-react";
import { api } from "@/lib/api";
import { Badge, Button, Card, ErrorNote, Field, Input, Select, errorText } from "./ui";

type Profile = { sourceTagId: string; sourceTagName: string; sourceUnit: string; offBelow: number; runAbove: number; countCycles: boolean; minCycleSec: number;
  autoLearn: boolean; learnedAt: string | null; learnedFrom: number | null; cycleCount: number; lastTs: string | null };
type Info = { profile: Profile | null; sources: { tagId: string; code: string; name: string; sensorTypeCode: string; unit: string }[] };
type Learned = { offBelow: number; runAbove: number; offLevel: number; idleLevel: number; runLevel: number; readings: number; note: string };

/**
 * Power sense: machine state and cycle count from one power or current sensor — for older machines with no PLC or data port.
 * The off / idle / running bands are learned from the machine's own power history.
 */
export function PowerSenseTab({ assetId, canEdit }: { assetId: string; canEdit: boolean }) {
  const [info, setInfo] = useState<Info | null>(null);
  const [form, setForm] = useState({ sourceTagId: "", offBelow: "", runAbove: "", countCycles: true, minCycleSec: "5", autoLearn: true });
  const [learned, setLearned] = useState<Learned | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const load = useCallback(() => api<Info>(`/api/v1/plant/assets/${assetId}/power-sense`).then((i) => {
    setInfo(i);
    const p = i.profile;
    setForm({ sourceTagId: p?.sourceTagId ?? i.sources[0]?.tagId ?? "", offBelow: p ? String(p.offBelow) : "", runAbove: p ? String(p.runAbove) : "",
      countCycles: p?.countCycles ?? true, minCycleSec: String(p?.minCycleSec ?? 5), autoLearn: p?.autoLearn ?? true });
  }).catch((e) => setError(errorText(e))), [assetId]);
  useEffect(() => { load(); }, [load]);

  async function save(enabled: boolean) {
    try {
      await api(`/api/v1/plant/assets/${assetId}/power-sense`, { method: "PUT", json: {
        enabled, sourceTagId: form.sourceTagId || null, offBelow: form.offBelow === "" ? null : Number(form.offBelow), runAbove: form.runAbove === "" ? null : Number(form.runAbove),
        countCycles: form.countCycles, minCycleSec: Number(form.minCycleSec || 5), autoLearn: form.autoLearn,
      } });
      setNotice(enabled ? "Power sense is on. State and cycles appear on the Live tab within seconds." : "Power sense is off.");
      setError(null);
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }
  async function learn() {
    try {
      const r = await api<Learned>(`/api/v1/plant/assets/${assetId}/power-sense/learn`, { method: "POST" });
      setLearned(r);
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }
  if (!info) return <ErrorNote error={error} />;
  const p = info.profile;
  const unit = info.sources.find((s) => s.tagId === form.sourceTagId)?.unit ?? "kW";

  return (
    <div className="space-y-4">
      <ErrorNote error={error} />
      {notice && <div className="rounded-lg bg-ok/10 px-3 py-2 text-sm text-ok">{notice}</div>}
      <Card title={<span className="flex items-center gap-2">Power sense {p ? <Badge tone="ok">on</Badge> : <Badge tone="muted">off</Badge>}</span>}>
        <p className="mb-4 text-sm text-muted">For machines without a PLC or data port: one power meter or current clamp is enough. FactoryOS learns the machine&apos;s off, idle and running
          power from its own history, then writes a machine state and a cycle count (one cycle per burst of power). Status, downtime, OEE, energy per part and alarms then work like on a connected machine.</p>
        {info.sources.length === 0 ? <p className="text-sm text-warn">Add a power (kW) or current (A) tag to this asset first.</p> : (
          <div className="grid gap-4 sm:grid-cols-3">
            <Field label="Source"><Select value={form.sourceTagId} disabled={!canEdit} onChange={(e) => setForm({ ...form, sourceTagId: e.target.value })}>
              {info.sources.map((s) => <option key={s.tagId} value={s.tagId}>{s.name} ({s.unit})</option>)}</Select></Field>
            <Field label={`Off below (${unit})`} hint="Empty = from rated power"><Input type="number" step="any" min="0" value={form.offBelow} disabled={!canEdit} onChange={(e) => setForm({ ...form, offBelow: e.target.value })} /></Field>
            <Field label={`Running above (${unit})`} hint="Between the two = idle"><Input type="number" step="any" min="0" value={form.runAbove} disabled={!canEdit} onChange={(e) => setForm({ ...form, runAbove: e.target.value })} /></Field>
            <Field label="Shortest cycle (s)" hint="Bursts closer together count once"><Input type="number" min="1" value={form.minCycleSec} disabled={!canEdit} onChange={(e) => setForm({ ...form, minCycleSec: e.target.value })} /></Field>
            <label className="flex items-center gap-2 self-end pb-2 text-sm"><input type="checkbox" checked={form.countCycles} disabled={!canEdit} onChange={(e) => setForm({ ...form, countCycles: e.target.checked })} /> Count cycles as parts</label>
            <label className="flex items-center gap-2 self-end pb-2 text-sm"><input type="checkbox" checked={form.autoLearn} disabled={!canEdit} onChange={(e) => setForm({ ...form, autoLearn: e.target.checked })} /> Re-learn the bands every day</label>
          </div>
        )}
        {canEdit && info.sources.length > 0 && (
          <div className="mt-4 flex flex-wrap gap-2">
            <Button onClick={() => save(true)}>{p ? "Save" : "Turn on"}</Button>
            {p && <Button variant="outline" onClick={learn}><Sparkles size={14} /> Learn from the last 24 hours</Button>}
            {p && <Button variant="ghost" className="text-bad" onClick={() => save(false)}>Turn off</Button>}
          </div>
        )}
      </Card>
      {p && (
        <Card title="Status">
          <dl className="grid grid-cols-2 gap-y-2 text-sm sm:grid-cols-4">
            <dt className="text-muted">Off below</dt><dd className="tabular-nums">{p.offBelow} {p.sourceUnit}</dd>
            <dt className="text-muted">Running above</dt><dd className="tabular-nums">{p.runAbove} {p.sourceUnit}</dd>
            <dt className="text-muted">Cycles counted</dt><dd className="tabular-nums">{p.cycleCount.toLocaleString()}</dd>
            <dt className="text-muted">Last learned</dt><dd>{p.learnedAt ? `${new Date(p.learnedAt + "Z").toLocaleString()} (${p.learnedFrom} readings)` : "not yet (rated-power defaults)"}</dd>
          </dl>
          {learned && <p className="mt-3 rounded-lg bg-panel-2 p-3 text-sm">{learned.note} Levels found: off ≈ {learned.offLevel}, idle ≈ {learned.idleLevel}, running ≈ {learned.runLevel} {p.sourceUnit} ({learned.readings} readings).</p>}
        </Card>
      )}
    </div>
  );
}
