"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { Move, Save } from "lucide-react";
import { api } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { useLive } from "@/lib/live";
import { statusTone } from "@/lib/types";
import { Badge, Button, Card, ErrorNote, LiveBadge, PageHeader, Select, errorText } from "@/components/ui";

type FloorAsset = {
  assetId: string; nodeId: string; areaName: string | null; lineName: string | null; code: string; name: string; assetTypeCode: string;
  category: string; status: keyof typeof statusTone; layoutX: number | null; layoutY: number | null; powerTagId: string | null;
  powerKw: number | null; ratedPowerKw: number | null;
};
type Placed = FloorAsset & { x: number; y: number; placed: boolean };

const W = 1000, H = 1000, BOX_W = 92, BOX_H = 52;
const fill: Record<string, string> = { ok: "var(--ok)", info: "var(--info)", warn: "var(--warn)", bad: "var(--bad)", muted: "var(--muted)" };

/** Assets without a saved position are laid out area by area, in a grid, so a new plant shows something useful at once. */
function autoLayout(assets: FloorAsset[]): Placed[] {
  const areas = [...new Set(assets.map((a) => a.areaName ?? "Plant"))];
  const cols = Math.ceil(Math.sqrt(areas.length));
  const zoneW = W / cols, zoneH = H / Math.ceil(areas.length / cols);
  return assets.map((a) => {
    if (a.layoutX !== null && a.layoutY !== null) return { ...a, x: a.layoutX, y: a.layoutY, placed: true };
    const z = areas.indexOf(a.areaName ?? "Plant");
    const inZone = assets.filter((b) => (b.areaName ?? "Plant") === (a.areaName ?? "Plant"));
    const i = inZone.indexOf(a);
    const perRow = Math.max(1, Math.floor((zoneW - 30) / (BOX_W + 12)));
    return {
      ...a, placed: false,
      x: Math.round((z % cols) * zoneW + 20 + (i % perRow) * (BOX_W + 12)),
      y: Math.round(Math.floor(z / cols) * zoneH + 44 + Math.floor(i / perRow) * (BOX_H + 14)),
    };
  });
}

export default function FloorPlanPage() {
  const { can } = useAuth();
  const router = useRouter();
  const canEdit = can("plant.manage");
  const [items, setItems] = useState<Placed[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [edit, setEdit] = useState(false);
  const [area, setArea] = useState("all");
  const [saved, setSaved] = useState<string | null>(null);
  const svg = useRef<SVGSVGElement>(null);
  const drag = useRef<{ id: string; dx: number; dy: number; moved: boolean } | null>(null);

  const load = useCallback(() => {
    api<FloorAsset[]>("/api/v1/plant/floor").then((r) => setItems(autoLayout(r))).catch((e) => setError(errorText(e)));
  }, []);
  useEffect(load, [load]);

  const hub = useLive({
    values: (msgs) => {
      const byTag = new Map(msgs.map((m) => [m.tagId, m.value]));
      setItems((cur) => cur.map((a) => (a.powerTagId && byTag.has(a.powerTagId) ? { ...a, powerKw: byTag.get(a.powerTagId)! } : a)));
    },
    status: (msgs) => setItems((cur) => cur.map((a) => {
      const m = msgs.find((x) => x.assetId === a.assetId);
      return m ? { ...a, status: m.status as Placed["status"] } : a;
    })),
  });

  const areas = useMemo(() => [...new Set(items.map((a) => a.areaName ?? "Plant"))], [items]);
  const shown = items.filter((a) => area === "all" || (a.areaName ?? "Plant") === area);
  const zones = useMemo(() => areas.map((name) => {
    const inZone = items.filter((a) => (a.areaName ?? "Plant") === name);
    const x1 = Math.min(...inZone.map((a) => a.x)) - 12, y1 = Math.min(...inZone.map((a) => a.y)) - 30;
    const x2 = Math.max(...inZone.map((a) => a.x + BOX_W)) + 12, y2 = Math.max(...inZone.map((a) => a.y + BOX_H)) + 12;
    return { name, x: x1, y: y1, w: x2 - x1, h: y2 - y1 };
  }), [areas, items]);
  const counts = items.reduce<Record<string, number>>((m, a) => ({ ...m, [a.status]: (m[a.status] ?? 0) + 1 }), {});
  const totalKw = items.reduce((s, a) => s + (a.category !== "Energy" && a.powerKw ? a.powerKw : 0), 0);

  function point(e: React.PointerEvent) {
    const r = svg.current!.getBoundingClientRect();
    return { x: ((e.clientX - r.left) / r.width) * W, y: ((e.clientY - r.top) / r.height) * H };
  }

  function down(e: React.PointerEvent, a: Placed) {
    if (!edit) return;
    const p = point(e);
    drag.current = { id: a.assetId, dx: p.x - a.x, dy: p.y - a.y, moved: false };
    (e.target as Element).setPointerCapture(e.pointerId);
  }

  function move(e: React.PointerEvent) {
    const d = drag.current;
    if (!d) return;
    const p = point(e);
    d.moved = true;
    const x = Math.round(Math.min(W - BOX_W, Math.max(0, p.x - d.dx))), y = Math.round(Math.min(H - BOX_H, Math.max(0, p.y - d.dy)));
    setItems((cur) => cur.map((a) => (a.assetId === d.id ? { ...a, x, y } : a)));
  }

  async function up() {
    const d = drag.current;
    drag.current = null;
    if (!d?.moved) return;
    const a = items.find((i) => i.assetId === d.id);
    if (!a) return;
    try {
      await api(`/api/v1/plant/assets/${a.assetId}/layout`, { method: "PUT", json: { x: a.x, y: a.y } });
      setItems((cur) => cur.map((i) => (i.assetId === a.assetId ? { ...i, placed: true } : i)));
    } catch (err) {
      setError(errorText(err));
    }
  }

  async function saveAll() {
    const unplaced = items.filter((a) => !a.placed);
    try {
      await Promise.all(unplaced.map((a) => api(`/api/v1/plant/assets/${a.assetId}/layout`, { method: "PUT", json: { x: a.x, y: a.y } })));
      setItems((cur) => cur.map((a) => ({ ...a, placed: true })));
      setSaved(`Saved ${unplaced.length} positions.`);
    } catch (err) {
      setError(errorText(err));
    }
  }

  return (
    <>
      <PageHeader
        title="Floor plan"
        sub="Every machine where it stands, coloured by state, with live power. Click a machine to open it."
        actions={<LiveBadge state={hub} />}
      />
      <ErrorNote error={error} />
      {saved && <div className="mb-3 rounded-lg bg-ok/10 px-3 py-2 text-sm text-ok">{saved}</div>}
      <Card
        title={`${shown.length} assets · ${Math.round(totalKw).toLocaleString()} kW now`}
        actions={
          <>
            <Select value={area} onChange={(e) => setArea(e.target.value)} className="w-44 py-1" aria-label="Area">
              <option value="all">All areas</option>
              {areas.map((a) => <option key={a}>{a}</option>)}
            </Select>
            {canEdit && (
              <Button variant={edit ? "primary" : "outline"} className="py-1" onClick={() => setEdit(!edit)}>
                <Move size={14} /> {edit ? "Done arranging" : "Arrange"}
              </Button>
            )}
            {canEdit && items.some((a) => !a.placed) && (
              <Button variant="outline" className="py-1" onClick={saveAll}><Save size={14} /> Save layout</Button>
            )}
          </>
        }
      >
        <div className="mb-3 flex flex-wrap gap-2 text-xs">
          {Object.entries(counts).map(([s, n]) => <Badge key={s} tone={statusTone[s as keyof typeof statusTone] ?? "muted"}>{s}: {n}</Badge>)}
          {edit && <span className="text-muted">Drag machines to where they stand; each move is saved.</span>}
        </div>
        {items.length === 0 ? (
          <p className="text-sm text-muted">No assets yet. Add them under Plant & assets, or generate the demo factory from the dashboard.</p>
        ) : (
          <div className="overflow-x-auto">
            <svg ref={svg} viewBox={`0 0 ${W} ${H}`} className="min-w-[720px] w-full select-none rounded-lg bg-panel-2" onPointerMove={move} onPointerUp={up}
              role="img" aria-label="Factory floor plan">
              {zones.filter((z) => area === "all" || z.name === area).map((z) => (
                <g key={z.name}>
                  <rect x={z.x} y={z.y} width={z.w} height={z.h} rx={10} fill="none" stroke="var(--border)" strokeDasharray="6 4" />
                  <text x={z.x + 10} y={z.y + 18} fontSize={13} fontWeight={600} fill="var(--muted)">{z.name}</text>
                </g>
              ))}
              {shown.map((a) => {
                const tone = statusTone[a.status] ?? "muted";
                return (
                  <g key={a.assetId} transform={`translate(${a.x} ${a.y})`} data-asset={a.code}
                    className={edit ? "cursor-move" : "cursor-pointer"}
                    onPointerDown={(e) => down(e, a)}
                    onClick={() => { if (!edit) router.push(`/app/plant/assets/${a.assetId}`); }}>
                    <title>{`${a.name} · ${a.status}${a.lineName ? ` · ${a.lineName}` : ""}`}</title>
                    <rect width={BOX_W} height={BOX_H} rx={8} fill="var(--panel)" stroke={fill[tone]} strokeWidth={2.5} />
                    <rect width={6} height={BOX_H} rx={3} fill={fill[tone]} />
                    <text x={12} y={18} fontSize={11} fontWeight={600} fill="var(--text)">{a.name.length > 13 ? a.name.slice(0, 12) + "…" : a.name}</text>
                    <text x={12} y={33} fontSize={10} fill="var(--muted)">{a.status}</text>
                    <text x={12} y={46} fontSize={10} fill="var(--text)">{a.powerKw !== null ? `${a.powerKw.toFixed(1)} kW` : a.category}</text>
                  </g>
                );
              })}
            </svg>
          </div>
        )}
      </Card>
    </>
  );
}
