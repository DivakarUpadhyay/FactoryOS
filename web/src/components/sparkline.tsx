"use client";

/** A small line chart of recent values. Gaps (null values) break the line. */
export function Sparkline({ points, height = 36 }: { points: { t: number; v: number | null }[]; height?: number }) {
  const valid = points.filter((p) => p.v !== null) as { t: number; v: number }[];
  if (valid.length < 2) {
    return <div className="flex items-center text-[10px] text-muted" style={{ height }}>collecting history…</div>;
  }
  const w = 200;
  const t0 = valid[0].t;
  const t1 = valid[valid.length - 1].t || t0 + 1;
  let min = Math.min(...valid.map((p) => p.v));
  let max = Math.max(...valid.map((p) => p.v));
  if (max - min < 1e-9) {
    min -= 1;
    max += 1;
  }
  const x = (t: number) => ((t - t0) / Math.max(1, t1 - t0)) * w;
  const y = (v: number) => height - 3 - ((v - min) / (max - min)) * (height - 6);
  let d = "";
  let pen = false;
  for (const p of points) {
    if (p.v === null) {
      pen = false;
      continue;
    }
    d += `${pen ? "L" : "M"}${x(p.t).toFixed(1)},${y(p.v).toFixed(1)} `;
    pen = true;
  }
  return (
    <svg viewBox={`0 0 ${w} ${height}`} preserveAspectRatio="none" className="w-full" style={{ height }} aria-hidden>
      <path d={d} fill="none" stroke="var(--brand)" strokeWidth={1.6} vectorEffect="non-scaling-stroke" />
    </svg>
  );
}
