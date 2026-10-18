"use client";

import { useCallback, useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { ChevronLeft, ClipboardCheck, Factory, LogOut, Minus, Plus, TimerOff } from "lucide-react";
import { api } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { Button, ErrorNote, cx, errorText } from "@/components/ui";
import { InspDue, InspPlan, Perform } from "@/components/inspections";

type Machine = { assetId: string; name: string; lineName: string | null; status: string; countsParts: boolean; goodToday: number; scrapToday: number;
  pendingDowntimes: number; workOrderId: string | null; workOrderCode: string | null; productName: string | null };
type Home = { since: string; machines: Machine[]; scrapReasons: string[] };
type Entry = { entryId: number; good: number; scrap: number; scrapReason: string | null; note: string | null; enteredBy: string | null; enteredAt: string };
type Downtime = { downtimeId: number; assetId: string; assetName: string; startTs: string; endTs: string | null; seconds: number; state: string; reasonCode: string | null };
type Reason = { reasonCode: string; name: string; category: string };
type View = "machines" | "downtime" | "inspections";

const dot: Record<string, string> = { Running: "bg-ok", Idle: "bg-warn", Fault: "bg-bad", Stopped: "bg-bad", Offline: "bg-muted", Unknown: "bg-muted" };
const time = (iso: string) => new Date(iso.endsWith("Z") ? iso : iso + "Z").toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
const mins = (s: number) => (s < 3600 ? `${Math.round(s / 60)} min` : `${(s / 3600).toFixed(1)} h`);

/** Shop-floor app for phones and tablets: hand counts, downtime reasons, inspections. Installable (PWA). */
export default function OperatorApp() {
  const { me, loading, logout, can } = useAuth();
  const router = useRouter();
  const [view, setView] = useState<View>("machines");
  const [home, setHome] = useState<Home | null>(null);
  const [downtimes, setDowntimes] = useState<Downtime[]>([]);
  const [due, setDue] = useState<InspDue[]>([]);
  const [machine, setMachine] = useState<Machine | null>(null);
  const [toast, setToast] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!loading && !me) router.replace("/login?next=/op");
  }, [loading, me, router]);
  useEffect(() => {
    if ("serviceWorker" in navigator) navigator.serviceWorker.register("/sw.js").catch(() => undefined);
  }, []);

  const load = useCallback(() => {
    const from = new Date(Date.now() - 2 * 86400_000).toISOString();
    Promise.all([
      api<Home>("/api/v1/production/operator"),
      api<Downtime[]>(`/api/v1/production/downtimes?pending=true&from=${from}`).catch(() => []),
      api<InspDue[]>("/api/v1/quality/inspections/due").catch(() => []),
    ]).then(([h, d, i]) => { setHome(h); setDowntimes(d.filter((x) => !x.reasonCode)); setDue(i); setError(null); }).catch((e) => setError(errorText(e)));
  }, []);
  useEffect(() => {
    if (!me) return;
    load();
    const t = setInterval(load, 15_000);
    return () => clearInterval(t);
  }, [me, load]);
  useEffect(() => {
    if (!toast) return;
    const t = setTimeout(() => setToast(null), 3500);
    return () => clearTimeout(t);
  }, [toast]);

  if (loading || !me) return <div className="grid min-h-screen place-items-center text-sm text-muted">Loading…</div>;
  const done = (msg: string) => { setToast(msg); setMachine(null); load(); };

  return (
    <div className="mx-auto flex min-h-screen max-w-xl flex-col bg-bg pb-20">
      <header className="sticky top-0 z-10 flex items-center gap-2 bg-brand px-4 py-3 text-white" style={{ paddingTop: "max(0.75rem, env(safe-area-inset-top))" }}>
        {machine ? <button aria-label="Back" onClick={() => setMachine(null)} className="-ml-1 rounded p-1"><ChevronLeft size={22} /></button> : <Factory size={20} />}
        <div className="min-w-0 flex-1">
          <div className="truncate font-semibold">{machine ? machine.name : "Shop floor"}</div>
          <div className="truncate text-xs opacity-80">{me.displayName} · {me.tenantName}</div>
        </div>
        <button aria-label="Sign out" onClick={() => logout().then(() => router.replace("/login?next=/op"))} className="rounded p-1"><LogOut size={18} /></button>
      </header>
      <main className="flex-1 space-y-3 p-3">
        <ErrorNote error={error} />
        {toast && <div role="status" className="rounded-lg bg-ok px-3 py-2 text-sm font-medium text-white">{toast}</div>}
        {machine && home ? <CountSheet machine={machine} reasons={home.scrapReasons} canOperate={can("production.operate")} onDone={done} /> : (
          <>
            {view === "machines" && home && <Machines list={home.machines} onOpen={setMachine} />}
            {view === "downtime" && <DowntimeList list={downtimes} canExplain={can("production.operate")} onDone={done} />}
            {view === "inspections" && <InspectionList list={due} canInspect={can("quality.execute")} onDone={done} />}
          </>
        )}
      </main>
      {!machine && (
        <nav className="fixed inset-x-0 bottom-0 z-10 mx-auto grid max-w-xl grid-cols-3 border-t border-line bg-panel" style={{ paddingBottom: "env(safe-area-inset-bottom)" }}>
          {([["machines", "Counts", Factory, 0], ["downtime", "Downtime", TimerOff, downtimes.length], ["inspections", "Inspections", ClipboardCheck, due.length]] as const).map(([v, label, Icon, n]) => (
            <button key={v} onClick={() => setView(v)} className={cx("relative flex flex-col items-center gap-0.5 py-2 text-xs", view === v ? "text-brand" : "text-muted")}>
              <Icon size={22} />{label}
              {n > 0 && <span className="absolute right-[30%] top-1 min-w-5 rounded-full bg-bad px-1 text-[11px] font-semibold text-white">{n}</span>}
            </button>
          ))}
        </nav>
      )}
    </div>
  );
}

function Machines({ list, onOpen }: { list: Machine[]; onOpen: (m: Machine) => void }) {
  if (list.length === 0) return <p className="p-6 text-center text-sm text-muted">No production machines yet.</p>;
  return (
    <ul className="space-y-2">
      {list.map((m) => (
        <li key={m.assetId}>
          <button onClick={() => onOpen(m)} className="flex w-full items-center gap-3 rounded-xl border border-line bg-panel p-3 text-left active:bg-panel-2">
            <span className={cx("h-3 w-3 shrink-0 rounded-full", dot[m.status] ?? "bg-muted")} title={m.status} />
            <div className="min-w-0 flex-1">
              <div className="truncate font-medium">{m.name}</div>
              <div className="truncate text-xs text-muted">{m.workOrderCode ? `${m.workOrderCode} · ${m.productName}` : m.lineName ?? m.status}</div>
            </div>
            <div className="text-right text-sm tabular-nums">
              <div className="font-semibold text-ok">{m.goodToday.toLocaleString()}</div>
              <div className="text-xs text-bad">{m.scrapToday > 0 ? `${m.scrapToday} NG` : ""}</div>
            </div>
          </button>
        </li>
      ))}
    </ul>
  );
}

function Stepper({ label, value, onChange, tone }: { label: string; value: number; onChange: (n: number) => void; tone: "ok" | "bad" }) {
  const set = (n: number) => onChange(Math.max(0, Math.min(100_000, n)));
  return (
    <div className="rounded-xl border border-line bg-panel p-3">
      <div className="mb-2 text-sm font-medium">{label}</div>
      <div className="flex items-center gap-2">
        <button type="button" aria-label={`${label} minus one`} onClick={() => set(value - 1)} className="grid h-14 w-14 place-items-center rounded-xl bg-panel-2 active:scale-95"><Minus /></button>
        <input aria-label={label} inputMode="numeric" value={value || ""} placeholder="0" onChange={(e) => set(Number(e.target.value.replace(/\D/g, "")) || 0)}
          className={cx("h-14 min-w-0 flex-1 rounded-xl border border-line bg-bg text-center text-3xl font-semibold tabular-nums", tone === "ok" ? "text-ok" : "text-bad")} />
        <button type="button" aria-label={`${label} plus one`} onClick={() => set(value + 1)} className="grid h-14 w-14 place-items-center rounded-xl bg-panel-2 active:scale-95"><Plus /></button>
      </div>
      <div className="mt-2 flex gap-2">{[5, 10, 50].map((n) => <button key={n} type="button" onClick={() => set(value + n)} className="flex-1 rounded-lg bg-panel-2 py-2 text-sm">+{n}</button>)}</div>
    </div>
  );
}

function CountSheet({ machine, reasons, canOperate, onDone }: { machine: Machine; reasons: string[]; canOperate: boolean; onDone: (msg: string) => void }) {
  const [good, setGood] = useState(0);
  const [scrap, setScrap] = useState(0);
  const [reason, setReason] = useState<string | null>(null);
  const [note, setNote] = useState("");
  const [entries, setEntries] = useState<Entry[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { api<Entry[]>(`/api/v1/production/operator/entries?assetId=${machine.assetId}`).then(setEntries).catch(() => undefined); }, [machine.assetId]);

  async function submit() {
    setBusy(true);
    try {
      await api("/api/v1/production/operator/counts", { method: "POST", json: { assetId: machine.assetId, good, scrap, scrapReason: scrap > 0 ? reason : null, note: note || null } });
      onDone(`${machine.name}: ${good} good${scrap ? `, ${scrap} scrap` : ""} saved.`);
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  }
  return (
    <div className="space-y-3">
      <div className="grid grid-cols-2 gap-2 text-center">
        <div className="rounded-xl bg-panel p-3"><div className="text-2xl font-semibold text-ok tabular-nums">{machine.goodToday.toLocaleString()}</div><div className="text-xs text-muted">good today</div></div>
        <div className="rounded-xl bg-panel p-3"><div className="text-2xl font-semibold text-bad tabular-nums">{machine.scrapToday.toLocaleString()}</div><div className="text-xs text-muted">scrap today</div></div>
      </div>
      {machine.countsParts && <p className="rounded-lg bg-info/10 px-3 py-2 text-xs text-info">This machine counts good parts itself. Enter only parts it missed, and the scrap you find.</p>}
      <ErrorNote error={error} />
      {canOperate ? (
        <>
          <Stepper label="Good parts (OK)" value={good} onChange={setGood} tone="ok" />
          <Stepper label="Scrap parts (NG)" value={scrap} onChange={setScrap} tone="bad" />
          {scrap > 0 && (
            <div className="rounded-xl border border-line bg-panel p-3">
              <div className="mb-2 text-sm font-medium">Why scrapped?</div>
              <div className="flex flex-wrap gap-2">
                {reasons.map((r) => <button key={r} type="button" onClick={() => setReason(r)}
                  className={cx("rounded-full border px-3 py-1.5 text-sm", reason === r ? "border-bad bg-bad text-white" : "border-line bg-bg")}>{r}</button>)}
              </div>
            </div>
          )}
          <input aria-label="Note" placeholder="Note (optional)" maxLength={300} value={note} onChange={(e) => setNote(e.target.value)}
            className="h-12 w-full rounded-xl border border-line bg-panel px-3 text-sm" />
          <Button className="h-14 w-full text-base" disabled={busy || good + scrap === 0 || (scrap > 0 && !reason)} onClick={submit}>Save count</Button>
        </>
      ) : <p className="text-sm text-muted">You can view counts but not enter them.</p>}
      {entries.length > 0 && (
        <div className="rounded-xl border border-line bg-panel p-3">
          <div className="mb-2 text-sm font-medium">Entered today</div>
          <ul className="divide-y divide-line text-sm">
            {entries.map((e) => (
              <li key={e.entryId} className="flex justify-between gap-2 py-1.5">
                <span className="text-muted">{time(e.enteredAt)} · {e.enteredBy ?? "—"}</span>
                <span className="tabular-nums"><span className="text-ok">{e.good}</span>{e.scrap > 0 && <span className="text-bad"> / {e.scrap} NG ({e.scrapReason})</span>}</span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}

function DowntimeList({ list, canExplain, onDone }: { list: Downtime[]; canExplain: boolean; onDone: (msg: string) => void }) {
  const [reasons, setReasons] = useState<Reason[]>([]);
  const [open, setOpen] = useState<Downtime | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { api<Reason[]>("/api/v1/production/reasons").then(setReasons).catch(() => undefined); }, []);
  async function explain(d: Downtime, code: string) {
    try {
      await api(`/api/v1/production/downtimes/${d.downtimeId}/reason`, { method: "PUT", json: { reasonCode: code, comment: null } });
      setOpen(null);
      onDone(`${d.assetName}: reason saved.`);
    } catch (err) {
      setError(errorText(err));
    }
  }
  if (list.length === 0) return <p className="p-6 text-center text-sm text-muted">Every stop has a reason. Nothing to do.</p>;
  return (
    <div className="space-y-2">
      <ErrorNote error={error} />
      <p className="px-1 text-xs text-muted">Stops without a reason. Tap one and choose why the machine stopped.</p>
      {list.map((d) => (
        <div key={d.downtimeId} className="rounded-xl border border-line bg-panel">
          <button className="flex w-full items-center justify-between gap-2 p-3 text-left" onClick={() => setOpen(open?.downtimeId === d.downtimeId ? null : d)}>
            <div><div className="font-medium">{d.assetName}</div><div className="text-xs text-muted">{time(d.startTs)}{d.endTs ? `–${time(d.endTs)}` : " · still stopped"} · {d.state}</div></div>
            <span className={cx("text-sm font-semibold tabular-nums", d.endTs ? "text-warn" : "text-bad")}>{mins(d.seconds)}</span>
          </button>
          {open?.downtimeId === d.downtimeId && canExplain && (
            <div className="grid grid-cols-2 gap-2 border-t border-line p-3">
              {reasons.map((r) => <button key={r.reasonCode} onClick={() => explain(d, r.reasonCode)} className="rounded-lg bg-panel-2 px-2 py-3 text-sm active:bg-brand active:text-white">{r.name}</button>)}
            </div>
          )}
        </div>
      ))}
    </div>
  );
}

function InspectionList({ list, canInspect, onDone }: { list: InspDue[]; canInspect: boolean; onDone: (msg: string) => void }) {
  const [plans, setPlans] = useState<InspPlan[]>([]);
  const [open, setOpen] = useState<InspDue | null>(null);
  useEffect(() => { api<InspPlan[]>("/api/v1/quality/inspection-plans").then(setPlans).catch(() => undefined); }, []);
  const plan = open && plans.find((p) => p.planId === open.planId);
  if (open && plan) {
    return (
      <div className="space-y-3">
        <button onClick={() => setOpen(null)} className="flex items-center gap-1 text-sm text-brand"><ChevronLeft size={16} /> Due inspections</button>
        <div className="rounded-xl border border-line bg-panel p-3">
          <div className="font-medium">{plan.name}</div>
          <div className="mb-3 text-xs text-muted">{open.workOrderCode} · {open.productName} · {open.reason}</div>
          <Perform plan={plan} due={open} onDone={(msg) => { setOpen(null); onDone(msg); }} />
        </div>
      </div>
    );
  }
  if (list.length === 0) return <p className="p-6 text-center text-sm text-muted">No inspections due.</p>;
  return (
    <ul className="space-y-2">
      {list.map((d) => (
        <li key={`${d.planId}-${d.workOrderId}`}>
          <button disabled={!canInspect} onClick={() => setOpen(d)} className="w-full rounded-xl border border-line bg-panel p-3 text-left active:bg-panel-2">
            <div className="font-medium">{d.planName}</div>
            <div className="text-xs text-muted">{d.workOrderCode} · {d.productName} · {d.reason}</div>
          </button>
        </li>
      ))}
    </ul>
  );
}
