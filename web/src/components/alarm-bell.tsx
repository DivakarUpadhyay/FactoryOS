"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { Bell, X } from "lucide-react";
import { api } from "@/lib/api";
import { LiveAlarmMsg, useLive } from "@/lib/live";
import { cx } from "./ui";

type Summary = { active: number; critical: number; unacknowledged: number };

/** Open-alarm counter in the top bar, with a pop-up for each new alarm pushed by the server. */
export function AlarmBell() {
  const [s, setS] = useState<Summary | null>(null);
  const [toasts, setToasts] = useState<LiveAlarmMsg[]>([]);
  const load = useCallback(() => api<Summary>("/api/v1/alarms/summary").then(setS).catch(() => undefined), []);
  useEffect(() => {
    load();
    const t = setInterval(load, 20_000);
    return () => clearInterval(t);
  }, [load]);
  useLive({
    alarms: (msgs) => {
      load();
      setToasts((cur) => [...msgs.slice(-3), ...cur].slice(0, 4));
      msgs.forEach((m) => setTimeout(() => setToasts((cur) => cur.filter((t) => t.alarmId !== m.alarmId)), 10_000));
    },
  });

  return (
    <>
      <Link href="/app/alarms" className="relative rounded p-1.5 hover:bg-panel-2" aria-label={`Alarms: ${s?.active ?? 0} open`}>
        <Bell size={16} className={s?.critical ? "text-bad" : undefined} />
        {!!s?.active && (
          <span className={cx("absolute -right-1 -top-1 grid h-4 min-w-4 place-items-center rounded-full px-1 text-[10px] font-bold text-white", s.critical ? "bg-bad" : "bg-warn")}>
            {s.active > 99 ? "99+" : s.active}
          </span>
        )}
      </Link>
      <div className="fixed right-4 top-16 z-50 w-80 space-y-2" aria-live="polite">
        {toasts.map((t) => (
          <div key={t.alarmId} role="alert"
            className={cx("rounded-lg border bg-panel p-3 text-sm shadow-lg", t.severity === "Critical" ? "border-bad" : t.severity === "Warning" ? "border-warn" : "border-info")}>
            <div className="flex items-start gap-2">
              <span className={cx("mt-1 h-2 w-2 shrink-0 rounded-full", t.severity === "Critical" ? "bg-bad" : t.severity === "Warning" ? "bg-warn" : "bg-info")} />
              <Link href="/app/alarms" className="flex-1"><span className="font-medium">{t.severity}:</span> {t.message}</Link>
              <button onClick={() => setToasts((cur) => cur.filter((x) => x.alarmId !== t.alarmId))} aria-label="Dismiss"><X size={14} /></button>
            </div>
          </div>
        ))}
      </div>
    </>
  );
}
