"use client";

import { useCallback, useEffect, useState } from "react";
import { Download, Factory, Flame, Leaf, Sun } from "lucide-react";
import { api } from "@/lib/api";
import { BarChart, HBars, PERIODS, fmt, periodQuery } from "@/components/charts";
import { Button, Card, ErrorNote, PageHeader, Select, Stat, Table, errorText } from "@/components/ui";

type Day = { day: string; gridKwh: number; solarKwh: number; gasM3: number; scope1Kg: number; scope2Kg: number };
type Report = {
  from: string; to: string; electricityFactor: number; gasFactor: number; gridKwh: number; solarKwh: number; gasM3: number; scope1Kg: number;
  scope2Kg: number; avoidedKg: number; totalKg: number; kgPerPart: number | null; parts: number; daily: Day[]; byArea: { meter: string; kg: number }[];
};

export default function CarbonPage() {
  const [period, setPeriod] = useState("30d");
  const [r, setR] = useState<Report | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(() => {
    api<Report>(`/api/v1/energy/carbon?${periodQuery(period)}`).then(setR).catch((e) => setError(errorText(e)));
  }, [period]);
  useEffect(load, [load]);

  function exportCsv() {
    if (!r) return;
    const rows = [["Day", "Grid kWh", "Solar kWh", "Gas Nm3", "Scope 1 kg CO2e", "Scope 2 kg CO2e"],
      ...r.daily.map((d) => [d.day.slice(0, 10), d.gridKwh, d.solarKwh, d.gasM3, d.scope1Kg, d.scope2Kg])];
    const blob = new Blob([rows.map((x) => x.join(",")).join("\n")], { type: "text/csv" });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = `carbon-${r.from.slice(0, 10)}-to-${r.to.slice(0, 10)}.csv`;
    a.click();
  }

  return (
    <>
      <PageHeader title="Carbon / ESG" sub={r ? `Factors: ${r.electricityFactor} kg CO₂e/kWh grid, ${r.gasFactor} kg CO₂e/Nm³ gas (change in Settings)` : "Scope 1 and 2 emissions"}
        actions={
          <>
            <Select value={period} onChange={(e) => setPeriod(e.target.value)} className="w-40 py-1" aria-label="Period">
              {PERIODS.map((p) => <option key={p.key} value={p.key}>{p.label}</option>)}
            </Select>
            <Button variant="outline" className="py-1" onClick={exportCsv} disabled={!r}><Download size={14} /> CSV</Button>
          </>
        } />
      <ErrorNote error={error} />
      {r && (
        <>
          <div className="mb-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
            <Stat label="Total emissions" value={`${fmt(r.totalKg / 1000)} t CO₂e`} sub={r.kgPerPart ? `${r.kgPerPart} kg per part` : "Scope 1 + 2"} icon={<Leaf size={16} />} />
            <Stat label="Scope 2 (electricity)" value={`${fmt(r.scope2Kg)} kg`} sub={`${fmt(r.gridKwh)} kWh from the grid`} icon={<Factory size={16} />} />
            <Stat label="Scope 1 (fuel)" value={`${fmt(r.scope1Kg)} kg`} sub={`${fmt(r.gasM3)} Nm³ natural gas`} icon={<Flame size={16} />} />
            <Stat label="Avoided by solar" value={`${fmt(r.avoidedKg)} kg`} sub={`${fmt(r.solarKwh)} kWh generated`} icon={<Sun size={16} />} />
          </div>
          <div className="mb-6 grid gap-4 lg:grid-cols-3">
            <Card title="Emissions per day (kg CO₂e)" className="lg:col-span-2">
              <BarChart bars={r.daily.map((d) => ({ label: d.day.slice(5, 10), value: d.scope1Kg + d.scope2Kg, title: `${d.day.slice(0, 10)}: scope 1 ${fmt(d.scope1Kg)} + scope 2 ${fmt(d.scope2Kg)} kg` }))} unit=" kg" />
            </Card>
            <Card title="Scope 2 by area meter">
              <HBars rows={r.byArea.map((a) => ({ label: a.meter, value: a.kg, tone: "ok" as const }))} unit=" kg" />
            </Card>
          </div>
          <Card title="Daily detail">
            <Table head={["Day", "Grid kWh", "Solar kWh", "Gas Nm³", "Scope 1 kg", "Scope 2 kg"]} empty={r.daily.length === 0}>
              {r.daily.map((d) => (
                <tr key={d.day}>
                  <td className="px-3 py-2">{d.day.slice(0, 10)}</td>
                  <td className="px-3 py-2 tabular-nums">{fmt(d.gridKwh)}</td>
                  <td className="px-3 py-2 tabular-nums">{fmt(d.solarKwh)}</td>
                  <td className="px-3 py-2 tabular-nums">{fmt(d.gasM3)}</td>
                  <td className="px-3 py-2 tabular-nums">{fmt(d.scope1Kg)}</td>
                  <td className="px-3 py-2 tabular-nums">{fmt(d.scope2Kg)}</td>
                </tr>
              ))}
            </Table>
          </Card>
        </>
      )}
    </>
  );
}
