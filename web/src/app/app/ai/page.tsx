"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { BrainCircuit, CheckCircle2, Play, ThumbsDown, ThumbsUp, X } from "lucide-react";
import { api } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { ago } from "@/lib/types";
import { Badge, Button, Card, ErrorNote, Input, Modal, PageHeader, Select, Stat, Table, errorText } from "@/components/ui";

type Insight = {
  insightId: number; assetId: string; assetName: string; tagId: string | null; tagName: string | null; kind: string; severity: "Info" | "Warning" | "Critical";
  title: string; detail: string | null; score: number; source: string; detectedAt: string; lastSeenAt: string; closedAt: string | null; feedback: string | null;
  feedbackNote: string | null;
};
type Tuning = { tagKey: string; kind: string; sensitivity: number; realCount: number; falseCount: number; name: string | null };
type Summary = { open: number; critical: number; warning: number; confirmed: number; falseAlarms: number; insights: Insight[]; tuning: Tuning[] };

const KINDS: Record<string, string> = {
  EnergyAnomaly: "Energy anomaly", MeterFlat: "Meter stopped", Drift: "Drift", Frozen: "Frozen sensor", TimeToLimit: "Time to limit", IdleWaste: "Idle waste", Spike: "Spike",
};
const sevTone = { Critical: "bad", Warning: "warn", Info: "info" } as const;

/** What the OT detectors found, in the cloud and on edge agents, and the operators' feedback that tunes them. */
export default function AiPage() {
  const { can } = useAuth();
  const [data, setData] = useState<Summary | null>(null);
  const [all, setAll] = useState(false);
  const [kind, setKind] = useState("all");
  const [error, setError] = useState<string | null>(null);
  const [fb, setFb] = useState<{ insight: Insight; verdict: "Real" | "False"; note: string } | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    api<Summary>(`/api/v1/ai/insights${all ? "?all=true" : ""}`).then((d) => { setData(d); setError(null); }).catch((e) => setError(errorText(e)));
  }, [all]);
  useEffect(() => {
    load();
    const t = setInterval(load, 60_000);
    return () => clearInterval(t);
  }, [load]);

  async function act(fn: () => Promise<unknown>) {
    setError(null);
    try { await fn(); load(); } catch (e) { setError(errorText(e)); }
  }

  async function runNow() {
    setBusy(true);
    await act(() => api("/api/v1/ai/run", { method: "POST" }));
    setBusy(false);
  }

  const shown = (data?.insights ?? []).filter((i) => kind === "all" || i.kind === kind);
  return (
    <>
      <PageHeader title="OT AI insights" sub="Detectors that learn each signal's own normal from its history — energy per hour of the week, drift, frozen sensors, time to a limit, idle waste — in the cloud and on edge agents next to the machines."
        actions={can("alarms.manage") && <Button className="py-1" onClick={runNow} disabled={busy}><Play size={14} /> {busy ? "Running…" : "Run detectors now"}</Button>} />
      <ErrorNote error={error} />
      <div className="mb-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <Stat label="Open insights" value={data?.open ?? "—"} sub={data ? `${data.critical} critical · ${data.warning} warning` : ""} icon={<BrainCircuit size={16} />} />
        <Stat label="Confirmed by operators" value={data?.confirmed ?? "—"} sub="marked as a real problem" icon={<ThumbsUp size={16} className="text-ok" />} />
        <Stat label="False alarms" value={data?.falseAlarms ?? "—"} sub="each one makes that detector less eager on that signal" icon={<ThumbsDown size={16} />} />
        <Stat label="Tuned signals" value={data?.tuning.length ?? "—"} sub="sensitivity learnt from feedback" icon={<CheckCircle2 size={16} />} />
      </div>
      <Card title={`${shown.length} insight(s)`} className="mb-6" actions={
        <span className="flex gap-2">
          <Select value={kind} onChange={(e) => setKind(e.target.value)} className="w-40 py-1" aria-label="Kind"><option value="all">All kinds</option>{Object.entries(KINDS).map(([k, l]) => <option key={k} value={k}>{l}</option>)}</Select>
          <Select value={all ? "all" : "open"} onChange={(e) => setAll(e.target.value === "all")} className="w-32 py-1" aria-label="State"><option value="open">Open</option><option value="all">Open + closed</option></Select>
        </span>
      }>
        <Table head={["", "Finding", "Asset · signal", "Kind", "Source", "Detected", "Feedback"]} empty={shown.length === 0}>
          {shown.map((i) => (
            <tr key={i.insightId} className={i.closedAt ? "opacity-60" : ""}>
              <td className="px-3 py-2"><Badge tone={sevTone[i.severity]}>{i.severity}</Badge></td>
              <td className="max-w-xl px-3 py-2"><div className="font-medium">{i.title}</div>{i.detail && <div className="text-xs text-muted">{i.detail}</div>}</td>
              <td className="px-3 py-2 text-xs"><Link href={`/app/plant/assets/${i.assetId}`} className="hover:text-brand">{i.assetName}</Link>{i.tagName ? <div className="text-muted">{i.tagName}</div> : null}</td>
              <td className="px-3 py-2 text-xs">{KINDS[i.kind] ?? i.kind}<div className="text-muted">score {i.score}</div></td>
              <td className="px-3 py-2 text-xs"><Badge tone={i.source === "cloud" ? "muted" : "brand"}>{i.source}</Badge></td>
              <td className="whitespace-nowrap px-3 py-2 text-xs text-muted">{ago(i.detectedAt)}{i.closedAt ? " · closed" : ""}</td>
              <td className="px-3 py-2">
                {i.feedback ? <Badge tone={i.feedback === "Real" ? "ok" : "muted"}>{i.feedback === "Real" ? "real" : "false alarm"}</Badge>
                  : can("alarms.acknowledge") && !i.closedAt ? (
                    <span className="flex gap-1">
                      <Button variant="ghost" className="py-1" title="A real problem" onClick={() => setFb({ insight: i, verdict: "Real", note: "" })}><ThumbsUp size={14} /></Button>
                      <Button variant="ghost" className="py-1" title="False alarm" onClick={() => setFb({ insight: i, verdict: "False", note: "" })}><ThumbsDown size={14} /></Button>
                      <Button variant="ghost" className="py-1" title="Close" onClick={() => act(() => api(`/api/v1/ai/insights/${i.insightId}/close`, { method: "POST" }))}><X size={14} /></Button>
                    </span>
                  ) : <span className="text-xs text-muted">—</span>}
              </td>
            </tr>
          ))}
        </Table>
      </Card>
      <Card title="Learnt sensitivity per signal (1 = default; false alarms raise the threshold up to 3×, confirmed problems lower it to 0.7×)">
        <Table head={["Signal", "Detector", "Sensitivity", "Real", "False"]} empty={(data?.tuning.length ?? 0) === 0}>
          {(data?.tuning ?? []).map((t) => (
            <tr key={t.tagKey + t.kind}>
              <td className="px-3 py-2">{t.name ?? t.tagKey.slice(0, 8)}</td>
              <td className="px-3 py-2">{KINDS[t.kind] ?? t.kind}</td>
              <td className="px-3 py-2 tabular-nums">{t.sensitivity.toFixed(2)}×</td>
              <td className="px-3 py-2 tabular-nums">{t.realCount}</td>
              <td className="px-3 py-2 tabular-nums">{t.falseCount}</td>
            </tr>
          ))}
        </Table>
      </Card>
      <Modal open={!!fb} title={fb?.verdict === "Real" ? "Confirm a real problem" : "Mark as a false alarm"} onClose={() => setFb(null)}>
        {fb && (
          <div className="space-y-3">
            <p className="text-sm">{fb.insight.title}</p>
            <Input value={fb.note} onChange={(e) => setFb({ ...fb, note: e.target.value })} placeholder={fb.verdict === "Real" ? "What was found (e.g. bearing replaced)" : "Why it was normal (e.g. planned cleaning)"} />
            <div className="flex justify-end"><Button onClick={() => { const f = fb; setFb(null); act(() => api(`/api/v1/ai/insights/${f.insight.insightId}/feedback`, { method: "POST", json: { verdict: f.verdict, note: f.note || null } })); }}>Save</Button></div>
          </div>
        )}
      </Modal>
    </>
  );
}
