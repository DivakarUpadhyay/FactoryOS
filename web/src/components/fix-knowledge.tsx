"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { Lightbulb, Search } from "lucide-react";
import { api } from "@/lib/api";
import { Badge, Button, Card, ErrorNote, Field, Input, Table, errorText } from "./ui";

export type PastFix = { maintWoId: string; code: string; title: string; assetId: string; assetName: string; failureCode: string | null; cause: string; fix: string;
  recordedAt: string; recordedBy: string | null; helpful: number };
type Suggestion = { fix: PastFix; score: number; reasons: string[] };
type Resolution = { failureCode: string | null; cause: string; fix: string; recordedAt: string; recordedBy: string | null };

const day = (iso: string) => new Date(iso.endsWith("Z") ? iso : iso + "Z").toLocaleDateString();

/** Suggested fixes from past breakdowns that look like this one (same alarm rule, sensor, machine or words). */
export function FixSuggestions({ path, onUse }: { path: string; onUse?: (f: PastFix) => void }) {
  const [list, setList] = useState<Suggestion[] | null>(null);
  useEffect(() => { api<Suggestion[]>(path).then(setList).catch(() => setList([])); }, [path]);
  if (!list || list.length === 0) return null;
  return (
    <div className="rounded-lg border border-warn/40 bg-warn/5 p-3">
      <div className="mb-2 flex items-center gap-2 font-medium"><Lightbulb size={14} className="text-warn" /> Fixed before: {list.length} similar {list.length === 1 ? "case" : "cases"}</div>
      <ul className="space-y-2">
        {list.map((s) => (
          <li key={s.fix.maintWoId} className="rounded-md bg-panel p-2 text-sm">
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-medium">{s.fix.title}</span>
              {s.fix.failureCode && <Badge tone="info">{s.fix.failureCode}</Badge>}
              <span className="text-xs text-muted">{s.fix.assetName} · {day(s.fix.recordedAt)}{s.fix.recordedBy && ` · ${s.fix.recordedBy}`}{s.fix.helpful > 0 && ` · reused ${s.fix.helpful}×`}</span>
            </div>
            <div className="mt-1"><span className="text-muted">Cause:</span> {s.fix.cause}</div>
            <div><span className="text-muted">Fix:</span> {s.fix.fix}</div>
            <div className="mt-1 flex items-center justify-between gap-2">
              <span className="text-[11px] text-muted">{s.reasons.join(" · ")}</span>
              {onUse && <Button variant="outline" className="py-0.5 text-xs" onClick={() => onUse(s.fix)}>Use this fix</Button>}
            </div>
          </li>
        ))}
      </ul>
    </div>
  );
}

/** Cause and fix of a breakdown work order, with suggestions from the fix library. Required before a corrective order can be completed. */
export function ResolutionPanel({ woId, editable, required, onChange }: { woId: string; editable: boolean; required: boolean; onChange: (recorded: boolean) => void }) {
  const [saved, setSaved] = useState<Resolution | null>(null);
  const [form, setForm] = useState({ failureCode: "", cause: "", fix: "" });
  const [codes, setCodes] = useState<string[]>([]);
  const [usedFrom, setUsedFrom] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(() => api<Resolution | undefined>(`/api/v1/maintenance/work-orders/${woId}/resolution`).then((r) => {
    setSaved(r ?? null);
    onChange(!!r);
    if (r) setForm({ failureCode: r.failureCode ?? "", cause: r.cause, fix: r.fix });
  }).catch((e) => setError(errorText(e))), [woId, onChange]);
  useEffect(() => {
    load();
    api<{ failureCodes: string[] }>("/api/v1/maintenance/knowledge").then((k) => setCodes(k.failureCodes)).catch(() => undefined);
  }, [load]);

  async function save() {
    try {
      await api(`/api/v1/maintenance/work-orders/${woId}/resolution`, { method: "PUT", json: { failureCode: form.failureCode || null, cause: form.cause, fix: form.fix } });
      if (usedFrom) await api(`/api/v1/maintenance/knowledge/${usedFrom}/helpful`, { method: "POST" }).catch(() => undefined);
      setUsedFrom(null);
      setError(null);
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }
  function use(f: PastFix) {
    setForm({ failureCode: f.failureCode ?? "", cause: f.cause, fix: f.fix });
    setUsedFrom(f.maintWoId);
  }
  const dirty = form.cause !== (saved?.cause ?? "") || form.fix !== (saved?.fix ?? "") || form.failureCode !== (saved?.failureCode ?? "");

  return (
    <div className="space-y-3">
      {editable && <FixSuggestions path={`/api/v1/maintenance/work-orders/${woId}/suggestions`} onUse={use} />}
      <div className="rounded-lg border border-line p-3">
        <div className="mb-2 flex items-center gap-2 font-medium">Cause &amp; fix {saved ? <Badge tone="ok">recorded</Badge> : required && <Badge tone="warn">needed to complete</Badge>}</div>
        <ErrorNote error={error} />
        {editable ? (
          <div className="grid gap-3">
            <Field label="Failure code" hint="Short tag to group the same failure, e.g. SEAL-LEAK">
              <Input list="failure-codes" value={form.failureCode} maxLength={40} onChange={(e) => setForm({ ...form, failureCode: e.target.value })} />
              <datalist id="failure-codes">{codes.map((c) => <option key={c} value={c} />)}</datalist>
            </Field>
            <Field label="What was wrong (cause)"><textarea className="w-full rounded-lg border border-line bg-panel px-3 py-2 text-sm" rows={2} maxLength={500} value={form.cause} onChange={(e) => setForm({ ...form, cause: e.target.value })} /></Field>
            <Field label="What fixed it"><textarea className="w-full rounded-lg border border-line bg-panel px-3 py-2 text-sm" rows={3} maxLength={1000} value={form.fix} onChange={(e) => setForm({ ...form, fix: e.target.value })} /></Field>
            <div className="flex justify-end"><Button variant="outline" disabled={!dirty || !form.cause.trim() || !form.fix.trim()} onClick={save}>Save cause &amp; fix</Button></div>
          </div>
        ) : saved ? (
          <div className="space-y-1">
            {saved.failureCode && <Badge tone="info">{saved.failureCode}</Badge>}
            <div><span className="text-muted">Cause:</span> {saved.cause}</div>
            <div><span className="text-muted">Fix:</span> {saved.fix}</div>
            <div className="text-xs text-muted">{day(saved.recordedAt)}{saved.recordedBy && ` · ${saved.recordedBy}`}</div>
          </div>
        ) : <p className="text-muted">Not recorded.</p>}
      </div>
    </div>
  );
}

/** Searchable library of every recorded fix. */
export function FixLibrary() {
  const [q, setQ] = useState("");
  const [fixes, setFixes] = useState<PastFix[]>([]);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    const t = setTimeout(() => api<{ fixes: PastFix[] }>(`/api/v1/maintenance/knowledge${q ? `?q=${encodeURIComponent(q)}` : ""}`)
      .then((k) => { setFixes(k.fixes); setError(null); }).catch((e) => setError(errorText(e))), 250);
    return () => clearTimeout(t);
  }, [q]);
  return (
    <Card title={`Fix library: ${fixes.length} recorded ${fixes.length === 1 ? "fix" : "fixes"}`}
      actions={<div className="relative"><Search size={14} className="absolute left-2 top-2.5 text-muted" /><Input className="pl-7" placeholder="Search cause, fix, code, machine" value={q} onChange={(e) => setQ(e.target.value)} /></div>}>
      <ErrorNote error={error} />
      <p className="mb-3 text-sm text-muted">Every breakdown closed with its cause and fix lands here. When the same alarm or problem comes back, the work order shows these fixes first.</p>
      <Table head={["Problem", "Machine", "Code", "Cause", "Fix", "Recorded", "Reused"]} empty={fixes.length === 0}>
        {fixes.map((f) => (
          <tr key={f.maintWoId} className="align-top">
            <td className="px-3 py-2"><div className="font-medium">{f.title}</div><div className="font-mono text-[11px] text-muted">{f.code}</div></td>
            <td className="px-3 py-2"><Link href={`/app/plant/assets/${f.assetId}`} className="hover:text-brand">{f.assetName}</Link></td>
            <td className="px-3 py-2">{f.failureCode ? <Badge tone="info">{f.failureCode}</Badge> : "—"}</td>
            <td className="max-w-64 px-3 py-2 text-sm">{f.cause}</td>
            <td className="max-w-80 px-3 py-2 text-sm">{f.fix}</td>
            <td className="whitespace-nowrap px-3 py-2 text-xs">{day(f.recordedAt)}<div className="text-muted">{f.recordedBy}</div></td>
            <td className="px-3 py-2 tabular-nums">{f.helpful}</td>
          </tr>
        ))}
      </Table>
    </Card>
  );
}
