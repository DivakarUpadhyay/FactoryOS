"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { Bot, Cable, Factory, Zap } from "lucide-react";
import { api } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { Asset, Catalogue, TreeNode } from "@/lib/types";
import { Badge, Button, Card, ErrorNote, PageHeader, Stat, errorText } from "@/components/ui";

const statusTone = { Running: "ok", Idle: "info", Stopped: "warn", Fault: "bad", Maintenance: "warn", Offline: "muted" } as const;

function flatten(nodes: TreeNode[]): { levels: Record<string, number>; assets: Asset[] } {
  const levels: Record<string, number> = {};
  const assets: Asset[] = [];
  const walk = (n: TreeNode) => {
    levels[n.node.level] = (levels[n.node.level] ?? 0) + 1;
    assets.push(...n.assets);
    n.children.forEach(walk);
  };
  nodes.forEach(walk);
  return { levels, assets };
}

export default function FactoryDashboard() {
  const { me, can } = useAuth();
  const [tree, setTree] = useState<TreeNode[]>([]);
  const [cat, setCat] = useState<Catalogue | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [building, setBuilding] = useState(false);
  const [built, setBuilt] = useState<string | null>(null);

  const load = useCallback(() => {
    if (!can("plant.read")) return;
    Promise.all([api<TreeNode[]>("/api/v1/plant/tree"), api<Catalogue>("/api/v1/plant/catalogue")])
      .then(([t, c]) => {
        setTree(t);
        setCat(c);
      })
      .catch((e) => setError(errorText(e)));
  }, [can]);
  useEffect(() => {
    load();
    const t = setInterval(load, 10_000);   // statuses change as the worker reads the devices
    return () => clearInterval(t);
  }, [load]);

  async function buildDemo() {
    setBuilding(true);
    setError(null);
    try {
      const r = await api<{ nodes: number; assets: number; tags: number; skippedForPlanLimit: number }>("/api/v1/plant/demo", { method: "POST" });
      setBuilt(`Demo factory ready: ${r.assets} assets with ${r.tags} tags${r.skippedForPlanLimit ? ` (${r.skippedForPlanLimit} skipped by your plan limit)` : ""}. Live values appear within a few seconds while the worker runs.`);
      load();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBuilding(false);
    }
  }

  const { levels, assets } = useMemo(() => flatten(tree), [tree]);
  const typeOf = useMemo(() => Object.fromEntries((cat?.assetTypes ?? []).map((t) => [t.assetTypeCode, t])), [cat]);
  const byCategory = assets.reduce<Record<string, number>>((m, a) => {
    const c = typeOf[a.assetTypeCode]?.category ?? "Other";
    m[c] = (m[c] ?? 0) + 1;
    return m;
  }, {});
  const byStatus = assets.reduce<Record<string, number>>((m, a) => ({ ...m, [a.status]: (m[a.status] ?? 0) + 1 }), {});
  const protocols = new Set(assets.map((a) => a.protocolCode).filter(Boolean));
  const ratedKw = assets.reduce((s, a) => s + (a.ratedPowerKw ?? 0), 0);

  return (
    <>
      <PageHeader title={`Welcome, ${me?.displayName ?? ""}`} sub={`${me?.tenantName ?? ""} · plant overview`} />
      <ErrorNote error={error} />
      {built && <div className="mb-4 rounded-lg bg-ok/10 px-3 py-2 text-sm text-ok">{built}</div>}
      <div className="mb-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <Stat label="Plants" value={levels.Plant ?? 0} sub={`${levels.Site ?? 0} sites · ${levels.Line ?? 0} lines`} icon={<Factory size={16} />} />
        <Stat label="Assets" value={assets.length} sub={`${byCategory.Robotics ?? 0} robots & AGVs`} icon={<Bot size={16} />} />
        <Stat label="Installed power" value={`${ratedKw.toLocaleString()} kW`} sub="Sum of rated power" icon={<Zap size={16} />} />
        <Stat label="Protocols in use" value={protocols.size} sub={[...protocols].slice(0, 4).join(", ") || "none yet"} icon={<Cable size={16} />} />
      </div>

      {assets.length === 0 ? (
        <Card title="Get started">
          <ol className="list-decimal space-y-2 pl-5 text-sm">
            <li>Build your plant structure: Site → Plant → Area → Line → Cell.</li>
            <li>Add machines, robots, meters and sensors to the lines, choosing each one&apos;s protocol (OPC UA, MQTT, Modbus, S7, SECS/GEM…).</li>
            <li>Live data, energy, OEE and robotics dashboards light up as the next phases are switched on.</li>
          </ol>
          {can("plant.manage") && (
            <div className="mt-4 flex flex-wrap items-center gap-3">
              <Button onClick={buildDemo} disabled={building}>{building ? "Building…" : "Generate demo factory"}</Button>
              <span className="text-xs text-muted">45 simulated assets: SMT lines, CNC cell, moulding, robots, AGVs, utilities, solar, energy meters</span>
              <Link href="/app/plant" className="text-sm font-medium text-brand">or build your own →</Link>
            </div>
          )}
        </Card>
      ) : (
        <div className="grid gap-4 lg:grid-cols-2">
          <Card title="Assets by category">
            <div className="space-y-2">
              {Object.entries(byCategory).sort((a, b) => b[1] - a[1]).map(([c, n]) => (
                <div key={c}>
                  <div className="flex justify-between text-sm"><span>{c}</span><span className="tabular-nums text-muted">{n}</span></div>
                  <div className="mt-1 h-2 rounded-full bg-panel-2">
                    <div className="h-2 rounded-full bg-brand" style={{ width: `${(n / assets.length) * 100}%` }} />
                  </div>
                </div>
              ))}
            </div>
          </Card>
          <Card title="Asset status">
            <div className="flex flex-wrap gap-2">
              {Object.entries(byStatus).map(([s, n]) => (
                <Badge key={s} tone={statusTone[s as keyof typeof statusTone] ?? "muted"}>{s}: {n}</Badge>
              ))}
            </div>
            <Link href="/app/connectivity" className="mt-3 inline-block text-xs font-medium text-brand">Open connectivity monitor →</Link>
          </Card>
        </div>
      )}
    </>
  );
}
