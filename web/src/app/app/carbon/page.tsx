"use client";

import { useCallback, useEffect, useState } from "react";
import { Download, Factory, Flame, Leaf, Pencil, Sun } from "lucide-react";
import { api } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { BarChart, HBars, PERIODS, fmt, periodQuery } from "@/components/charts";
import { Button, Card, ErrorNote, Field, Input, Modal, PageHeader, Select, Stat, Table, errorText } from "@/components/ui";

type Day = { day: string; gridKwh: number; solarKwh: number; gasM3: number; scope1Kg: number; scope2Kg: number };
type Report = {
  from: string; to: string; electricityFactor: number; gasFactor: number; gridKwh: number; solarKwh: number; gasM3: number; scope1Kg: number;
  scope2Kg: number; avoidedKg: number; totalKg: number; kgPerPart: number | null; parts: number; daily: Day[]; byArea: { meter: string; kg: number }[];
  electricityReference: string | null; gasReference: string | null; scope2MarketKg: number; greenKwh: number; unmeteredKwh: number;
  fuels: { source: string; unit: string; quantity: number; factor: number; kg: number; scope: number; factorName: string | null }[] | null;
  fugitiveKg: number; processKg: number; scope3Kg: number; gridRegion: string;
  scope3: { category: number; name: string; kg: number }[] | null;
  activities: { source: string; name: string; scope: number; scope3Category: number | null; method: string; unit: string; quantity: number; kg: number }[] | null;
};
type Material = { itemCode: string; itemName: string; unit: string; qtyPerUnit: number; kgCo2ePerUnit: number | null; kgPerProduct: number; source: string | null; itemId: string };
type Product = {
  productId: string; code: string; name: string; orders: number; units: number; massKg: number | null; directKwh: number; overheadKwh: number; electricityKg: number;
  scope1Kg: number; materialsKg: number; totalKg: number; kgPerUnit: number; kgPerTonne: number | null; electricityKgPerUnit: number; materialsKgPerUnit: number;
  materialsWithFactor: number; materialsTotal: number; materials: Material[];
};
type Footprint = { boundary: string; electricityFactor: number; siteKwh: number; machinesKwh: number; overheadSharePct: number; siteScope1Kg: number; products: Product[]; notes: string[] };

type FootprintForm = { product: Product; mass: string; factors: Record<string, { value: string; source: string }> };

export default function CarbonPage() {
  const { can } = useAuth();
  const manage = can("energy.manage");
  const [edit, setEdit] = useState<FootprintForm | null>(null);
  const [saving, setSaving] = useState(false);
  const [period, setPeriod] = useState("30d");
  const [r, setR] = useState<Report | null>(null);
  const [fp, setFp] = useState<Footprint | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(() => {
    api<Report>(`/api/v1/energy/carbon?${periodQuery(period)}`).then(setR).catch((e) => setError(errorText(e)));
    api<Footprint>(`/api/v1/energy/carbon/footprint?${periodQuery(period)}`).then(setFp).catch((e) => setError(errorText(e)));
  }, [period]);
  useEffect(load, [load]);

  function openEdit(p: Product) {
    setEdit({ product: p, mass: p.massKg !== null ? String(p.massKg) : "",
      factors: Object.fromEntries(p.materials.map((m) => [m.itemId, { value: m.kgCo2ePerUnit !== null ? String(m.kgCo2ePerUnit) : "", source: m.source ?? "" }])) });
  }

  /** Saves the unit mass and each changed material factor, then reloads the footprint. */
  async function saveEdit() {
    if (!edit) return;
    setSaving(true);
    setError(null);
    try {
      const p = edit.product;
      const mass = edit.mass.trim() === "" ? null : Number(edit.mass);
      if (mass !== p.massKg) await api(`/api/v1/enms/products/${p.productId}/mass`, { method: "PUT", json: { massKg: mass } });
      for (const m of p.materials) {
        const f = edit.factors[m.itemId];
        const value = f.value.trim() === "" ? null : Number(f.value);
        if (value === m.kgCo2ePerUnit && (f.source || null) === m.source) continue;
        await api(`/api/v1/enms/items/${m.itemId}/co2e`, { method: "PUT", json: { kgCo2ePerUnit: value, source: value === null ? null : f.source } });
      }
      setEdit(null);
      load();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setSaving(false);
    }
  }

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
      <PageHeader title="Carbon / ESG" sub={r ? `Grid region ${r.gridRegion}: ${r.electricityFactor} kg CO₂e/kWh, gas ${Number(r.gasFactor).toFixed(3)} kg CO₂e/Nm³ — from the factor library (ISO 50001 → Emission factors)` : "Scope 1, 2 and 3 emissions"}
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
            <Stat label="Scope 2 (electricity)" value={`${fmt(r.scope2Kg)} kg`} sub={`location-based · market-based ${fmt(r.scope2MarketKg)} kg${r.greenKwh ? ` (${fmt(r.greenKwh)} kWh green power)` : ""}`} icon={<Factory size={16} />} />
            <Stat label="Scope 1 (fuel, refrigerants, process)" value={`${fmt(r.scope1Kg)} kg`} sub={`${fmt(r.gasM3)} Nm³ metered gas${r.fuels?.some((f) => f.scope === 1) ? " + fuel deliveries" : ""}${r.fugitiveKg ? ` · refrigerant ${fmt(r.fugitiveKg)} kg` : ""}${r.processKg ? ` · process ${fmt(r.processKg)} kg` : ""}`} icon={<Flame size={16} />} />
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
          <div className="mb-6 grid gap-4 lg:grid-cols-2">
            <Card title={`Scope 3 (value chain) — ${fmt(r.scope3Kg)} kg CO₂e, reported separately`}>
              {(r.scope3?.length ?? 0) === 0 ? <p className="text-sm text-muted">No Scope 3 activities in this period. Add materials, freight, waste and travel under ISO 50001 → GHG activities.</p> : (
                <HBars rows={r.scope3!.map((c) => ({ label: `Cat ${c.name}`, value: c.kg, tone: "info" as const }))} unit=" kg" />
              )}
            </Card>
            <Card title="Activity data in this period (refrigerant, process, Scope 3)">
              <Table head={["Activity", "Scope", "Quantity", "kg CO₂e"]} empty={(r.activities?.length ?? 0) === 0}>
                {(r.activities ?? []).map((a) => (
                  <tr key={a.source}>
                    <td className="px-3 py-2">{a.name}</td>
                    <td className="px-3 py-2 text-xs">{a.scope}{a.scope3Category ? ` · cat ${a.scope3Category}` : ""} · {a.method}</td>
                    <td className="px-3 py-2 tabular-nums">{fmt(a.quantity)} {a.unit}</td>
                    <td className="px-3 py-2 tabular-nums">{fmt(a.kg)}</td>
                  </tr>
                ))}
              </Table>
            </Card>
          </div>
          {(r.fuels?.length ?? 0) > 0 && (
            <Card title="Fuels and unmetered energy (from Energy data entries)" className="mb-6">
              <Table head={["Source", "Quantity", "Scope", "kg CO₂e / unit", "kg CO₂e", "Factor"]}>
                {r.fuels!.map((f) => (
                  <tr key={f.source + f.unit}>
                    <td className="px-3 py-2">{f.source.replace("_", " ")}</td>
                    <td className="px-3 py-2 tabular-nums">{fmt(f.quantity)} {f.unit}</td>
                    <td className="px-3 py-2">{f.scope}</td>
                    <td className="px-3 py-2 tabular-nums">{Number(f.factor).toFixed(4)}</td>
                    <td className="px-3 py-2 tabular-nums">{fmt(f.kg)}</td>
                    <td className="px-3 py-2 text-xs text-muted">{f.factorName ?? "—"}</td>
                  </tr>
                ))}
              </Table>
            </Card>
          )}
          {fp && (
            <Card title={`Product carbon footprint — ${fp.boundary}`} className="mb-6">
              <Table head={["Product", "Orders · units", "Machine kWh", "Site overhead kWh", "Energy kg/unit", "Materials kg/unit", "kg CO₂e / unit", "per tonne", ""]} empty={fp.products.length === 0}>
                {fp.products.map((p) => (
                  <tr key={p.productId}>
                    <td className="px-3 py-2 font-medium">{p.name}<div className="font-mono text-[11px] text-muted">{p.code}</div></td>
                    <td className="px-3 py-2 tabular-nums">{p.orders} · {fmt(p.units)}</td>
                    <td className="px-3 py-2 tabular-nums">{fmt(p.directKwh)}</td>
                    <td className="px-3 py-2 tabular-nums">{fmt(p.overheadKwh)}</td>
                    <td className="px-3 py-2 tabular-nums">{p.electricityKgPerUnit.toFixed(4)}</td>
                    <td className="px-3 py-2 tabular-nums">{p.materialsKgPerUnit.toFixed(4)}<div className="text-[11px] text-muted">{p.materialsWithFactor}/{p.materialsTotal} materials with a factor</div></td>
                    <td className="px-3 py-2 tabular-nums font-medium">{p.kgPerUnit.toFixed(4)}</td>
                    <td className="px-3 py-2 tabular-nums">{p.kgPerTonne !== null ? `${fmt(p.kgPerTonne)} kg/t` : <span className="text-xs text-muted">no mass</span>}</td>
                    <td className="px-3 py-2">{manage && <Button variant="ghost" className="py-1" title="Mass and material factors" aria-label={`Edit footprint data for ${p.code}`} onClick={() => openEdit(p)}><Pencil size={14} /></Button>}</td>
                  </tr>
                ))}
              </Table>
              <ul className="mt-3 list-disc space-y-1 pl-5 text-xs text-muted">{fp.notes.map((n) => <li key={n}>{n}</li>)}</ul>
            </Card>
          )}
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
          <p className="mt-4 text-xs text-muted">
            Grid: {r.electricityReference ?? "Settings"}. Gas: {r.gasReference ?? "Settings"}. Scope 2 follows the GHG Protocol Scope 2 Guidance (location- and market-based); refrigerant losses use AR5 GWPs; Scope 3 is reported separately from the Scope 1 + 2 total, as the GHG Protocol Corporate Standard asks.
          </p>
        </>
      )}
      <Modal open={edit !== null} title={edit ? `Footprint data — ${edit.product.name}` : ""} onClose={() => setEdit(null)} wide>
        {edit && (
          <div className="space-y-4">
            <Field label="Mass of one unit (kg)" hint="Needed for kg CO₂e per tonne; leave empty if not known">
              <Input type="number" min="0" step="any" value={edit.mass} onChange={(e) => setEdit({ ...edit, mass: e.target.value })} />
            </Field>
            <div>
              <div className="mb-2 text-sm font-medium">Cradle-to-gate factors of the materials in the bill of materials</div>
              {edit.product.materials.length === 0 ? <p className="text-xs text-muted">This product has no bill of materials, so only energy is counted.</p> : (
                <Table head={["Material", "Per unit", "kg CO₂e per material unit", "Source (supplier EPD, database and version…)"]}>
                  {edit.product.materials.map((m) => {
                    const f = edit.factors[m.itemId];
                    const set = (patch: Partial<typeof f>) => setEdit({ ...edit, factors: { ...edit.factors, [m.itemId]: { ...f, ...patch } } });
                    return (
                      <tr key={m.itemId}>
                        <td className="px-3 py-2">{m.itemName}<div className="font-mono text-[11px] text-muted">{m.itemCode}</div></td>
                        <td className="px-3 py-2 tabular-nums">{m.qtyPerUnit} {m.unit}</td>
                        <td className="px-3 py-2"><Input type="number" min="0" step="any" value={f.value} aria-label={`Factor for ${m.itemCode}`} onChange={(e) => set({ value: e.target.value })} /></td>
                        <td className="px-3 py-2"><Input value={f.source} aria-label={`Source for ${m.itemCode}`} onChange={(e) => set({ source: e.target.value })} /></td>
                      </tr>
                    );
                  })}
                </Table>
              )}
            </div>
            <div className="flex justify-end gap-2">
              <Button variant="outline" onClick={() => setEdit(null)}>Cancel</Button>
              <Button onClick={saveEdit} disabled={saving}>{saving ? "Saving…" : "Save"}</Button>
            </div>
          </div>
        )}
      </Modal>
    </>
  );
}
