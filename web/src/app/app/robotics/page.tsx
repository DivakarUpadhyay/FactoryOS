"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { BatteryCharging, Plus, Send, X } from "lucide-react";
import { api } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { statusTone } from "@/lib/types";
import { fmt } from "@/components/charts";
import { Badge, Button, Card, ErrorNote, Field, Input, Modal, PageHeader, Select, Table, errorText } from "@/components/ui";

type Member = {
  assetId: string; code: string; name: string; kind: "robot" | "vehicle"; assetTypeCode: string; protocolCode: string | null; status: keyof typeof statusTone;
  lastSeenAt: string | null; battery: number | null; x: number | null; y: number | null; speed: number | null; missions: number | null; cycles: number | null;
  jointTemp: number | null; jointTempMax24h: number | null; torque: number | null; torqueMax24h: number | null; tcpSpeed: number | null; alarmCode: number | null;
  utilizationPct: number | null; health: "ok" | "warn" | "bad"; warnings: string[]; currentMission: string | null;
};
type Station = { stationId: string; code: string; name: string; x: number; y: number };
type Mission = {
  missionId: string; code: string; fromStationId: string; fromStation: string; toStationId: string; toStation: string; priority: number; payload: string | null;
  vehicleAssetId: string | null; vehicleName: string | null; status: string; orderId: string | null; error: string | null; createdAt: string; finishedAt: string | null;
};
type Fleet = { robots: Member[]; vehicles: Member[]; stations: Station[]; activeMissions: Mission[]; mapWidth: number; mapHeight: number };

const missionTone: Record<string, "info" | "warn" | "ok" | "bad" | "muted"> = { Queued: "muted", Assigned: "info", PickedUp: "warn", Done: "ok", Failed: "bad", Cancelled: "muted" };
const colour: Record<string, string> = { ok: "var(--ok)", info: "var(--info)", warn: "var(--warn)", bad: "var(--bad)", muted: "var(--muted)" };

export default function RoboticsPage() {
  const { can } = useAuth();
  const operate = can("robotics.operate");
  const manage = can("plant.manage");
  const [fleet, setFleet] = useState<Fleet | null>(null);
  const [missions, setMissions] = useState<Mission[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [newMission, setNewMission] = useState(false);
  const [newStation, setNewStation] = useState(false);
  const [mf, setMf] = useState({ fromStationId: "", toStationId: "", priority: "2", payload: "", vehicleAssetId: "" });
  const [sf, setSf] = useState({ code: "", name: "", x: "", y: "" });

  const load = useCallback(() => {
    Promise.all([api<Fleet>("/api/v1/robotics/fleet"), api<Mission[]>("/api/v1/robotics/missions")])
      .then(([f, m]) => { setFleet(f); setMissions(m); setError(null); }).catch((e) => setError(errorText(e)));
  }, []);
  useEffect(() => {
    load();
    const t = setInterval(load, 3000);   // vehicles move about 4 m between refreshes
    return () => clearInterval(t);
  }, [load]);

  async function send(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api("/api/v1/robotics/missions", { method: "POST", json: { ...mf, priority: Number(mf.priority), payload: mf.payload || null, vehicleAssetId: mf.vehicleAssetId || null } });
      setNewMission(false);
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }
  async function addStation(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api("/api/v1/robotics/stations", { method: "POST", json: { ...sf, x: Number(sf.x), y: Number(sf.y) } });
      setNewStation(false);
      setSf({ code: "", name: "", x: "", y: "" });
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }
  async function cancel(id: string) {
    try {
      await api(`/api/v1/robotics/missions/${id}/cancel`, { method: "POST" });
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }

  const W = fleet?.mapWidth ?? 200, H = fleet?.mapHeight ?? 100;
  return (
    <>
      <PageHeader title="Robotics & AGV fleet" sub="Live map, robot health and transport missions (sent to real vehicles as VDA 5050 orders)."
        actions={operate && fleet && fleet.stations.length >= 2 && <Button onClick={() => setNewMission(true)}><Send size={14} /> New mission</Button>} />
      <ErrorNote error={error} />
      {fleet && (
        <>
          <div className="mb-6 grid gap-4 xl:grid-cols-3">
            <Card title="Fleet map" className="xl:col-span-2" actions={manage && <Button variant="outline" className="py-1" onClick={() => setNewStation(true)}><Plus size={14} /> Station</Button>}>
              <svg viewBox={`-5 -5 ${W + 10} ${H + 10}`} className="w-full rounded-lg bg-panel-2" role="img" aria-label="Fleet map">
                <rect x={0} y={0} width={W} height={H} fill="none" stroke="var(--border)" strokeDasharray="2 2" strokeWidth={0.4} />
                {fleet.activeMissions.map((m) => {
                  const v = fleet.vehicles.find((x) => x.assetId === m.vehicleAssetId);
                  const target = fleet.stations.find((s) => s.stationId === (m.status === "PickedUp" ? m.toStationId : m.fromStationId));
                  return v?.x != null && v.y != null && target ? <line key={m.missionId} x1={v.x} y1={v.y} x2={target.x} y2={target.y} stroke="var(--brand)" strokeWidth={0.6} strokeDasharray="2 1.5" /> : null;
                })}
                {fleet.stations.map((s) => (
                  <g key={s.stationId}>
                    <rect x={s.x - 2.5} y={s.y - 2.5} width={5} height={5} fill="var(--panel)" stroke="var(--muted)" strokeWidth={0.5} />
                    <text x={s.x} y={s.y - 4} fontSize={3.2} textAnchor="middle" fill="var(--muted)">{s.name}</text>
                  </g>
                ))}
                {fleet.vehicles.filter((v) => v.x !== null && v.y !== null).map((v) => {
                  const tone = statusTone[v.status] ?? "muted";
                  return (
                    <g key={v.assetId} data-vehicle={v.code} transform={`translate(${v.x} ${v.y})`}>
                      <title>{`${v.name} · ${v.status} · battery ${v.battery?.toFixed(0) ?? "?"} %`}</title>
                      <circle r={3.2} fill={colour[tone]} stroke="var(--panel)" strokeWidth={0.8} />
                      <text y={7.5} fontSize={3.2} textAnchor="middle" fontWeight={600} fill="var(--text)">{v.name}</text>
                    </g>
                  );
                })}
              </svg>
              <p className="mt-2 text-xs text-muted">{W} m × {H} m · squares are stations · dashed lines show where each vehicle is heading.</p>
            </Card>
            <Card title="Missions in progress">
              {fleet.activeMissions.length === 0 ? <p className="text-sm text-muted">No missions running.</p> : (
                <ul className="space-y-3 text-sm">
                  {fleet.activeMissions.map((m) => (
                    <li key={m.missionId} className="rounded-lg border border-line p-2">
                      <div className="flex items-center justify-between"><span className="font-mono text-xs">{m.code}</span><Badge tone={missionTone[m.status]}>{m.status}</Badge></div>
                      <div>{m.fromStation} → {m.toStation}</div>
                      <div className="text-xs text-muted">{m.vehicleName ?? "waiting for a free vehicle"}{m.payload ? ` · ${m.payload}` : ""}</div>
                    </li>
                  ))}
                </ul>
              )}
            </Card>
          </div>

          <Card title={`Vehicles (${fleet.vehicles.length})`} className="mb-6">
            <Table head={["Vehicle", "State", "Battery", "Speed", "Missions done", "Utilisation 24 h", "Current mission", "Health"]} empty={fleet.vehicles.length === 0}>
              {fleet.vehicles.map((v) => (
                <tr key={v.assetId}>
                  <td className="px-3 py-2"><Link href={`/app/plant/assets/${v.assetId}`} className="font-medium hover:text-brand">{v.name}</Link><div className="text-[11px] text-muted">{v.protocolCode}</div></td>
                  <td className="px-3 py-2"><Badge tone={statusTone[v.status] ?? "muted"}>{v.status}</Badge></td>
                  <td className="min-w-28 px-3 py-2">
                    <div className="flex items-center gap-1 text-xs">{v.battery !== null && v.battery < 25 && <BatteryCharging size={12} className="text-warn" />}{v.battery?.toFixed(0) ?? "—"} %</div>
                    <div className="mt-1 h-1.5 rounded-full bg-panel-2"><div className="h-1.5 rounded-full" style={{ width: `${v.battery ?? 0}%`, background: (v.battery ?? 0) < 25 ? "var(--warn)" : "var(--ok)" }} /></div>
                  </td>
                  <td className="px-3 py-2 tabular-nums">{v.speed !== null ? `${fmt(v.speed)} m/s` : "—"}</td>
                  <td className="px-3 py-2 tabular-nums">{v.missions?.toLocaleString() ?? "—"}</td>
                  <td className="px-3 py-2 tabular-nums">{v.utilizationPct !== null ? `${v.utilizationPct}%` : "—"}</td>
                  <td className="px-3 py-2 text-xs">{v.currentMission ?? "—"}</td>
                  <td className="px-3 py-2"><Badge tone={v.health}>{v.health}</Badge>{v.warnings.length > 0 && <div className="mt-1 text-xs text-muted">{v.warnings.join("; ")}</div>}</td>
                </tr>
              ))}
            </Table>
          </Card>

          <h2 className="mb-3 text-sm font-semibold uppercase tracking-wide text-muted">Robots ({fleet.robots.length})</h2>
          <div className="mb-6 grid gap-4 md:grid-cols-2 xl:grid-cols-3">
            {fleet.robots.map((r) => (
              <Card key={r.assetId} title={<span className="flex items-center gap-2"><Link href={`/app/plant/assets/${r.assetId}`} className="hover:text-brand">{r.name}</Link><Badge tone={statusTone[r.status] ?? "muted"}>{r.status}</Badge></span>}
                actions={<Badge tone={r.health}>{r.health}</Badge>}>
                <dl className="grid grid-cols-2 gap-x-4 gap-y-2 text-sm">
                  <dt className="text-muted">Utilisation 24 h</dt><dd className="tabular-nums">{r.utilizationPct !== null ? `${r.utilizationPct}%` : "—"}</dd>
                  <dt className="text-muted">Cycles</dt><dd className="tabular-nums">{r.cycles?.toLocaleString() ?? "—"}</dd>
                  <dt className="text-muted">Joint temp</dt><dd className="tabular-nums">{r.jointTemp !== null ? `${fmt(r.jointTemp)} °C` : "—"}{r.jointTempMax24h !== null && <span className="text-xs text-muted"> (max {fmt(r.jointTempMax24h)})</span>}</dd>
                  <dt className="text-muted">Joint torque</dt><dd className="tabular-nums">{r.torque !== null ? `${fmt(r.torque)} %` : "—"}{r.torqueMax24h !== null && <span className="text-xs text-muted"> (max {fmt(r.torqueMax24h)})</span>}</dd>
                  <dt className="text-muted">TCP speed</dt><dd className="tabular-nums">{r.tcpSpeed !== null ? `${fmt(r.tcpSpeed)} mm/s` : "—"}</dd>
                </dl>
                {r.warnings.length > 0 && <ul className="mt-3 list-disc pl-5 text-xs text-warn">{r.warnings.map((w) => <li key={w}>{w}</li>)}</ul>}
              </Card>
            ))}
          </div>

          <Card title="Missions — last 7 days">
            <Table head={["Mission", "Route", "Vehicle", "Priority", "Status", "Created", ""]} empty={missions.length === 0}>
              {missions.map((m) => (
                <tr key={m.missionId}>
                  <td className="px-3 py-2 font-mono text-xs">{m.code}{m.orderId && <div className="text-muted">order {m.orderId}</div>}</td>
                  <td className="px-3 py-2">{m.fromStation} → {m.toStation}{m.payload && <div className="text-xs text-muted">{m.payload}</div>}</td>
                  <td className="px-3 py-2">{m.vehicleName ?? "—"}</td>
                  <td className="px-3 py-2">{["", "Urgent", "Normal", "Low"][m.priority]}</td>
                  <td className="px-3 py-2"><Badge tone={missionTone[m.status]}>{m.status}</Badge>{m.error && <div className="text-xs text-bad">{m.error}</div>}</td>
                  <td className="whitespace-nowrap px-3 py-2 text-xs">{new Date(m.createdAt + "Z").toLocaleString([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" })}</td>
                  <td className="px-3 py-2 text-right">{operate && ["Queued", "Assigned", "PickedUp"].includes(m.status) &&
                    <Button variant="ghost" className="py-1 text-xs text-bad" onClick={() => cancel(m.missionId)}><X size={12} /> Cancel</Button>}</td>
                </tr>
              ))}
            </Table>
          </Card>

          <Modal open={newMission} title="New transport mission" onClose={() => setNewMission(false)}>
            <form onSubmit={send} className="grid gap-4 sm:grid-cols-2">
              <Field label="Pick up at"><Select required value={mf.fromStationId} onChange={(e) => setMf({ ...mf, fromStationId: e.target.value })}>
                <option value="">Choose…</option>{fleet.stations.map((s) => <option key={s.stationId} value={s.stationId}>{s.name}</option>)}</Select></Field>
              <Field label="Drop off at"><Select required value={mf.toStationId} onChange={(e) => setMf({ ...mf, toStationId: e.target.value })}>
                <option value="">Choose…</option>{fleet.stations.filter((s) => s.stationId !== mf.fromStationId).map((s) => <option key={s.stationId} value={s.stationId}>{s.name}</option>)}</Select></Field>
              <Field label="Priority"><Select value={mf.priority} onChange={(e) => setMf({ ...mf, priority: e.target.value })}>
                <option value="1">Urgent</option><option value="2">Normal</option><option value="3">Low</option></Select></Field>
              <Field label="Vehicle" hint="Automatic picks the nearest free one with enough battery"><Select value={mf.vehicleAssetId} onChange={(e) => setMf({ ...mf, vehicleAssetId: e.target.value })}>
                <option value="">Automatic</option>{fleet.vehicles.map((v) => <option key={v.assetId} value={v.assetId}>{v.name}</option>)}</Select></Field>
              <div className="sm:col-span-2"><Field label="Load"><Input value={mf.payload} maxLength={100} onChange={(e) => setMf({ ...mf, payload: e.target.value })} placeholder="e.g. 2 reels of PCB, pallet 1140" /></Field></div>
              <div className="flex justify-end gap-2 sm:col-span-2"><Button type="button" variant="ghost" onClick={() => setNewMission(false)}>Cancel</Button><Button type="submit"><Send size={14} /> Send</Button></div>
            </form>
          </Modal>
          <Modal open={newStation} title="New station" onClose={() => setNewStation(false)}>
            <form onSubmit={addStation} className="grid gap-4 sm:grid-cols-2">
              <Field label="Code"><Input required value={sf.code} onChange={(e) => setSf({ ...sf, code: e.target.value })} /></Field>
              <Field label="Name"><Input required value={sf.name} onChange={(e) => setSf({ ...sf, name: e.target.value })} /></Field>
              <Field label="X (m)"><Input required type="number" step="0.1" min="0" value={sf.x} onChange={(e) => setSf({ ...sf, x: e.target.value })} /></Field>
              <Field label="Y (m)"><Input required type="number" step="0.1" min="0" value={sf.y} onChange={(e) => setSf({ ...sf, y: e.target.value })} /></Field>
              <div className="flex justify-end gap-2 sm:col-span-2"><Button type="button" variant="ghost" onClick={() => setNewStation(false)}>Cancel</Button><Button type="submit">Add</Button></div>
            </form>
          </Modal>
        </>
      )}
    </>
  );
}
