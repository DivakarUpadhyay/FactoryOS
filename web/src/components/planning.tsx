"use client";

import { useCallback, useEffect, useState } from "react";
import { Check, Play, Plus, Trash2, Truck } from "lucide-react";
import { api } from "@/lib/api";
import { Badge, Button, ErrorNote, Input, Select, Table, cx, errorText } from "./ui";

type Item = { itemId: string; code: string; name: string; unit: string };
type Asset = { assetId: string; name: string };
type BomRow = { itemId: string; qtyPer: string; scrapPct: string };
type StepRow = { name: string; assetId: string; stdCycleSec: string; setupMinutes: string };

/** Bill of materials and routing of one product, edited together. */
export function BomRoutingEditor({ productId, canEdit, onSaved }: { productId: string; canEdit: boolean; onSaved: () => void }) {
  const [items, setItems] = useState<Item[]>([]);
  const [assets, setAssets] = useState<Asset[]>([]);
  const [bom, setBom] = useState<BomRow[]>([]);
  const [steps, setSteps] = useState<StepRow[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    api<Item[]>("/api/v1/inventory/items").then(setItems).catch(() => setItems([]));
    api<Asset[]>("/api/v1/plant/floor").then(setAssets).catch(() => undefined);
    api<{ itemId: string; qtyPer: number; scrapPct: number }[]>(`/api/v1/production/products/${productId}/bom`)
      .then((b) => setBom(b.map((x) => ({ itemId: x.itemId, qtyPer: String(x.qtyPer), scrapPct: String(x.scrapPct) })))).catch((e) => setError(errorText(e)));
    api<{ name: string; assetId: string | null; stdCycleSec: number | null; setupMinutes: number | null }[]>(`/api/v1/production/products/${productId}/routing`)
      .then((r) => setSteps(r.map((x) => ({ name: x.name, assetId: x.assetId ?? "", stdCycleSec: x.stdCycleSec?.toString() ?? "", setupMinutes: x.setupMinutes?.toString() ?? "" }))))
      .catch((e) => setError(errorText(e)));
  }, [productId]);

  async function save() {
    try {
      await api(`/api/v1/production/products/${productId}/bom`, { method: "PUT", json: bom.filter((b) => b.itemId).map((b) => ({ itemId: b.itemId, qtyPer: Number(b.qtyPer), scrapPct: Number(b.scrapPct || 0) })) });
      await api(`/api/v1/production/products/${productId}/routing`, { method: "PUT", json: steps.filter((s) => s.name.trim()).map((s) => ({
        name: s.name, assetId: s.assetId || null, stdCycleSec: s.stdCycleSec ? Number(s.stdCycleSec) : null, setupMinutes: s.setupMinutes ? Number(s.setupMinutes) : null,
      })) });
      setSaved(true);
      setError(null);
      onSaved();
    } catch (err) {
      setError(errorText(err));
    }
  }
  const unit = (id: string) => items.find((i) => i.itemId === id)?.unit ?? "";

  return (
    <div className="space-y-5 text-sm">
      <ErrorNote error={error} />
      {saved && <div className="rounded-lg bg-ok/10 px-3 py-2 text-ok">Saved. New work orders use this bill of materials and routing.</div>}
      <section>
        <div className="mb-2 flex items-center justify-between"><h3 className="font-semibold">Bill of materials (per 1 unit)</h3>
          {canEdit && <Button variant="outline" className="py-1 text-xs" onClick={() => setBom([...bom, { itemId: "", qtyPer: "1", scrapPct: "0" }])}><Plus size={12} /> Material</Button>}</div>
        {items.length === 0 && <p className="text-muted">Add inventory items first (Inventory → Item).</p>}
        <div className="space-y-2">
          {bom.map((b, i) => (
            <div key={i} className="grid grid-cols-[1fr_110px_90px_auto] items-center gap-2">
              <Select value={b.itemId} disabled={!canEdit} aria-label="Material" onChange={(e) => setBom(bom.map((x, j) => (j === i ? { ...x, itemId: e.target.value } : x)))}>
                <option value="">Choose…</option>{items.map((it) => <option key={it.itemId} value={it.itemId}>{it.code} · {it.name}</option>)}</Select>
              <div className="flex items-center gap-1"><Input type="number" step="any" min="0" value={b.qtyPer} disabled={!canEdit} aria-label="Quantity per unit" onChange={(e) => setBom(bom.map((x, j) => (j === i ? { ...x, qtyPer: e.target.value } : x)))} /><span className="text-xs text-muted">{unit(b.itemId)}</span></div>
              <div className="flex items-center gap-1"><Input type="number" step="any" min="0" max="100" value={b.scrapPct} disabled={!canEdit} aria-label="Scrap %" onChange={(e) => setBom(bom.map((x, j) => (j === i ? { ...x, scrapPct: e.target.value } : x)))} /><span className="text-xs text-muted">%</span></div>
              {canEdit && <Button variant="ghost" className="py-1 text-bad" onClick={() => setBom(bom.filter((_, j) => j !== i))} aria-label="Remove material"><Trash2 size={14} /></Button>}
            </div>
          ))}
          {bom.length === 0 && <p className="text-muted">No materials yet.</p>}
        </div>
      </section>
      <section>
        <div className="mb-2 flex items-center justify-between"><h3 className="font-semibold">Routing (operations in order)</h3>
          {canEdit && <Button variant="outline" className="py-1 text-xs" onClick={() => setSteps([...steps, { name: "", assetId: "", stdCycleSec: "", setupMinutes: "" }])}><Plus size={12} /> Operation</Button>}</div>
        <div className="space-y-2">
          {steps.map((s, i) => (
            <div key={i} className="grid grid-cols-[24px_1fr_1fr_90px_80px_auto] items-center gap-2">
              <span className="text-xs text-muted">{(i + 1) * 10}</span>
              <Input value={s.name} disabled={!canEdit} placeholder="Operation" aria-label="Operation name" onChange={(e) => setSteps(steps.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)))} />
              <Select value={s.assetId} disabled={!canEdit} aria-label="Machine" onChange={(e) => setSteps(steps.map((x, j) => (j === i ? { ...x, assetId: e.target.value } : x)))}>
                <option value="">Any / manual</option>{assets.map((a) => <option key={a.assetId} value={a.assetId}>{a.name}</option>)}</Select>
              <Input type="number" step="any" min="0" value={s.stdCycleSec} disabled={!canEdit} placeholder="cycle s" aria-label="Standard cycle seconds" onChange={(e) => setSteps(steps.map((x, j) => (j === i ? { ...x, stdCycleSec: e.target.value } : x)))} />
              <Input type="number" min="0" value={s.setupMinutes} disabled={!canEdit} placeholder="setup min" aria-label="Setup minutes" onChange={(e) => setSteps(steps.map((x, j) => (j === i ? { ...x, setupMinutes: e.target.value } : x)))} />
              {canEdit && <Button variant="ghost" className="py-1 text-bad" onClick={() => setSteps(steps.filter((_, j) => j !== i))} aria-label="Remove operation"><Trash2 size={14} /></Button>}
            </div>
          ))}
          {steps.length === 0 && <p className="text-muted">No operations yet.</p>}
        </div>
      </section>
      {canEdit && <div className="flex justify-end"><Button onClick={save}>Save bill of materials and routing</Button></div>}
    </div>
  );
}

type Step = { seq: number; name: string; assetName: string | null; status: "Pending" | "Running" | "Done"; goodQty: number | null; scrapQty: number | null };
type Need = { itemId: string; itemCode: string; itemName: string; unit: string; required: number; issued: number; remaining: number; onHand: number; short: number };

/** A work order's operations (start and finish them in order) and its material: required, issued, short; issue it in one click. */
export function WorkOrderDetail({ workOrderId, status, canOperate, onChange }: { workOrderId: string; status: string; canOperate: boolean; onChange: () => void }) {
  const [steps, setSteps] = useState<Step[]>([]);
  const [needs, setNeeds] = useState<Need[]>([]);
  const [qty, setQty] = useState<Record<number, { good: string; scrap: string }>>({});
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const load = useCallback(() => {
    api<Step[]>(`/api/v1/production/work-orders/${workOrderId}/steps`).then(setSteps).catch((e) => setError(errorText(e)));
    api<{ lines: Need[] }>(`/api/v1/production/work-orders/${workOrderId}/materials`).then((m) => setNeeds(m.lines)).catch((e) => setError(errorText(e)));
  }, [workOrderId]);
  useEffect(load, [load]);

  async function step(seq: number, action: "start" | "complete") {
    try {
      const q = qty[seq];
      await api(`/api/v1/production/work-orders/${workOrderId}/steps/${seq}/${action}`, { method: "POST", json: action === "complete" ? { goodQty: Number(q?.good || 0), scrapQty: Number(q?.scrap || 0) } : {} });
      setError(null);
      load();
      onChange();
    } catch (err) {
      setError(errorText(err));
    }
  }
  async function issue() {
    try {
      const r = await api<{ issued: { itemName: string; lotNo: string | null; quantity: number }[]; stillShort: Need[] }>(`/api/v1/production/work-orders/${workOrderId}/issue-materials`, { method: "POST" });
      setNotice(r.issued.length ? `Issued ${r.issued.map((i) => `${i.quantity} ${i.itemName} (lot ${i.lotNo})`).join(", ")}.${r.stillShort.length ? ` Still short: ${r.stillShort.map((s) => `${s.remaining} ${s.unit} ${s.itemName}`).join(", ")}.` : ""}` : "Nothing left to issue.");
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }
  const live = status === "Planned" || status === "Running";

  return (
    <div className="space-y-5 text-sm">
      <ErrorNote error={error} />
      {notice && <div className="rounded-lg bg-ok/10 px-3 py-2 text-ok">{notice}</div>}
      <section>
        <h3 className="mb-2 font-semibold">Operations</h3>
        {steps.length === 0 ? <p className="text-muted">This product has no routing. Add one under Products → BOM & routing.</p> : (
          <Table head={["#", "Operation", "Machine", "Status", "Good / scrap", ""]}>
            {steps.map((s) => (
              <tr key={s.seq}>
                <td className="px-3 py-2 text-xs text-muted">{s.seq}</td>
                <td className="px-3 py-2">{s.name}</td>
                <td className="px-3 py-2 text-xs">{s.assetName ?? "—"}</td>
                <td className="px-3 py-2"><Badge tone={s.status === "Done" ? "ok" : s.status === "Running" ? "warn" : "muted"}>{s.status}</Badge></td>
                <td className="px-3 py-2 tabular-nums">{s.status === "Done" ? `${s.goodQty} / ${s.scrapQty}` : s.status === "Running" && canOperate ? (
                  <div className="flex gap-1"><Input className="w-20 py-1" type="number" min="0" placeholder="good" aria-label={`Good quantity for operation ${s.seq}`} value={qty[s.seq]?.good ?? ""} onChange={(e) => setQty({ ...qty, [s.seq]: { good: e.target.value, scrap: qty[s.seq]?.scrap ?? "" } })} />
                    <Input className="w-20 py-1" type="number" min="0" placeholder="scrap" aria-label={`Scrap quantity for operation ${s.seq}`} value={qty[s.seq]?.scrap ?? ""} onChange={(e) => setQty({ ...qty, [s.seq]: { good: qty[s.seq]?.good ?? "", scrap: e.target.value } })} /></div>
                ) : "—"}</td>
                <td className="px-3 py-2 text-right">{canOperate && status === "Running" && (
                  s.status === "Pending" ? <Button variant="outline" className="py-1 text-xs" onClick={() => step(s.seq, "start")}><Play size={12} /> Start</Button>
                    : s.status === "Running" ? <Button className="py-1 text-xs" onClick={() => step(s.seq, "complete")}><Check size={12} /> Done</Button> : null)}</td>
              </tr>
            ))}
          </Table>
        )}
        {status === "Planned" && steps.length > 0 && <p className="mt-2 text-xs text-muted">Start the work order to run its operations.</p>}
      </section>
      <section>
        <div className="mb-2 flex items-center justify-between"><h3 className="font-semibold">Material</h3>
          {canOperate && live && needs.some((n) => n.remaining > 0) && <Button variant="outline" className="py-1 text-xs" onClick={issue}><Truck size={12} /> Issue material (FIFO)</Button>}</div>
        {needs.length === 0 ? <p className="text-muted">This product has no bill of materials.</p> : (
          <Table head={["Material", "Required", "Issued", "Still needed", "In stock", ""]}>
            {needs.map((n) => (
              <tr key={n.itemId} className={cx(n.short > 0 && "bg-bad/5")}>
                <td className="px-3 py-2">{n.itemName}<div className="font-mono text-[11px] text-muted">{n.itemCode}</div></td>
                <td className="px-3 py-2 tabular-nums">{n.required} {n.unit}</td>
                <td className="px-3 py-2 tabular-nums">{n.issued}</td>
                <td className="px-3 py-2 tabular-nums">{n.remaining}</td>
                <td className="px-3 py-2 tabular-nums">{n.onHand}</td>
                <td className="px-3 py-2">{n.short > 0 ? <Badge tone="bad">short {n.short}</Badge> : n.remaining === 0 ? <Badge tone="ok">issued</Badge> : <Badge tone="info">available</Badge>}</td>
              </tr>
            ))}
          </Table>
        )}
      </section>
    </div>
  );
}
