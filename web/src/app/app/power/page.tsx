"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { api } from "@/lib/api";
import { ago } from "@/lib/types";
import { Badge, Card, ErrorNote, PageHeader, Table, errorText } from "@/components/ui";

type Signal = { sensorTypeCode: string; unit: string; latest: number | null; min: number | null; max: number | null; avg: number | null; state: "ok" | "warn" | "bad" | "muted"; note: string | null };
type Point = { assetId: string; assetName: string; assetTypeCode: string; latestAt: string | null; signals: Signal[]; worst: Signal["state"] };

const names: Record<string, string> = {
  voltage_ln: "Voltage L-N", current: "Current", power_factor: "Power factor", frequency: "Frequency", thd_voltage: "Voltage THD", thd_current: "Current THD",
};
const stateText = { ok: "Good", warn: "Watch", bad: "Problem", muted: "No data" };

/** Power quality at every analyser and meter: today's latest, minimum, maximum and average, graded against common limits. */
export default function PowerQualityPage() {
  const [points, setPoints] = useState<Point[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    const load = () => api<Point[]>("/api/v1/energy/power-quality").then(setPoints).catch((e) => setError(errorText(e)));
    load();
    const t = setInterval(load, 30_000);
    return () => clearInterval(t);
  }, []);

  return (
    <>
      <PageHeader title="Power quality" sub="Last 24 hours at each analyser and meter. Limits: PF ≥ 0.90, voltage THD ≤ 5 %, frequency 59.5–60.5 Hz, voltage ±5 % of 220 V." />
      <ErrorNote error={error} />
      {points?.length === 0 && <Card><p className="text-sm text-muted">No power analysers or energy meters with voltage, current, power factor or harmonics tags yet.</p></Card>}
      <div className="space-y-4">
        {points?.map((p) => (
          <Card key={p.assetId}
            title={<span className="flex items-center gap-2"><Link href={`/app/plant/assets/${p.assetId}`} className="hover:text-brand">{p.assetName}</Link>
              <Badge tone={p.worst}>{stateText[p.worst]}</Badge></span>}
            actions={<span className="text-xs text-muted">updated {ago(p.latestAt)}</span>}>
            <Table head={["Signal", "Now", "Min", "Max", "Average", "State"]}>
              {p.signals.map((s) => (
                <tr key={s.sensorTypeCode}>
                  <td className="px-3 py-2">{names[s.sensorTypeCode] ?? s.sensorTypeCode}</td>
                  <td className="px-3 py-2 font-medium tabular-nums">{v(s.latest)} {s.unit}</td>
                  <td className="px-3 py-2 tabular-nums">{v(s.min)}</td>
                  <td className="px-3 py-2 tabular-nums">{v(s.max)}</td>
                  <td className="px-3 py-2 tabular-nums">{v(s.avg)}</td>
                  <td className="px-3 py-2"><Badge tone={s.state}>{stateText[s.state]}</Badge>{s.note && <span className="ml-2 text-xs text-muted">{s.note}</span>}</td>
                </tr>
              ))}
            </Table>
          </Card>
        ))}
      </div>
    </>
  );
}

function v(x: number | null) {
  return x === null ? "—" : Math.abs(x) >= 100 ? x.toFixed(1) : x.toFixed(2);
}
