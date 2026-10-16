"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { ChevronDown, ChevronRight, Pencil, Plus, Trash2 } from "lucide-react";
import { api } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { Asset, Catalogue, TreeNode, childLevel } from "@/lib/types";
import { Badge, Button, Card, ErrorNote, Field, Input, Modal, PageHeader, Select, Table, cx, errorText } from "@/components/ui";

const statusTone = { Running: "ok", Idle: "info", Stopped: "warn", Fault: "bad", Maintenance: "warn", Offline: "muted" } as const;

type NodeForm = { nodeId?: string; parentId: string | null; level: string; code: string; name: string; description: string };
type AssetForm = {
  assetId?: string; nodeId: string; assetTypeCode: string; protocolCode: string; code: string; name: string; vendor: string;
  model: string; serialNo: string; endpoint: string; ratedPowerKw: string; idealCycleSec: string; installedOn: string;
};

function find(nodes: TreeNode[], id: string | null): TreeNode | null {
  for (const n of nodes) {
    if (n.node.nodeId === id) return n;
    const hit = find(n.children, id);
    if (hit) return hit;
  }
  return null;
}

function TreeItem({ t, depth, selected, onSelect, open, toggle }: {
  t: TreeNode; depth: number; selected: string | null; onSelect: (id: string) => void; open: Set<string>; toggle: (id: string) => void;
}) {
  const isOpen = open.has(t.node.nodeId);
  return (
    <div>
      <div
        className={cx("flex cursor-pointer items-center gap-1 rounded-md py-1.5 pr-2 text-sm", selected === t.node.nodeId ? "bg-brand/15 text-brand" : "hover:bg-panel-2")}
        style={{ paddingLeft: 6 + depth * 14 }}
        onClick={() => onSelect(t.node.nodeId)}
      >
        <button
          className="grid h-4 w-4 place-items-center text-muted"
          onClick={(e) => {
            e.stopPropagation();
            toggle(t.node.nodeId);
          }}
        >
          {t.children.length > 0 ? isOpen ? <ChevronDown size={14} /> : <ChevronRight size={14} /> : null}
        </button>
        <span className="flex-1 truncate">{t.node.name}</span>
        <span className="text-[10px] uppercase text-muted">{t.node.level}</span>
        {t.assets.length > 0 && <span className="rounded bg-panel-2 px-1 text-[10px] text-muted">{t.assets.length}</span>}
      </div>
      {isOpen && t.children.map((c) => <TreeItem key={c.node.nodeId} t={c} depth={depth + 1} selected={selected} onSelect={onSelect} open={open} toggle={toggle} />)}
    </div>
  );
}

export default function PlantPage() {
  const { can } = useAuth();
  const manage = can("plant.manage");
  const [tree, setTree] = useState<TreeNode[]>([]);
  const [cat, setCat] = useState<Catalogue>({ assetTypes: [], protocols: [] });
  const [selected, setSelected] = useState<string | null>(null);
  const [open, setOpen] = useState<Set<string>>(new Set());
  const [error, setError] = useState<string | null>(null);
  const [nodeForm, setNodeForm] = useState<NodeForm | null>(null);
  const [assetForm, setAssetForm] = useState<AssetForm | null>(null);
  const [formError, setFormError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const [t, c] = await Promise.all([api<TreeNode[]>("/api/v1/plant/tree"), api<Catalogue>("/api/v1/plant/catalogue")]);
      setTree(t);
      setCat(c);
      setOpen((o) => (o.size ? o : new Set(t.map((x) => x.node.nodeId))));
    } catch (e) {
      setError(errorText(e));
    }
  }, []);
  useEffect(() => {
    load();
  }, [load]);

  const current = useMemo(() => find(tree, selected), [tree, selected]);
  const typeName = useMemo(() => Object.fromEntries(cat.assetTypes.map((t) => [t.assetTypeCode, t.name])), [cat]);
  const protoName = useMemo(() => Object.fromEntries(cat.protocols.map((p) => [p.protocolCode, p.name])), [cat]);
  const typesByCategory = useMemo(() => groupBy(cat.assetTypes, (t) => t.category), [cat]);
  const protocolsByFamily = useMemo(() => groupBy(cat.protocols, (p) => p.family), [cat]);

  const toggle = (id: string) => setOpen((o) => {
    const n = new Set(o);
    if (n.has(id)) n.delete(id);
    else n.add(id);
    return n;
  });

  async function run(action: () => Promise<unknown>, close: () => void) {
    setFormError(null);
    try {
      await action();
      close();
      await load();
    } catch (e) {
      setFormError(errorText(e));
    }
  }

  function saveNode(e: React.FormEvent) {
    e.preventDefault();
    if (!nodeForm) return;
    const body = { parentId: nodeForm.parentId, level: nodeForm.level, code: nodeForm.code, name: nodeForm.name, description: nodeForm.description || null };
    run(
      () => nodeForm.nodeId ? api(`/api/v1/plant/nodes/${nodeForm.nodeId}`, { method: "PUT", json: body }) : api("/api/v1/plant/nodes", { method: "POST", json: body }),
      () => {
        if (nodeForm.parentId) setOpen((o) => new Set(o).add(nodeForm.parentId!));
        setNodeForm(null);
      },
    );
  }

  function saveAsset(e: React.FormEvent) {
    e.preventDefault();
    if (!assetForm) return;
    const f = assetForm;
    const body = {
      nodeId: f.nodeId, assetTypeCode: f.assetTypeCode, protocolCode: f.protocolCode || null, code: f.code, name: f.name,
      vendor: f.vendor || null, model: f.model || null, serialNo: f.serialNo || null, endpoint: f.endpoint || null,
      ratedPowerKw: f.ratedPowerKw ? Number(f.ratedPowerKw) : null, idealCycleSec: f.idealCycleSec ? Number(f.idealCycleSec) : null,
      installedOn: f.installedOn || null,
    };
    run(
      () => f.assetId ? api(`/api/v1/plant/assets/${f.assetId}`, { method: "PUT", json: body }) : api("/api/v1/plant/assets", { method: "POST", json: body }),
      () => setAssetForm(null),
    );
  }

  async function remove(path: string, what: string) {
    if (!confirm(`Delete this ${what}?`)) return;
    setError(null);
    try {
      await api(path, { method: "DELETE" });
      if (what === "node") setSelected(null);
      await load();
    } catch (e) {
      setError(errorText(e));
    }
  }

  const newAsset = (nodeId: string): AssetForm => ({
    nodeId, assetTypeCode: cat.assetTypes[0]?.assetTypeCode ?? "", protocolCode: "", code: "", name: "", vendor: "", model: "",
    serialNo: "", endpoint: "", ratedPowerKw: "", idealCycleSec: "", installedOn: "",
  });
  const editAsset = (a: Asset): AssetForm => ({
    assetId: a.assetId, nodeId: a.nodeId, assetTypeCode: a.assetTypeCode, protocolCode: a.protocolCode ?? "", code: a.code, name: a.name,
    vendor: a.vendor ?? "", model: a.model ?? "", serialNo: a.serialNo ?? "", endpoint: a.endpoint ?? "",
    ratedPowerKw: a.ratedPowerKw?.toString() ?? "", idealCycleSec: a.idealCycleSec?.toString() ?? "", installedOn: a.installedOn ?? "",
  });
  const port = cat.protocols.find((p) => p.protocolCode === assetForm?.protocolCode);

  return (
    <>
      <PageHeader
        title="Plant & assets"
        sub="ISA-95 hierarchy: Site → Plant → Area → Line → Cell, with the machines, robots, meters and sensors on each."
        actions={manage && <Button onClick={() => setNodeForm({ parentId: null, level: "Site", code: "", name: "", description: "" })}><Plus size={16} /> New site</Button>}
      />
      <ErrorNote error={error} />
      <div className="grid gap-4 lg:grid-cols-[300px_1fr]">
        <Card title="Hierarchy" className="h-fit">
          {tree.length === 0 ? <p className="text-sm text-muted">No sites yet.</p> : tree.map((t) => (
            <TreeItem key={t.node.nodeId} t={t} depth={0} selected={selected} onSelect={setSelected} open={open} toggle={toggle} />
          ))}
        </Card>

        {current ? (
          <div className="space-y-4">
            <Card
              title={<span>{current.node.name} <Badge tone="brand">{current.node.level}</Badge></span>}
              actions={manage && (
                <>
                  {childLevel(current.node.level) && (
                    <Button variant="outline" className="py-1" onClick={() => setNodeForm({ parentId: current.node.nodeId, level: childLevel(current.node.level)!, code: "", name: "", description: "" })}>
                      <Plus size={14} /> {childLevel(current.node.level)}
                    </Button>
                  )}
                  <Button variant="ghost" className="py-1" onClick={() => setNodeForm({ ...current.node, description: current.node.description ?? "" })}><Pencil size={14} /></Button>
                  <Button variant="ghost" className="py-1 text-bad" onClick={() => remove(`/api/v1/plant/nodes/${current.node.nodeId}`, "node")}><Trash2 size={14} /></Button>
                </>
              )}
            >
              <dl className="grid gap-3 text-sm sm:grid-cols-3">
                <div><dt className="text-muted">Code</dt><dd className="font-mono">{current.node.code}</dd></div>
                <div><dt className="text-muted">Children</dt><dd>{current.children.length}</dd></div>
                <div><dt className="text-muted">Assets here</dt><dd>{current.assets.length}</dd></div>
                {current.node.description && <div className="sm:col-span-3"><dt className="text-muted">Description</dt><dd>{current.node.description}</dd></div>}
              </dl>
            </Card>
            <Card title="Assets" actions={manage && <Button className="py-1" onClick={() => setAssetForm(newAsset(current.node.nodeId))}><Plus size={14} /> Add asset</Button>}>
              <Table head={["Code", "Name", "Type", "Protocol", "Vendor / model", "Rated kW", "Status", ""]} empty={current.assets.length === 0}>
                {current.assets.map((a) => (
                  <tr key={a.assetId}>
                    <td className="px-3 py-2 font-mono text-xs">{a.code}</td>
                    <td className="px-3 py-2 font-medium"><Link href={`/app/plant/assets/${a.assetId}`} className="hover:text-brand hover:underline">{a.name}</Link></td>
                    <td className="px-3 py-2">{typeName[a.assetTypeCode]}</td>
                    <td className="px-3 py-2 text-xs">{a.protocolCode ? protoName[a.protocolCode] : "—"}</td>
                    <td className="px-3 py-2 text-xs text-muted">{[a.vendor, a.model].filter(Boolean).join(" ") || "—"}</td>
                    <td className="px-3 py-2 tabular-nums">{a.ratedPowerKw ?? "—"}</td>
                    <td className="px-3 py-2"><Badge tone={statusTone[a.status]}>{a.status}</Badge></td>
                    <td className="px-3 py-2">
                      {manage && (
                        <div className="flex gap-1">
                          <button className="rounded p-1 hover:bg-panel-2" onClick={() => setAssetForm(editAsset(a))} aria-label="Edit"><Pencil size={14} /></button>
                          <button className="rounded p-1 text-bad hover:bg-panel-2" onClick={() => remove(`/api/v1/plant/assets/${a.assetId}`, "asset")} aria-label="Delete"><Trash2 size={14} /></button>
                        </div>
                      )}
                    </td>
                  </tr>
                ))}
              </Table>
            </Card>
          </div>
        ) : (
          <Card><p className="text-sm text-muted">Select a node in the hierarchy to see and manage its assets.</p></Card>
        )}
      </div>

      <Modal open={!!nodeForm} title={nodeForm?.nodeId ? "Edit node" : `New ${nodeForm?.level ?? ""}`} onClose={() => setNodeForm(null)}>
        {nodeForm && (
          <form onSubmit={saveNode} className="space-y-3">
            <ErrorNote error={formError} />
            <div className="grid gap-3 sm:grid-cols-2">
              <Field label="Name"><Input required value={nodeForm.name} onChange={(e) => setNodeForm({ ...nodeForm, name: e.target.value })} /></Field>
              <Field label="Code" hint="Unique in your company"><Input required value={nodeForm.code} onChange={(e) => setNodeForm({ ...nodeForm, code: e.target.value })} /></Field>
            </div>
            <Field label="Description"><Input value={nodeForm.description} onChange={(e) => setNodeForm({ ...nodeForm, description: e.target.value })} /></Field>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" onClick={() => setNodeForm(null)}>Cancel</Button>
              <Button type="submit">Save</Button>
            </div>
          </form>
        )}
      </Modal>

      <Modal open={!!assetForm} title={assetForm?.assetId ? "Edit asset" : "Add asset"} onClose={() => setAssetForm(null)}>
        {assetForm && (
          <form onSubmit={saveAsset} className="space-y-3">
            <ErrorNote error={formError} />
            <div className="grid gap-3 sm:grid-cols-2">
              <Field label="Name"><Input required value={assetForm.name} onChange={(e) => setAssetForm({ ...assetForm, name: e.target.value })} /></Field>
              <Field label="Code"><Input required value={assetForm.code} onChange={(e) => setAssetForm({ ...assetForm, code: e.target.value })} /></Field>
              <Field label="Type">
                <Select value={assetForm.assetTypeCode} onChange={(e) => setAssetForm({ ...assetForm, assetTypeCode: e.target.value })}>
                  {Object.entries(typesByCategory).map(([c, list]) => (
                    <optgroup key={c} label={c}>{list.map((t) => <option key={t.assetTypeCode} value={t.assetTypeCode}>{t.name}</option>)}</optgroup>
                  ))}
                </Select>
              </Field>
              <Field label="Protocol">
                <Select value={assetForm.protocolCode} onChange={(e) => setAssetForm({ ...assetForm, protocolCode: e.target.value })}>
                  <option value="">Not connected</option>
                  {Object.entries(protocolsByFamily).map(([f, list]) => (
                    <optgroup key={f} label={f}>{list.map((p) => <option key={p.protocolCode} value={p.protocolCode}>{p.name}</option>)}</optgroup>
                  ))}
                </Select>
              </Field>
            </div>
            <Field label="Endpoint" hint={port ? `${port.transport}${port.defaultPort ? `, default port ${port.defaultPort}` : ""}${port.notes ? ` · ${port.notes}` : ""}` : "Address, topic or URL of the device"}>
              <Input value={assetForm.endpoint} onChange={(e) => setAssetForm({ ...assetForm, endpoint: e.target.value })} placeholder="opc.tcp://10.0.0.21:4840" />
            </Field>
            <div className="grid gap-3 sm:grid-cols-3">
              <Field label="Vendor"><Input value={assetForm.vendor} onChange={(e) => setAssetForm({ ...assetForm, vendor: e.target.value })} /></Field>
              <Field label="Model"><Input value={assetForm.model} onChange={(e) => setAssetForm({ ...assetForm, model: e.target.value })} /></Field>
              <Field label="Serial no."><Input value={assetForm.serialNo} onChange={(e) => setAssetForm({ ...assetForm, serialNo: e.target.value })} /></Field>
              <Field label="Rated power (kW)"><Input type="number" step="0.01" min={0} value={assetForm.ratedPowerKw} onChange={(e) => setAssetForm({ ...assetForm, ratedPowerKw: e.target.value })} /></Field>
              <Field label="Ideal cycle (s)" hint="For OEE"><Input type="number" step="0.001" min={0} value={assetForm.idealCycleSec} onChange={(e) => setAssetForm({ ...assetForm, idealCycleSec: e.target.value })} /></Field>
              <Field label="Installed on"><Input type="date" value={assetForm.installedOn} onChange={(e) => setAssetForm({ ...assetForm, installedOn: e.target.value })} /></Field>
            </div>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" onClick={() => setAssetForm(null)}>Cancel</Button>
              <Button type="submit">Save</Button>
            </div>
          </form>
        )}
      </Modal>
    </>
  );
}

function groupBy<T>(items: T[], key: (t: T) => string): Record<string, T[]> {
  return items.reduce<Record<string, T[]>>((m, x) => {
    (m[key(x)] ??= []).push(x);
    return m;
  }, {});
}
