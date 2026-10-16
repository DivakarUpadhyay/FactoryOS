"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { Coins, Gauge, Lightbulb, Sun, Zap } from "lucide-react";
import { api } from "@/lib/api";
import { BarChart, HBars, PERIODS, fmt, periodQuery } from "@/components/charts";
import { Card, ErrorNote, PageHeader, Select, Stat, Table, errorText } from "@/components/ui";

type AssetEnergy = { assetId: string; code: string; name: string; assetTypeCode: string; category: string; kwh: number; cost: number; sharePct: number; parts: number | null; kwhPerPart: number | null };
type Summary = {
  from: string; to: string; currency: string; gridKwh: number; solarKwh: number; consumptionKwh: number; cost: number; avgRate: number;
  peakKw: number; peakAt: string | null; contractKw: number | null; contractUsePct: number | null; idleKwh: number; idleCost: number; co2Kg: number;
  mainMeters: string[]; meters: AssetEnergy[]; machines: AssetEnergy[]; hourly: { hour: string; kwh: number; cost: number; peak: boolean }[];
  idle: { assetId: string; assetCode: string; assetName: string; idleKwh: number; idleHours: number; runKwh: number }[]; insights: string[];
};

export default function EnergyPage() {
  const [period, setPeriod] = useState("24h");
  const [s, setS] = useState<Summary | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(() => {
    api<Summary>(`/api/v1/energy/summary?${periodQuery(period)}`).then((r) => { setS(r); setError(null); }).catch((e) => setError(errorText(e)));
  }, [period]);
  useEffect(() => {
    load();
    const t = setInterval(load, 60_000);
    return () => clearInterval(t);
  }, [load]);

  const cur = s?.currency ?? "TWD";
  const daily = period !== "24h";
  // for longer periods, sum hours into days so the chart stays readable
  const bars = !s ? [] : daily
    ? Object.entries(s.hourly.reduce<Record<string, number>>((m, h) => ({ ...m, [h.hour.slice(5, 10)]: (m[h.hour.slice(5, 10)] ?? 0) + h.kwh }), {}))
        .map(([label, value]) => ({ label, value }))
    : s.hourly.map((h) => ({ label: h.hour.slice(11, 13), value: h.kwh, tone: h.peak ? ("warn" as const) : ("brand" as const),
        title: `${h.hour.slice(11, 16)} · ${fmt(h.kwh)} kWh · ${cur} ${fmt(h.cost)}${h.peak ? " · peak rate" : ""}` }));

  return (
    <>
      <PageHeader title="Energy" sub={s ? `Main meter: ${s.mainMeters.join(", ") || "none — using machine meters"}` : "Consumption, cost, demand and waste"}
        actions={
          <Select value={period} onChange={(e) => setPeriod(e.target.value)} className="w-40 py-1" aria-label="Period">
            {PERIODS.map((p) => <option key={p.key} value={p.key}>{p.label}</option>)}
          </Select>
        } />
      <ErrorNote error={error} />
      {s && (
        <>
          <div className="mb-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
            <Stat label="Grid energy" value={`${fmt(s.gridKwh)} kWh`} sub={s.solarKwh ? `+ ${fmt(s.solarKwh)} kWh solar` : `${fmt(s.co2Kg)} kg CO₂e`} icon={<Zap size={16} />} />
            <Stat label="Energy cost" value={`${cur} ${s.cost.toLocaleString()}`} sub={`avg ${s.avgRate} ${cur}/kWh`} icon={<Coins size={16} />} />
            <Stat label="Peak demand" value={`${fmt(s.peakKw)} kW`}
              sub={s.contractKw ? `${s.contractUsePct ?? 0}% of ${s.contractKw} kW contract` : "Set the contract capacity in Settings"} icon={<Gauge size={16} />} />
            <Stat label="Idle waste" value={`${fmt(s.idleKwh)} kWh`} sub={`${cur} ${s.idleCost.toLocaleString()} while idle/stopped`} icon={<Lightbulb size={16} />} />
          </div>

          {s.insights.length > 0 && (
            <Card title="What to act on" className="mb-6">
              <ul className="space-y-2 text-sm">{s.insights.map((i) => <li key={i} className="flex gap-2"><Lightbulb size={16} className="mt-0.5 shrink-0 text-warn" />{i}</li>)}</ul>
            </Card>
          )}

          <Card title={daily ? "Grid energy per day (kWh)" : "Grid energy per hour (kWh) — orange = peak-rate hours"} className="mb-6">
            <BarChart bars={bars} unit=" kWh" />
          </Card>

          <div className="mb-6 grid gap-4 lg:grid-cols-2">
            <Card title="Energy by machine">
              <HBars rows={s.machines.slice(0, 12).map((m) => ({ label: m.name, value: m.kwh, sub: `${m.sharePct}%` }))} unit=" kWh" />
            </Card>
            <Card title="Meters">
              <HBars rows={s.meters.map((m) => ({ label: m.name, value: m.kwh, tone: m.assetTypeCode === "solar-inverter" ? "ok" as const : "info" as const }))} unit=" kWh" />
              {s.solarKwh > 0 && <p className="mt-3 flex items-center gap-1 text-xs text-muted"><Sun size={12} /> Solar is shown in green.</p>}
            </Card>
          </div>

          <div className="grid gap-4 lg:grid-cols-2">
            <Card title="Energy per part">
              <Table head={["Machine", "kWh", "Parts", "kWh / part"]} empty={!s.machines.some((m) => m.kwhPerPart !== null)}>
                {s.machines.filter((m) => m.kwhPerPart !== null).sort((a, b) => (b.kwhPerPart ?? 0) - (a.kwhPerPart ?? 0)).map((m) => (
                  <tr key={m.assetId}>
                    <td className="px-3 py-2"><Link href={`/app/plant/assets/${m.assetId}`} className="hover:text-brand">{m.name}</Link></td>
                    <td className="px-3 py-2 tabular-nums">{fmt(m.kwh)}</td>
                    <td className="px-3 py-2 tabular-nums">{m.parts?.toLocaleString()}</td>
                    <td className="px-3 py-2 font-medium tabular-nums">{m.kwhPerPart}</td>
                  </tr>
                ))}
              </Table>
            </Card>
            <Card title="Idle energy (machine on, not producing)">
              <Table head={["Machine", "Idle kWh", "Idle hours", "Cost"]} empty={s.idle.length === 0}>
                {s.idle.slice(0, 10).map((i) => (
                  <tr key={i.assetId}>
                    <td className="px-3 py-2"><Link href={`/app/plant/assets/${i.assetId}`} className="hover:text-brand">{i.assetName}</Link></td>
                    <td className="px-3 py-2 tabular-nums">{fmt(i.idleKwh)}</td>
                    <td className="px-3 py-2 tabular-nums">{fmt(i.idleHours)}</td>
                    <td className="px-3 py-2 tabular-nums">{cur} {Math.round(i.idleKwh * s.avgRate).toLocaleString()}</td>
                  </tr>
                ))}
              </Table>
            </Card>
          </div>
        </>
      )}
    </>
  );
}
