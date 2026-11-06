"use client";

import { useEffect, useRef, useState } from "react";
import { usePathname } from "next/navigation";
import { onBusy } from "@/lib/api";
import { LogoMark } from "./logo";

/**
 * Slim bar at the top of the window while pages load data: it appears only when loading takes longer than a moment
 * (quick polls never flash it), creeps towards the end, then completes and fades out.
 */
export function TopProgress() {
  const path = usePathname();
  const [state, setState] = useState<"idle" | "running" | "done">("idle");
  const showTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const hideTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const navigating = useRef(false);

  useEffect(() => {
    const start = () => {
      if (hideTimer.current) clearTimeout(hideTimer.current);
      if (!showTimer.current) showTimer.current = setTimeout(() => { showTimer.current = null; setState("running"); }, 180);
    };
    const finish = () => {
      if (showTimer.current) { clearTimeout(showTimer.current); showTimer.current = null; }
      setState((s) => (s === "running" ? "done" : s));
      hideTimer.current = setTimeout(() => setState("idle"), 350);
    };
    const off = onBusy((n) => {
      if (n > 0) start();
      else { navigating.current = false; finish(); }
    });
    // a page change starts the bar right away; the new page's requests keep it going
    navigating.current = true;
    start();
    const t = setTimeout(() => { if (navigating.current) { navigating.current = false; finish(); } }, 600);
    return () => { off(); clearTimeout(t); };
  }, [path]);

  return (
    <div aria-hidden className="pointer-events-none fixed inset-x-0 top-0 z-[60] h-0.5">
      <div
        className="h-full bg-gradient-to-r from-brand to-brand-2 shadow-[0_0_8px_var(--brand)]"
        style={{
          width: state === "idle" ? "0%" : state === "running" ? "85%" : "100%",
          opacity: state === "idle" ? 0 : 1,
          transition: state === "running" ? "width 8s cubic-bezier(.1,.7,.2,1), opacity .15s" : state === "done" ? "width .2s ease-out, opacity .3s .15s" : "none",
        }}
      />
    </div>
  );
}

/** What the signed-in layout looks like while the session is restored: the real frame with soft placeholders, no blank page. */
export function ShellSkeleton() {
  return (
    <div className="min-h-screen lg:grid lg:grid-cols-[256px_1fr]" aria-busy="true" aria-label="Loading Divakar FactoryOS">
      <aside className="hidden border-r border-line bg-panel lg:block">
        <div className="flex h-14 items-center gap-2 border-b border-line px-4">
          <LogoMark className="h-8 w-8 shrink-0" />
          <div className="leading-tight">
            <div className="text-sm font-semibold">Divakar FactoryOS</div>
            <div className="skeleton mt-1 h-2.5 w-24 rounded" />
          </div>
        </div>
        <div className="space-y-2 p-3">
          {Array.from({ length: 14 }, (_, i) => (
            <div key={i} className="flex items-center gap-3 px-2 py-1.5">
              <div className="skeleton h-4 w-4 rounded" />
              <div className="skeleton h-3 rounded" style={{ width: `${45 + ((i * 37) % 40)}%` }} />
            </div>
          ))}
        </div>
      </aside>
      <div className="min-w-0">
        <header className="flex h-14 items-center gap-3 border-b border-line bg-panel/80 px-4">
          <div className="lg:hidden"><LogoMark className="h-7 w-7" /></div>
          <div className="skeleton h-3 w-28 rounded" />
          <div className="flex-1" />
          <div className="skeleton h-6 w-6 rounded-full" />
          <div className="skeleton h-6 w-20 rounded" />
        </header>
        <main className="mx-auto max-w-7xl p-4 md:p-6">
          <div className="skeleton mb-2 h-6 w-56 rounded" />
          <div className="skeleton mb-6 h-3 w-80 max-w-full rounded" />
          <div className="mb-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
            {Array.from({ length: 4 }, (_, i) => <div key={i} className="skeleton h-24 rounded-xl" />)}
          </div>
          <div className="grid gap-4 lg:grid-cols-3">
            <div className="skeleton h-64 rounded-xl lg:col-span-2" />
            <div className="skeleton h-64 rounded-xl" />
          </div>
        </main>
      </div>
    </div>
  );
}

/** Full-screen branded loader for pages without the sidebar frame (start page, shop-floor app). */
export function BrandSplash({ label = "Divakar FactoryOS" }: { label?: string }) {
  return (
    <div className="grid min-h-dvh place-items-center bg-bg" aria-busy="true">
      <div className="flex flex-col items-center gap-4">
        <LogoMark className="h-14 w-14 animate-pulse" />
        <div className="text-sm font-semibold tracking-wide">{label}</div>
        <div className="h-1 w-40 overflow-hidden rounded-full bg-panel-2">
          <div className="splash-bar h-full w-1/3 rounded-full bg-gradient-to-r from-brand to-brand-2" />
        </div>
      </div>
    </div>
  );
}

/** A few shimmering lines where a table or chart is about to appear. */
export function SkeletonRows({ rows = 4 }: { rows?: number }) {
  return (
    <div className="space-y-2" aria-busy="true">
      {Array.from({ length: rows }, (_, i) => <div key={i} className="skeleton h-4 rounded" style={{ width: `${92 - ((i * 17) % 30)}%` }} />)}
    </div>
  );
}
