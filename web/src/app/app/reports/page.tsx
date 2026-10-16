"use client";

import { useCallback, useEffect, useState } from "react";
import { Download, Mail, Printer, Trash2 } from "lucide-react";
import { api, hubToken, API_URL } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { PERIODS, periodQuery } from "@/components/charts";
import { Badge, Button, Card, ErrorNote, Field, Input, PageHeader, Select, Table, cx, errorText } from "@/components/ui";

type Report = { type: string; title: string; from: string; to: string; figures: { label: string; value: string }[]; columns: string[]; rows: string[][]; notes: string[] };
type Schedule = { scheduleId: string; reportType: string; recipients: string; localHour: number; lastSentOn: string | null; isActive: boolean };

const types = [
  { key: "production", label: "Production & OEE" },
  { key: "energy", label: "Energy" },
  { key: "maintenance", label: "Maintenance" },
  { key: "quality", label: "Quality" },
  { key: "alarms", label: "Alarms" },
];

/** Every report on one page: pick a type and period, read it, print it (save as PDF) or download a CSV for Excel. */
export default function ReportsPage() {
  const { can } = useAuth();
  const [type, setType] = useState("production");
  const [period, setPeriod] = useState("24h");
  const [report, setReport] = useState<Report | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    setBusy(true);
    api<Report>(`/api/v1/reports/${type}?${periodQuery(period)}`).then((r) => { setReport(r); setError(null); }).catch((e) => setError(errorText(e))).finally(() => setBusy(false));
  }, [type, period]);
  useEffect(load, [load]);

  async function downloadCsv() {
    // fetch with the access token (a plain link cannot send it), then save the file
    const res = await fetch(`${API_URL}/api/v1/reports/${type}?${periodQuery(period)}&format=csv`, { headers: { Authorization: `Bearer ${await hubToken()}` } });
    if (!res.ok) return setError(`Download failed (${res.status})`);
    const blob = await res.blob();
    const name = /filename="?([^";]+)/.exec(res.headers.get("Content-Disposition") ?? "")?.[1] ?? `${type}.csv`;
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = name;
    a.click();
  }

  return (
    <>
      <PageHeader title="Reports" sub="Read, print (save as PDF) or download any report as CSV for Excel. Schedule a daily e-mail below."
        actions={
          <div className="flex flex-wrap gap-2 print:hidden">
            <Select value={type} onChange={(e) => setType(e.target.value)} className="w-44 py-1" aria-label="Report">{types.map((t) => <option key={t.key} value={t.key}>{t.label}</option>)}</Select>
            <Select value={period} onChange={(e) => setPeriod(e.target.value)} className="w-40 py-1" aria-label="Period">{PERIODS.map((p) => <option key={p.key} value={p.key}>{p.label}</option>)}</Select>
            <Button variant="outline" className="py-1" onClick={() => window.print()}><Printer size={14} /> Print / PDF</Button>
            <Button className="py-1" onClick={downloadCsv}><Download size={14} /> CSV</Button>
          </div>
        } />
      <ErrorNote error={error} />
      {report && (
        <div className={cx(busy && "opacity-60")}>
          <Card title={`${report.title} · ${new Date(report.from + (report.from.endsWith("Z") ? "" : "Z")).toLocaleString()} – ${new Date(report.to + (report.to.endsWith("Z") ? "" : "Z")).toLocaleString()}`} className="mb-4">
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6">
              {report.figures.map((f) => <div key={f.label} className="rounded-lg bg-panel-2 p-3"><div className="text-xs text-muted">{f.label}</div><div className="text-lg font-semibold">{f.value}</div></div>)}
            </div>
            {report.notes.length > 0 && <ul className="mt-4 list-disc space-y-1 pl-5 text-sm">{report.notes.map((n) => <li key={n}>{n}</li>)}</ul>}
          </Card>
          <Card>
            <Table head={report.columns} empty={report.rows.length === 0}>
              {report.rows.map((r, i) => <tr key={i}>{r.map((c, j) => <td key={j} className={cx("px-3 py-2 text-sm", j > 0 && /^-?[\d.,]+%?$/.test(c) && "tabular-nums")}>{c || "—"}</td>)}</tr>)}
            </Table>
          </Card>
        </div>
      )}
      {can("settings.manage") && <Schedules />}
    </>
  );
}

function Schedules() {
  const [rows, setRows] = useState<Schedule[]>([]);
  const [form, setForm] = useState({ reportType: "production", recipients: "", localHour: "7" });
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(() => api<Schedule[]>("/api/v1/reports/schedules").then(setRows).catch((e) => setError(errorText(e))), []);
  useEffect(() => { load(); }, [load]);

  async function add(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api("/api/v1/reports/schedules", { method: "POST", json: { ...form, localHour: Number(form.localHour) } });
      setForm({ ...form, recipients: "" });
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }
  async function remove(id: string) {
    await api(`/api/v1/reports/schedules/${id}`, { method: "DELETE" }).catch((e) => setError(errorText(e)));
    load();
  }

  return (
    <Card title={<span className="flex items-center gap-2"><Mail size={16} /> Daily e-mail reports</span>} className="mt-6 print:hidden">
      <ErrorNote error={error} />
      <form onSubmit={add} className="mb-4 grid gap-3 sm:grid-cols-[180px_1fr_120px_auto] sm:items-end">
        <Field label="Report"><Select value={form.reportType} onChange={(e) => setForm({ ...form, reportType: e.target.value })}>{types.map((t) => <option key={t.key} value={t.key}>{t.label}</option>)}</Select></Field>
        <Field label="Send to" hint="Comma separated"><Input required value={form.recipients} onChange={(e) => setForm({ ...form, recipients: e.target.value })} placeholder="plant.manager@company.com" /></Field>
        <Field label="At (local hour)"><Input type="number" min="0" max="23" value={form.localHour} onChange={(e) => setForm({ ...form, localHour: e.target.value })} /></Field>
        <Button type="submit">Add</Button>
      </form>
      <Table head={["Report", "Recipients", "Time", "Last sent", ""]} empty={rows.length === 0}>
        {rows.map((s) => (
          <tr key={s.scheduleId}>
            <td className="px-3 py-2">{types.find((t) => t.key === s.reportType)?.label}</td>
            <td className="px-3 py-2 text-sm">{s.recipients}</td>
            <td className="px-3 py-2 tabular-nums">{String(s.localHour).padStart(2, "0")}:00</td>
            <td className="px-3 py-2 text-xs">{s.lastSentOn ?? <Badge tone="muted">not yet</Badge>}</td>
            <td className="px-3 py-2 text-right"><Button variant="ghost" className="py-1 text-bad" onClick={() => remove(s.scheduleId)} aria-label="Delete schedule"><Trash2 size={14} /></Button></td>
          </tr>
        ))}
      </Table>
      <p className="mt-3 text-xs text-muted">Each e-mail carries the previous day&apos;s key figures. E-mail needs an SMTP server in the API settings (Smtp:Host); otherwise messages are recorded as skipped.</p>
    </Card>
  );
}
