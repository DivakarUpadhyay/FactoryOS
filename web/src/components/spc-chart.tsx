"use client";

export type SpcPoint = { subgroupNo: number; ts: string; mean: number; range: number; size: number; lotNo: string | null; violations: string[] };
export type SpcLimits = { centre: number; ucl: number; lcl: number; rangeCentre: number; rangeUcl: number; rangeLcl: number; sigma: number };

/** A control chart: points joined by a line, centre and control limits, optional specification limits; rule breaks in red. */
export function ControlChart({ points, value, centre, ucl, lcl, lsl, usl, label, height = 200 }: {
  points: SpcPoint[]; value: (p: SpcPoint) => number; centre: number; ucl: number; lcl: number; lsl?: number | null; usl?: number | null; label: string; height?: number;
}) {
  if (points.length === 0) return <p className="py-8 text-center text-sm text-muted">No measurements yet.</p>;
  const W = 640, padL = 44, padR = 60, padT = 10, padB = 18;
  const vals = points.map(value);
  const lines = [centre, ucl, lcl, ...(lsl != null ? [lsl] : []), ...(usl != null ? [usl] : [])];
  let lo = Math.min(...vals, ...lines), hi = Math.max(...vals, ...lines);
  const pad = (hi - lo) * 0.08 || 1;
  lo -= pad; hi += pad;
  const x = (i: number) => padL + (points.length === 1 ? (W - padL - padR) / 2 : (i / (points.length - 1)) * (W - padL - padR));
  const y = (v: number) => padT + (1 - (v - lo) / (hi - lo)) * (height - padT - padB);
  const fmt = (v: number) => (Math.abs(v) >= 100 ? v.toFixed(1) : Math.abs(v) >= 1 ? v.toFixed(3) : v.toFixed(4));
  const hline = (v: number, colour: string, dash: string, name: string) => (
    <g key={name}>
      <line x1={padL} x2={W - padR} y1={y(v)} y2={y(v)} stroke={colour} strokeDasharray={dash} strokeWidth={1} />
      <text x={W - padR + 4} y={y(v) + 3} fontSize={9} fill={colour}>{name} {fmt(v)}</text>
    </g>
  );
  return (
    <svg viewBox={`0 0 ${W} ${height}`} className="w-full" role="img" aria-label={label}>
      <text x={2} y={12} fontSize={9} fill="var(--muted)">{label}</text>
      {usl != null && hline(usl, "var(--bad)", "2 3", "USL")}
      {lsl != null && hline(lsl, "var(--bad)", "2 3", "LSL")}
      {hline(ucl, "var(--warn)", "6 3", "UCL")}
      {hline(lcl, "var(--warn)", "6 3", "LCL")}
      {hline(centre, "var(--ok)", "", "CL")}
      <polyline fill="none" stroke="var(--brand)" strokeWidth={1.4} points={vals.map((v, i) => `${x(i)},${y(v)}`).join(" ")} />
      {points.map((p, i) => (
        <circle key={i} cx={x(i)} cy={y(vals[i])} r={p.violations.length ? 3.6 : 2.4} fill={p.violations.length ? "var(--bad)" : "var(--brand)"}>
          <title>{`${new Date(p.ts.endsWith("Z") ? p.ts : p.ts + "Z").toLocaleString()} · ${fmt(vals[i])}${p.lotNo ? ` · lot ${p.lotNo}` : ""}${p.violations.length ? ` · ${p.violations.join(", ")}` : ""}`}</title>
        </circle>
      ))}
    </svg>
  );
}
