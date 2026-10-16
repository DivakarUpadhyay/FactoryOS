"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { CircleCheck, CircleOff, Plug, TriangleAlert } from "lucide-react";
import { api } from "@/lib/api";
import { ConnectionRow, ago, statusTone } from "@/lib/types";
import { useLive } from "@/lib/live";
import { Badge, Card, ErrorNote, Input, LiveBadge, PageHeader, Select, Stat, Table, errorText } from "@/components/ui";

const supportTone = { Native: "ok", Push: "info", Gateway: "warn", Planned: "muted" } as const;

/** Every asset's connection health, refreshed every 5 seconds. */
export default function ConnectivityPage() {
  const [rows, setRows] = useState<ConnectionRow[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState("");
  const [status, setStatus] = useState("all");

  const load = useCallback(() => {
    api<ConnectionRow[]>("/api/v1/connectivity").then((r) => {
      setRows(r);
      setError(null);
    }).catch((e) => setError(errorText(e)));
  }, []);
  const hub = useLive({
    status: (msgs) => setRows((cur) => cur.map((r) => {
      const m = msgs.find((x) => x.assetId === r.assetId);
      return m ? { ...r, status: m.status as ConnectionRow["status"], lastError: m.lastError, lastSeenAt: m.lastSeenAt ?? r.lastSeenAt } : r;
    })),
    values: (msgs) => {
      const seen = new Map(msgs.map((m) => [m.assetId, m.ts]));
      setRows((cur) => cur.map((r) => (seen.has(r.assetId) ? { ...r, lastSeenAt: seen.get(r.assetId)! } : r)));
    },
  });

  useEffect(() => {
    load();
    const t = setInterval(load, 30_000);
    return () => clearInterval(t);
  }, [load]);

  const shown = useMemo(() => rows.filter((r) =>
    (status === "all" || (status === "problem" ? r.status === "Offline" || r.status === "Fault" || !!r.lastError : r.status === status))
    && (!filter || `${r.code} ${r.name} ${r.protocolCode ?? ""}`.toLowerCase().includes(filter.toLowerCase()))), [rows, filter, status]);

  const online = rows.filter((r) => r.status !== "Offline").length;
  const errors = rows.filter((r) => r.lastError).length;
  const unconnected = rows.filter((r) => !r.protocolCode).length;

  return (
    <>
      <PageHeader title="Connectivity" sub="Live connection status of every machine, robot, meter and sensor." actions={<LiveBadge state={hub} />} />
      <ErrorNote error={error} />
      <div className="mb-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <Stat label="Online" value={`${online} / ${rows.length}`} icon={<CircleCheck size={16} className="text-ok" />} />
        <Stat label="Offline" value={rows.length - online} icon={<CircleOff size={16} />} />
        <Stat label="With errors" value={errors} icon={<TriangleAlert size={16} className="text-warn" />} />
        <Stat label="No protocol set" value={unconnected} icon={<Plug size={16} />} />
      </div>
      <Card
        title={`${shown.length} assets`}
        actions={
          <>
            <Input placeholder="Search…" value={filter} onChange={(e) => setFilter(e.target.value)} className="w-40 py-1" />
            <Select value={status} onChange={(e) => setStatus(e.target.value)} className="w-36 py-1">
              <option value="all">All</option>
              <option value="problem">Problems</option>
              {["Running", "Idle", "Stopped", "Fault", "Maintenance", "Offline"].map((s) => <option key={s}>{s}</option>)}
            </Select>
          </>
        }
      >
        <Table head={["Asset", "Protocol", "Endpoint", "Poll", "Tags", "Status", "Last data", "Error"]} empty={shown.length === 0}>
          {shown.map((r) => (
            <tr key={r.assetId}>
              <td className="px-3 py-2">
                <Link href={`/app/plant/assets/${r.assetId}`} className="font-medium hover:text-brand">{r.name}</Link>
                <div className="font-mono text-[11px] text-muted">{r.code}</div>
              </td>
              <td className="px-3 py-2 text-xs">
                {r.protocolCode ?? "—"} {r.supportLevel && <Badge tone={supportTone[r.supportLevel as keyof typeof supportTone] ?? "muted"}>{r.supportLevel}</Badge>}
              </td>
              <td className="max-w-48 truncate px-3 py-2 font-mono text-xs text-muted">{r.endpoint ?? "—"}</td>
              <td className="px-3 py-2 text-xs tabular-nums">{r.pollMs / 1000}s</td>
              <td className="px-3 py-2 tabular-nums">{r.tags}</td>
              <td className="px-3 py-2"><Badge tone={statusTone[r.status]}>{r.status}</Badge></td>
              <td className="whitespace-nowrap px-3 py-2 text-xs text-muted">{ago(r.lastSeenAt)}</td>
              <td className="max-w-64 truncate px-3 py-2 text-xs text-bad" title={r.lastError ?? ""}>{r.lastError}</td>
            </tr>
          ))}
        </Table>
      </Card>
    </>
  );
}
