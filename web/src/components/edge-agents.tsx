"use client";

import { useCallback, useEffect, useState } from "react";
import { Copy, KeyRound, Plus, Router, Trash2 } from "lucide-react";
import { api } from "@/lib/api";
import { ago } from "@/lib/types";
import { Badge, Button, Card, Field, Input, Modal, Select, Table, errorText } from "./ui";

type Agent = {
  agentId: string; name: string; keyPrefix: string; isActive: boolean; version: string | null; hostname: string | null; buffered: number | null; dropped: number | null;
  lastError: string | null; lastSeenAt: string | null; createdAt: string; assetIds: string; online: boolean;
};
type Asset = { assetId: string; name: string; code: string };

/**
 * Edge agents: the FactoryOS edge program on a plant PC or gateway reads the machines itself (same drivers), keeps readings on
 * disk while the link is down and runs the OT detectors locally. The cloud worker leaves an agent's assets alone.
 */
export function EdgeAgents({ assets, manage, onError }: { assets: Asset[]; manage: boolean; onError: (e: string | null) => void }) {
  const [agents, setAgents] = useState<Agent[]>([]);
  const [name, setName] = useState<string | null>(null);
  const [key, setKey] = useState<{ name: string; key: string } | null>(null);
  const [pick, setPick] = useState<{ agent: Agent; ids: string[] } | null>(null);

  const load = useCallback(() => {
    api<Agent[]>("/api/v1/edge/agents").then(setAgents).catch((e) => onError(errorText(e)));
  }, [onError]);
  useEffect(() => {
    load();
    const t = setInterval(load, 30_000);
    return () => clearInterval(t);
  }, [load]);

  async function act(fn: () => Promise<unknown>, after?: () => void) {
    onError(null);
    try { await fn(); after?.(); load(); } catch (e) { onError(errorText(e)); }
  }

  const names = new Map(assets.map((a) => [a.assetId, a.name]));
  return (
    <Card title="Edge agents (read machines in the plant, buffer offline, run AI locally)" className="mt-6"
      actions={manage && <Button className="py-1" onClick={() => setName("")}><Plus size={14} /> Agent</Button>}>
      <Table head={["Agent", "State", "Assets", "Buffered", "Version · host", "Last seen", ""]} empty={agents.length === 0}>
        {agents.map((a) => {
          const ids: string[] = JSON.parse(a.assetIds);
          return (
            <tr key={a.agentId}>
              <td className="px-3 py-2 font-medium"><span className="flex items-center gap-2"><Router size={14} />{a.name}</span><div className="font-mono text-[11px] text-muted">{a.keyPrefix}…</div></td>
              <td className="px-3 py-2">{!a.isActive ? <Badge>switched off</Badge> : a.online ? <Badge tone="ok">online</Badge> : <Badge tone="warn">not connected</Badge>}
                {a.lastError && <div className="max-w-48 truncate text-[11px] text-bad" title={a.lastError}>{a.lastError}</div>}</td>
              <td className="px-3 py-2 text-xs">{ids.length === 0 ? "—" : ids.slice(0, 3).map((i) => names.get(i) ?? i.slice(0, 8)).join(", ")}{ids.length > 3 ? ` +${ids.length - 3}` : ""}</td>
              <td className="px-3 py-2 tabular-nums text-xs">{a.buffered ?? "—"}{a.dropped ? <span className="text-bad"> · {a.dropped} dropped</span> : null}</td>
              <td className="px-3 py-2 text-xs">{a.version ?? "—"}{a.hostname ? ` · ${a.hostname}` : ""}</td>
              <td className="whitespace-nowrap px-3 py-2 text-xs text-muted">{ago(a.lastSeenAt)}</td>
              <td className="px-3 py-2 text-right">
                {manage && <span className="flex justify-end gap-1">
                  <Button variant="ghost" className="py-1" onClick={() => setPick({ agent: a, ids })}>Assets</Button>
                  <Button variant="ghost" className="py-1" title="New key" onClick={() => act(async () => {
                    const r = await api<{ name: string; key: string }>(`/api/v1/edge/agents/${a.agentId}/rotate-key`, { method: "POST" });
                    setKey(r);
                  })}><KeyRound size={14} /></Button>
                  <Button variant="ghost" className="py-1" onClick={() => act(() => api(`/api/v1/edge/agents/${a.agentId}/active?active=${!a.isActive}`, { method: "PUT" }))}>{a.isActive ? "Switch off" : "Switch on"}</Button>
                  <Button variant="ghost" className="py-1" aria-label="Delete" onClick={() => act(() => api(`/api/v1/edge/agents/${a.agentId}`, { method: "DELETE" }))}><Trash2 size={14} /></Button>
                </span>}
              </td>
            </tr>
          );
        })}
      </Table>
      <p className="mt-3 text-xs text-muted">Install: copy the <span className="font-mono">factoryos-edge</span> program to the plant PC or gateway, put the URL and key in <span className="font-mono">appsettings.json</span>, and run it as a Windows service or systemd unit (see docs/EDGE.md).</p>

      <Modal open={name !== null} title="New edge agent" onClose={() => setName(null)}>
        <div className="space-y-3">
          <Field label="Name" hint="Where it runs, e.g. Line 1 gateway"><Input value={name ?? ""} onChange={(e) => setName(e.target.value)} /></Field>
          <div className="flex justify-end"><Button onClick={() => act(async () => {
            const r = await api<{ name: string; key: string }>("/api/v1/edge/agents", { method: "POST", json: { name } });
            setName(null);
            setKey(r);
          })}>Create</Button></div>
        </div>
      </Modal>
      <Modal open={!!key} title={`Key for ${key?.name ?? ""}`} onClose={() => setKey(null)}>
        {key && (
          <div className="space-y-3">
            <p className="text-sm">Shown only now. Put it in the agent&apos;s appsettings.json (Edge:Key).</p>
            <div className="flex gap-2"><Input readOnly value={key.key} className="font-mono text-xs" /><Button variant="outline" onClick={() => navigator.clipboard?.writeText(key.key)}><Copy size={14} /></Button></div>
          </div>
        )}
      </Modal>
      <Modal open={!!pick} title={`Assets read by ${pick?.agent.name ?? ""}`} onClose={() => setPick(null)} wide>
        {pick && (
          <div className="space-y-3">
            <Field label="Assets" hint="Hold Ctrl to pick several. The cloud worker stops polling them while the agent is on.">
              <Select multiple size={10} value={pick.ids} onChange={(e) => setPick({ ...pick, ids: Array.from(e.target.selectedOptions).map((o) => o.value) })}>
                {assets.map((a) => <option key={a.assetId} value={a.assetId}>{a.name} ({a.code})</option>)}
              </Select>
            </Field>
            <div className="flex justify-end"><Button onClick={() => act(() => api(`/api/v1/edge/agents/${pick.agent.agentId}/assets`, { method: "PUT", json: { assetIds: pick.ids } }), () => setPick(null))}>Save</Button></div>
          </div>
        )}
      </Modal>
    </Card>
  );
}
