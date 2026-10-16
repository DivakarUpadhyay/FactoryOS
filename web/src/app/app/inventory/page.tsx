"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import Link from "next/link";
import QRCode from "qrcode";
import { ArrowDownToLine, ArrowUpFromLine, Factory, GitBranch, Plus, Printer, ScanLine, SlidersHorizontal } from "lucide-react";
import { api } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { Badge, Button, Card, ErrorNote, Field, Input, Modal, PageHeader, Select, Stat, Table, cx, errorText } from "@/components/ui";

type Item = { itemId: string; code: string; name: string; category: string; unit: string; minStock: number; reorderQty: number | null; location: string | null; barcode: string | null; productId: string | null; isActive: boolean; onHand: number; lots: number; low: boolean };
type Lot = { lotId: string; itemId: string; itemCode: string; itemName: string; lotNo: string; status: string; onHand: number; supplier: string | null; supplierLot: string | null; expiresOn: string | null; workOrderId: string | null; workOrderCode: string | null; createdAt: string };
type Movement = { movementId: number; itemId: string; itemName: string; lotNo: string | null; type: string; quantity: number; workOrderCode: string | null; maintWoCode: string | null; reference: string | null; userName: string | null; ts: string };
type Step = { kind: string; title: string; detail: string | null; at: string | null; link: string | null };
type Trace = { lot: Lot; produced: Step[]; inputs: Lot[]; usedIn: Step[]; quality: Step[]; events: Step[]; movements: Movement[] };
type Wo = { workOrderId: string; code: string; productName: string; status: string; produced: number };
type Product = { productId: string; code: string; name: string };
type Action = "receive" | "issue" | "produce" | "adjust" | null;

const when = (iso: string | null) => (iso ? new Date(iso.endsWith("Z") ? iso : iso + "Z").toLocaleString([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" }) : "—");
const qty = (n: number) => (Number.isInteger(n) ? n.toLocaleString() : n.toFixed(3).replace(/0+$/, ""));

export default function InventoryPage() {
  const { can } = useAuth();
  const operate = can("production.operate");
  const manage = can("production.manage");
  const [tab, setTab] = useState<"items" | "lots" | "moves" | "trace">("items");
  const [items, setItems] = useState<Item[]>([]);
  const [lots, setLots] = useState<Lot[]>([]);
  const [moves, setMoves] = useState<Movement[]>([]);
  const [orders, setOrders] = useState<Wo[]>([]);
  const [products, setProducts] = useState<Product[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [scan, setScan] = useState("");
  const [traceLot, setTraceLot] = useState("");
  const [trace, setTrace] = useState<Trace | null>(null);
  const [action, setAction] = useState<Action>(null);
  const [form, setForm] = useState<Record<string, string>>({});
  const [newItem, setNewItem] = useState(false);
  const [label, setLabel] = useState<Lot | null>(null);

  const load = useCallback(() => {
    Promise.all([api<Item[]>("/api/v1/inventory/items"), api<Lot[]>("/api/v1/inventory/lots"), api<Movement[]>("/api/v1/inventory/movements")])
      .then(([i, l, m]) => { setItems(i); setLots(l); setMoves(m); }).catch((e) => setError(errorText(e)));
  }, []);
  useEffect(() => {
    load();
    api<Wo[]>("/api/v1/production/work-orders").then(setOrders).catch(() => undefined);
    api<Product[]>("/api/v1/production/products").then(setProducts).catch(() => undefined);
  }, [load]);

  async function doScan(e: React.FormEvent) {
    e.preventDefault();
    if (!scan.trim()) return;
    try {
      const r = await api<{ kind: string; item: Item | null; lot: Lot | null }>(`/api/v1/inventory/lookup?code=${encodeURIComponent(scan.trim())}`);
      setError(null);
      if (r.kind === "lot" && r.lot) {
        setNotice(`Lot ${r.lot.lotNo}: ${r.lot.itemName}, ${qty(r.lot.onHand)} on hand (${r.lot.status}).`);
        setTraceLot(r.lot.lotNo);
        await runTrace(r.lot.lotNo);
        setTab("trace");
      } else if (r.item) {
        setNotice(`${r.item.name}: ${qty(r.item.onHand)} ${r.item.unit} on hand${r.item.low ? " — below minimum" : ""}${r.item.location ? `, ${r.item.location}` : ""}.`);
        setForm({ itemId: r.item.itemId });
      }
      setScan("");
    } catch (err) {
      setError(errorText(err));
    }
  }
  async function runTrace(lotNo: string) {
    try {
      setTrace(await api<Trace>(`/api/v1/trace?lot=${encodeURIComponent(lotNo)}`));
      setError(null);
    } catch (err) {
      setTrace(null);
      setError(errorText(err));
    }
  }
  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const n = (k: string) => (form[k] ? Number(form[k]) : null);
    const s = (k: string) => form[k] || null;
    try {
      if (action === "receive") await api("/api/v1/inventory/receive", { method: "POST", json: { itemId: form.itemId, quantity: n("quantity"), lotNo: s("lotNo"), supplier: s("supplier"), supplierLot: s("supplierLot"), expiresOn: s("expiresOn"), reference: s("reference") } });
      if (action === "issue") await api("/api/v1/inventory/issue", { method: "POST", json: { itemId: form.itemId, quantity: n("quantity"), lotId: s("lotId"), workOrderId: s("workOrderId"), reference: s("reference") } });
      if (action === "produce") await api("/api/v1/inventory/produce", { method: "POST", json: { workOrderId: form.workOrderId, quantity: n("quantity"), lotNo: s("lotNo") } });
      if (action === "adjust") await api("/api/v1/inventory/adjust", { method: "POST", json: { itemId: form.itemId, lotId: s("lotId"), quantity: n("quantity"), type: form.type ?? "Adjust", reference: s("reference") } });
      setNotice(`${action![0].toUpperCase()}${action!.slice(1)} saved.`);
      setAction(null);
      setForm({});
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }
  async function createItem(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api("/api/v1/inventory/items", { method: "POST", json: { code: form.code, name: form.name, category: form.category ?? "Raw", unit: form.unit || "pcs", minStock: Number(form.minStock || 0), reorderQty: form.reorderQty ? Number(form.reorderQty) : null, location: form.location || null, barcode: form.barcode || null, productId: form.productId || null } });
      setNewItem(false);
      setForm({});
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }

  const low = items.filter((i) => i.low);
  const itemLots = lots.filter((l) => l.itemId === form.itemId && (l.status === "Available" || action === "adjust"));
  const F = (k: string, label: string, props: React.InputHTMLAttributes<HTMLInputElement> = {}) => (
    <Field label={label}><Input value={form[k] ?? ""} onChange={(e) => setForm({ ...form, [k]: e.target.value })} {...props} /></Field>
  );
  const ItemSelect = () => (
    <Field label="Item"><Select required value={form.itemId ?? ""} onChange={(e) => setForm({ ...form, itemId: e.target.value, lotId: "" })}>
      <option value="">Choose…</option>{items.map((i) => <option key={i.itemId} value={i.itemId}>{i.code} · {i.name}</option>)}</Select></Field>
  );

  return (
    <>
      <PageHeader title="Inventory" sub="Raw material, spare parts and finished goods by lot; every movement recorded for traceability."
        actions={operate && (
          <div className="flex flex-wrap gap-2">
            <Button variant="outline" className="py-1" onClick={() => setAction("receive")}><ArrowDownToLine size={14} /> Receive</Button>
            <Button variant="outline" className="py-1" onClick={() => setAction("issue")}><ArrowUpFromLine size={14} /> Issue</Button>
            <Button variant="outline" className="py-1" onClick={() => setAction("produce")}><Factory size={14} /> Book production</Button>
            {manage && <Button variant="outline" className="py-1" onClick={() => setAction("adjust")}><SlidersHorizontal size={14} /> Adjust</Button>}
          </div>
        )} />
      <ErrorNote error={error} />
      {notice && <div className="mb-4 rounded-lg bg-ok/10 px-3 py-2 text-sm text-ok">{notice}</div>}
      <form onSubmit={doScan} className="mb-6 flex gap-2">
        <div className="relative flex-1">
          <ScanLine size={16} className="absolute left-3 top-1/2 -translate-y-1/2 text-muted" />
          <Input value={scan} onChange={(e) => setScan(e.target.value)} placeholder="Scan or type a barcode, item code or lot number" className="pl-9" aria-label="Scan" />
        </div>
        <Button type="submit">Look up</Button>
      </form>
      <div className="mb-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <Stat label="Items" value={items.length} sub={`${items.filter((i) => i.category === "Spare").length} spare parts`} />
        <Stat label="Below minimum" value={low.length} sub={low.slice(0, 2).map((i) => i.name).join(", ") || "all stocked"} />
        <Stat label="Open lots" value={lots.filter((l) => l.status === "Available").length} sub={`${lots.filter((l) => l.status === "Quarantine").length} in quarantine`} />
        <Stat label="Movements" value={moves.length} sub="latest 300" />
      </div>
      <div className="mb-4 flex gap-1 overflow-x-auto border-b border-line">
        {([["items", "Stock"], ["lots", "Lots"], ["moves", "Movements"], ["trace", "Traceability"]] as const).map(([t, l]) => (
          <button key={t} onClick={() => setTab(t)} className={cx("-mb-px whitespace-nowrap border-b-2 px-4 py-2 text-sm", tab === t ? "border-brand font-medium text-brand" : "border-transparent text-muted hover:text-ink")}>{l}</button>
        ))}
      </div>

      {tab === "items" && (
        <Card title="Stock on hand" actions={manage && <Button variant="outline" className="py-1" onClick={() => { setForm({ category: "Raw" }); setNewItem(true); }}><Plus size={14} /> Item</Button>}>
          <Table head={["Item", "Category", "On hand", "Minimum", "Location", "Lots"]} empty={items.length === 0}>
            {items.map((i) => (
              <tr key={i.itemId} className={i.low ? "bg-warn/5" : ""}>
                <td className="px-3 py-2"><div className="font-medium">{i.name}</div><div className="font-mono text-[11px] text-muted">{i.code}{i.barcode && i.barcode !== i.code ? ` · ${i.barcode}` : ""}</div></td>
                <td className="px-3 py-2"><Badge tone={i.category === "Spare" ? "warn" : i.category === "Finished" ? "ok" : "info"}>{i.category}</Badge></td>
                <td className="px-3 py-2 font-medium tabular-nums">{qty(i.onHand)} {i.unit} {i.low && <Badge tone="bad">low</Badge>}</td>
                <td className="px-3 py-2 tabular-nums">{qty(i.minStock)}</td>
                <td className="px-3 py-2 text-sm">{i.location ?? "—"}</td>
                <td className="px-3 py-2 tabular-nums">{i.lots}</td>
              </tr>
            ))}
          </Table>
        </Card>
      )}

      {tab === "lots" && (
        <Card title="Lots">
          <Table head={["Lot", "Item", "On hand", "Status", "From", "Expires", ""]} empty={lots.length === 0}>
            {lots.map((l) => (
              <tr key={l.lotId}>
                <td className="px-3 py-2 font-mono text-xs">{l.lotNo}</td>
                <td className="px-3 py-2">{l.itemName}</td>
                <td className="px-3 py-2 tabular-nums">{qty(l.onHand)}</td>
                <td className="px-3 py-2"><Badge tone={l.status === "Available" ? "ok" : l.status === "Quarantine" ? "warn" : "muted"}>{l.status}</Badge></td>
                <td className="px-3 py-2 text-xs">{l.workOrderCode ? `work order ${l.workOrderCode}` : l.supplier ?? "—"}</td>
                <td className="px-3 py-2 text-xs">{l.expiresOn ?? "—"}</td>
                <td className="whitespace-nowrap px-3 py-2 text-right">
                  <Button variant="ghost" className="py-1 text-xs" onClick={() => { setTraceLot(l.lotNo); runTrace(l.lotNo); setTab("trace"); }}><GitBranch size={12} /> Trace</Button>
                  <Button variant="ghost" className="py-1 text-xs" onClick={() => setLabel(l)}><Printer size={12} /> Label</Button>
                </td>
              </tr>
            ))}
          </Table>
        </Card>
      )}

      {tab === "moves" && (
        <Card title="Movements">
          <Table head={["When", "Type", "Item", "Lot", "Quantity", "For", "By"]} empty={moves.length === 0}>
            {moves.map((m) => (
              <tr key={m.movementId}>
                <td className="whitespace-nowrap px-3 py-2 text-xs">{when(m.ts)}</td>
                <td className="px-3 py-2"><Badge tone={m.quantity > 0 ? "ok" : "warn"}>{m.type}</Badge></td>
                <td className="px-3 py-2">{m.itemName}</td>
                <td className="px-3 py-2 font-mono text-xs">{m.lotNo ?? "—"}</td>
                <td className={cx("px-3 py-2 font-medium tabular-nums", m.quantity < 0 && "text-warn")}>{m.quantity > 0 ? "+" : ""}{qty(m.quantity)}</td>
                <td className="px-3 py-2 text-xs">{m.workOrderCode ? `WO ${m.workOrderCode}` : m.maintWoCode ? `maint. ${m.maintWoCode}` : m.reference ?? "—"}</td>
                <td className="px-3 py-2 text-xs">{m.userName ?? "—"}</td>
              </tr>
            ))}
          </Table>
        </Card>
      )}

      {tab === "trace" && (
        <>
          <form onSubmit={(e) => { e.preventDefault(); runTrace(traceLot); }} className="mb-4 flex gap-2">
            <Input value={traceLot} onChange={(e) => setTraceLot(e.target.value)} placeholder="Lot number, e.g. FG260116-1234" aria-label="Lot number" />
            <Button type="submit"><GitBranch size={14} /> Trace</Button>
          </form>
          {trace && <TraceView t={trace} onTrace={(lot) => { setTraceLot(lot); runTrace(lot); }} />}
        </>
      )}

      <Modal open={!!action} title={{ receive: "Receive goods", issue: "Issue material", produce: "Book production into stock", adjust: "Adjust or scrap stock" }[action ?? "receive"]} onClose={() => setAction(null)}>
        <form onSubmit={submit} className="grid gap-4 sm:grid-cols-2">
          {action !== "produce" && <ItemSelect />}
          {action === "produce" && (
            <Field label="Work order"><Select required value={form.workOrderId ?? ""} onChange={(e) => setForm({ ...form, workOrderId: e.target.value, quantity: String(orders.find((o) => o.workOrderId === e.target.value)?.produced ?? "") })}>
              <option value="">Choose…</option>{orders.filter((o) => o.status !== "Cancelled").map((o) => <option key={o.workOrderId} value={o.workOrderId}>{o.code} · {o.productName} ({o.produced} made)</option>)}</Select></Field>
          )}
          {F("quantity", "Quantity", { required: true, type: "number", step: "any" })}
          {(action === "issue" || action === "adjust") && (
            <Field label="Lot"><Select value={form.lotId ?? ""} onChange={(e) => setForm({ ...form, lotId: e.target.value })}>
              <option value="">{action === "issue" ? "Any (no lot)" : "No lot"}</option>{itemLots.map((l) => <option key={l.lotId} value={l.lotId}>{l.lotNo} ({qty(l.onHand)})</option>)}</Select></Field>
          )}
          {action === "issue" && (
            <Field label="To work order" hint="Links the material into the product's genealogy"><Select value={form.workOrderId ?? ""} onChange={(e) => setForm({ ...form, workOrderId: e.target.value })}>
              <option value="">—</option>{orders.filter((o) => o.status === "Running" || o.status === "Planned").map((o) => <option key={o.workOrderId} value={o.workOrderId}>{o.code} · {o.productName}</option>)}</Select></Field>
          )}
          {action === "adjust" && <Field label="Type"><Select value={form.type ?? "Adjust"} onChange={(e) => setForm({ ...form, type: e.target.value })}><option>Adjust</option><option>Scrap</option></Select></Field>}
          {(action === "receive" || action === "produce") && F("lotNo", "Lot number", { placeholder: "Leave empty to number automatically" })}
          {action === "receive" && <>{F("supplier", "Supplier")}{F("supplierLot", "Supplier lot")}{F("expiresOn", "Expires on", { type: "date" })}</>}
          {action !== "produce" && F("reference", "Reference", { placeholder: "PO, delivery note, count…" })}
          <div className="flex justify-end gap-2 sm:col-span-2"><Button type="button" variant="ghost" onClick={() => setAction(null)}>Cancel</Button><Button type="submit">Save</Button></div>
        </form>
      </Modal>
      <Modal open={newItem} title="New item" onClose={() => setNewItem(false)}>
        <form onSubmit={createItem} className="grid gap-4 sm:grid-cols-2">
          {F("code", "Code", { required: true })}{F("name", "Name", { required: true })}
          <Field label="Category"><Select value={form.category ?? "Raw"} onChange={(e) => setForm({ ...form, category: e.target.value })}>{["Raw", "WIP", "Finished", "Spare", "Consumable"].map((c) => <option key={c}>{c}</option>)}</Select></Field>
          {F("unit", "Unit", { placeholder: "pcs, kg, m…" })}{F("minStock", "Minimum stock", { type: "number", step: "any", min: "0" })}{F("reorderQty", "Reorder quantity", { type: "number", step: "any", min: "0" })}
          {F("location", "Location")}{F("barcode", "Barcode")}
          {form.category === "Finished" && <Field label="Made as product"><Select value={form.productId ?? ""} onChange={(e) => setForm({ ...form, productId: e.target.value })}><option value="">—</option>{products.map((p) => <option key={p.productId} value={p.productId}>{p.name}</option>)}</Select></Field>}
          <div className="flex justify-end gap-2 sm:col-span-2"><Button type="button" variant="ghost" onClick={() => setNewItem(false)}>Cancel</Button><Button type="submit">Create</Button></div>
        </form>
      </Modal>
      <Modal open={!!label} title="Lot label" onClose={() => setLabel(null)}>{label && <LotLabel lot={label} />}</Modal>
    </>
  );
}

function TraceView({ t, onTrace }: { t: Trace; onTrace: (lot: string) => void }) {
  const Steps = ({ steps, empty }: { steps: Step[]; empty: string }) => steps.length === 0 ? <p className="text-sm text-muted">{empty}</p> : (
    <ol className="space-y-2 border-l border-line pl-4">
      {steps.map((s, i) => (
        <li key={i} className="relative text-sm">
          <span className={cx("absolute -left-[21px] top-1.5 h-2.5 w-2.5 rounded-full", s.kind === "alarm" || s.kind === "ncr" ? "bg-bad" : s.kind === "downtime" ? "bg-warn" : "bg-brand")} />
          <div className="font-medium">{s.link ? <Link href={s.link} className="hover:text-brand">{s.title}</Link> : s.title}</div>
          {s.detail && <div className="text-xs text-muted">{s.detail}</div>}
          {s.at && <div className="text-[11px] text-muted">{when(s.at)}</div>}
        </li>
      ))}
    </ol>
  );
  return (
    <div className="grid gap-4 lg:grid-cols-2">
      <Card title={`Lot ${t.lot.lotNo}`} className="lg:col-span-2">
        <p className="text-sm">{t.lot.itemName} · <span className="font-medium">{qty(t.lot.onHand)}</span> on hand · <Badge tone={t.lot.status === "Available" ? "ok" : "muted"}>{t.lot.status}</Badge></p>
      </Card>
      <Card title="How it was made"><Steps steps={t.produced} empty="—" /></Card>
      <Card title="Material that went in">
        {t.inputs.length === 0 ? <p className="text-sm text-muted">No input lots recorded.</p> : (
          <ul className="space-y-1 text-sm">{t.inputs.map((l) => <li key={l.lotId}><button className="font-mono text-brand hover:underline" onClick={() => onTrace(l.lotNo)}>{l.lotNo}</button> · {l.itemName}{l.supplier ? ` · ${l.supplier}` : ""}</li>)}</ul>
        )}
      </Card>
      <Card title="Where it was used"><Steps steps={t.usedIn} empty="Not used yet." /></Card>
      <Card title="Quality records"><Steps steps={t.quality} empty="No measurements or non-conformances for this lot." /></Card>
      <Card title="Machine events while it was made" className="lg:col-span-2"><Steps steps={t.events} empty="No alarms or stops during the run." /></Card>
    </div>
  );
}

function LotLabel({ lot }: { lot: Lot }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    if (canvas.current) QRCode.toCanvas(canvas.current, lot.lotNo, { width: 160, margin: 1 }).catch(() => undefined);
  }, [lot]);
  return (
    <div>
      <div id="lot-label" className="flex items-center gap-4 rounded-lg border border-line bg-white p-4 text-black">
        <canvas ref={canvas} aria-label={`QR code for lot ${lot.lotNo}`} />
        <div className="text-sm">
          <div className="text-xs uppercase tracking-wide">Lot</div>
          <div className="font-mono text-lg font-bold">{lot.lotNo}</div>
          <div className="mt-1">{lot.itemName}</div>
          <div className="text-xs">{lot.itemCode}{lot.expiresOn ? ` · exp. ${lot.expiresOn}` : ""}</div>
        </div>
      </div>
      <div className="mt-4 flex justify-end"><Button onClick={() => window.print()}><Printer size={14} /> Print</Button></div>
    </div>
  );
}
