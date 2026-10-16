"use client";

import { cx } from "./ui";

export type Bar = { label: string; value: number; tone?: "brand" | "ok" | "warn" | "bad" | "info" | "muted"; title?: string };

const color: Record<string, string> = {
  brand: "var(--brand)", ok: "var(--ok)", warn: "var(--warn)", bad: "var(--bad)", info: "var(--info)", muted: "var(--muted)",
};

/** Vertical bar chart in SVG; scales to its container. Labels are thinned out when there are many bars. */
export function BarChart({ bars, height = 180, unit = "", limit }: { bars: Bar[]; height?: number; unit?: string; limit?: { value: number; label: string } }) {
  if (bars.length === 0) return <p className="py-8 text-center text-sm text-muted">No data in this period yet.</p>;
  const W = 600, pad = 28;
  const max = Math.max(...bars.map((b) => b.value), limit?.value ?? 0, 0.0001);
  const bw = (W - pad) / bars.length;
  const every = Math.ceil(bars.length / 12);
  const y = (v: number) => height - 18 - (v / max) * (height - 30);
  return (
    <svg viewBox={`0 0 ${W} ${height}`} className="w-full" role="img" aria-label="Bar chart">
      <text x={0} y={10} fontSize={9} fill="var(--muted)">{fmt(max)}{unit}</text>
      <line x1={pad} x2={W} y1={height - 18} y2={height - 18} stroke="var(--border)" />
      {bars.map((b, i) => (
        <g key={i}>
          <rect x={pad + i * bw + bw * 0.12} y={y(b.value)} width={bw * 0.76} height={Math.max(0, height - 18 - y(b.value))} rx={2}
            fill={color[b.tone ?? "brand"]}>
            <title>{b.title ?? `${b.label}: ${fmt(b.value)}${unit}`}</title>
          </rect>
          {i % every === 0 && <text x={pad + i * bw + bw / 2} y={height - 5} fontSize={9} textAnchor="middle" fill="var(--muted)">{b.label}</text>}
        </g>
      ))}
      {limit && (
        <g>
          <line x1={pad} x2={W} y1={y(limit.value)} y2={y(limit.value)} stroke="var(--bad)" strokeDasharray="5 3" />
          <text x={W - 2} y={y(limit.value) - 3} fontSize={9} textAnchor="end" fill="var(--bad)">{limit.label}</text>
        </g>
      )}
    </svg>
  );
}

/** Horizontal bars with labels and values: rankings such as energy by machine. */
export function HBars({ rows, unit = "", max: forced }: { rows: { label: string; value: number; sub?: string; tone?: Bar["tone"] }[]; unit?: string; max?: number }) {
  if (rows.length === 0) return <p className="py-6 text-center text-sm text-muted">No data in this period yet.</p>;
  const max = forced ?? Math.max(...rows.map((r) => r.value), 0.0001);
  return (
    <div className="space-y-2">
      {rows.map((r) => (
        <div key={r.label}>
          <div className="flex justify-between gap-2 text-sm">
            <span className="truncate">{r.label}</span>
            <span className="shrink-0 tabular-nums text-muted">{fmt(r.value)}{unit}{r.sub ? ` · ${r.sub}` : ""}</span>
          </div>
          <div className="mt-1 h-2 rounded-full bg-panel-2">
            <div className="h-2 rounded-full" style={{ width: `${Math.min(100, (r.value / max) * 100)}%`, background: color[r.tone ?? "brand"] }} />
          </div>
        </div>
      ))}
    </div>
  );
}

/** A ring showing a percentage, e.g. OEE or contract-capacity use. */
export function Ring({ pct, label, tone = "brand", size = 120 }: { pct: number | null; label: string; tone?: Bar["tone"]; size?: number }) {
  const r = 42, c = 2 * Math.PI * r;
  const p = Math.max(0, Math.min(100, pct ?? 0));
  return (
    <div className="flex flex-col items-center">
      <svg viewBox="0 0 100 100" width={size} height={size} role="img" aria-label={`${label} ${pct ?? "—"}%`}>
        <circle cx={50} cy={50} r={r} fill="none" stroke="var(--panel-2)" strokeWidth={10} />
        <circle cx={50} cy={50} r={r} fill="none" stroke={color[tone]} strokeWidth={10} strokeLinecap="round"
          strokeDasharray={`${(p / 100) * c} ${c}`} transform="rotate(-90 50 50)" />
        <text x={50} y={54} textAnchor="middle" fontSize={18} fontWeight={700} fill="var(--text)">{pct === null ? "—" : `${Math.round(pct)}%`}</text>
      </svg>
      <span className={cx("mt-1 text-xs text-muted")}>{label}</span>
    </div>
  );
}

export function fmt(v: number): string {
  if (Math.abs(v) >= 1000) return Math.round(v).toLocaleString();
  if (Math.abs(v) >= 10) return v.toFixed(1);
  return v.toFixed(2);
}

/** Quick period picker used by the analytics pages. */
export const PERIODS = [
  { key: "24h", label: "Last 24 hours", hours: 24 },
  { key: "7d", label: "Last 7 days", hours: 24 * 7 },
  { key: "30d", label: "Last 30 days", hours: 24 * 30 },
] as const;

export function periodQuery(key: string): string {
  const p = PERIODS.find((x) => x.key === key) ?? PERIODS[0];
  const to = new Date();
  const from = new Date(to.getTime() - p.hours * 3600_000);
  return `from=${from.toISOString()}&to=${to.toISOString()}`;
}
