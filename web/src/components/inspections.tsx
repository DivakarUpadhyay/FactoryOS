"use client";

import { useCallback, useEffect, useState } from "react";
import { ClipboardList, Plus, Trash2 } from "lucide-react";
import { api } from "@/lib/api";
import { Badge, Button, Card, ErrorNote, Field, Input, Modal, Select, Table, cx, errorText } from "./ui";

type Item = { seq: number; text: string; charId: string | null; charName: string | null; unit: string | null; lsl: number | null; usl: number | null };
type Plan = { planId: string; productId: string; productName: string; name: string; trigger: "FirstPiece" | "EveryN" | "Final"; everyN: number | null; createNcrOnFail: boolean; isActive: boolean; items: Item[] };
type Due = { planId: string; planName: string; trigger: string; workOrderId: string; workOrderCode: string; productName: string; lotNo: string | null; reason: string };
type Record = { inspectionId: string; planName: string; workOrderCode: string | null; lotNo: string | null; result: "Pass" | "Fail"; ncId: string | null; inspectorName: string | null; at: string };
type Product = { productId: string; name: string };
type Char = { charId: string; name: string; unit: string };

export type InspPlan = Plan;
export type InspDue = Due;

const when = { FirstPiece: "First piece", EveryN: "Every N parts", Final: "Final" } as const;

/** Inspections: what is due now, recording an inspection, the plans, and the history. */
export function InspectionsTab({ canInspect, canManage }: { canInspect: boolean; canManage: boolean }) {
  const [due, setDue] = useState<Due[]>([]);
  const [plans, setPlans] = useState<Plan[]>([]);
  const [history, setHistory] = useState<Record[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [doing, setDoing] = useState<{ plan: Plan; due?: Due } | null>(null);
  const [editing, setEditing] = useState<Partial<Plan> | null>(null);

  const load = useCallback(() => {
    Promise.all([api<Due[]>("/api/v1/quality/inspections/due"), api<Plan[]>("/api/v1/quality/inspection-plans"), api<Record[]>("/api/v1/quality/inspections")])
      .then(([d, p, h]) => { setDue(d); setPlans(p); setHistory(h); }).catch((e) => setError(errorText(e)));
  }, []);
  useEffect(() => {
    load();
    const t = setInterval(load, 30_000);
    return () => clearInterval(t);
  }, [load]);

  return (
    <>
      <ErrorNote error={error} />
      {notice && <div className="mb-4 rounded-lg bg-ok/10 px-3 py-2 text-sm text-ok">{notice}</div>}
      <Card title={`Due now (${due.length})`} className="mb-6">
        {due.length === 0 ? <p className="text-sm text-muted">Nothing due. Inspections appear here when an order starts, passes every N parts, or completes.</p> : (
          <Table head={["Inspection", "Work order", "Why", ""]}>
            {due.map((d) => (
              <tr key={`${d.planId}-${d.workOrderId}`}>
                <td className="px-3 py-2 font-medium">{d.planName}<div className="text-xs text-muted">{d.productName}</div></td>
                <td className="px-3 py-2 font-mono text-xs">{d.workOrderCode}{d.lotNo && <div className="text-muted">lot {d.lotNo}</div>}</td>
                <td className="px-3 py-2 text-sm">{d.reason}</td>
                <td className="px-3 py-2 text-right">{canInspect && <Button className="py-1 text-xs" onClick={() => { const p = plans.find((x) => x.planId === d.planId); if (p) setDoing({ plan: p, due: d }); }}><ClipboardList size={12} /> Inspect</Button>}</td>
              </tr>
            ))}
          </Table>
        )}
      </Card>
      <div className="grid gap-6 lg:grid-cols-2">
        <Card title="Inspection plans" actions={canManage && <Button variant="outline" className="py-1" onClick={() => setEditing({ trigger: "FirstPiece", createNcrOnFail: true, isActive: true, items: [] })}><Plus size={14} /> Plan</Button>}>
          <Table head={["Plan", "Product", "When", "Checks", ""]} empty={plans.length === 0}>
            {plans.map((p) => (
              <tr key={p.planId} className={p.isActive ? "" : "opacity-50"}>
                <td className="px-3 py-2 font-medium">{p.name}</td>
                <td className="px-3 py-2 text-sm">{p.productName}</td>
                <td className="px-3 py-2 text-sm">{p.trigger === "EveryN" ? `Every ${p.everyN} parts` : when[p.trigger]}</td>
                <td className="px-3 py-2 tabular-nums">{p.items.length}</td>
                <td className="whitespace-nowrap px-3 py-2 text-right">
                  {canInspect && <Button variant="ghost" className="py-1 text-xs" onClick={() => setDoing({ plan: p })}>Inspect</Button>}
                  {canManage && <Button variant="ghost" className="py-1 text-xs" onClick={() => setEditing(structuredClone(p))}>Edit</Button>}
                </td>
              </tr>
            ))}
          </Table>
        </Card>
        <Card title="Recent inspections">
          <Table head={["When", "Plan", "Order / lot", "Result", "By"]} empty={history.length === 0}>
            {history.slice(0, 30).map((h) => (
              <tr key={h.inspectionId}>
                <td className="whitespace-nowrap px-3 py-2 text-xs">{new Date(h.at + "Z").toLocaleString([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" })}</td>
                <td className="px-3 py-2 text-sm">{h.planName}</td>
                <td className="px-3 py-2 font-mono text-xs">{h.workOrderCode ?? "—"}{h.lotNo && ` · ${h.lotNo}`}</td>
                <td className="px-3 py-2"><Badge tone={h.result === "Pass" ? "ok" : "bad"}>{h.result}</Badge>{h.ncId && <div className="text-[11px] text-muted">NCR opened</div>}</td>
                <td className="px-3 py-2 text-xs">{h.inspectorName ?? "—"}</td>
              </tr>
            ))}
          </Table>
        </Card>
      </div>
      <Modal open={!!doing} title={doing ? `${doing.plan.name}${doing.due ? ` · ${doing.due.workOrderCode}` : ""}` : ""} onClose={() => setDoing(null)} wide>
        {doing && <Perform plan={doing.plan} due={doing.due} onDone={(msg) => { setDoing(null); setNotice(msg); load(); }} />}
      </Modal>
      <Modal open={!!editing} title={editing?.planId ? "Edit inspection plan" : "New inspection plan"} onClose={() => setEditing(null)} wide>
        {editing && <PlanEditor plan={editing} onSaved={() => { setEditing(null); load(); }} />}
      </Modal>
    </>
  );
}

export function Perform({ plan, due, onDone }: { plan: Plan; due?: Due; onDone: (msg: string) => void }) {
  const [values, setValues] = useState<{ [seq: number]: string }>({});
  const [pass, setPass] = useState<{ [seq: number]: boolean | undefined }>({});
  const [lot, setLot] = useState(due?.lotNo ?? "");
  const [note, setNote] = useState("");
  const [error, setError] = useState<string | null>(null);
  const out = (it: Item) => {
    const v = Number(values[it.seq]);
    return values[it.seq] !== undefined && values[it.seq] !== "" && ((it.lsl !== null && v < it.lsl) || (it.usl !== null && v > it.usl));
  };
  async function submit(e: React.FormEvent) {
    e.preventDefault();
    try {
      const r = await api<{ result: string; ncId: string | null; failed: string[] }>("/api/v1/quality/inspections", { method: "POST", json: {
        planId: plan.planId, workOrderId: due?.workOrderId ?? null, lotNo: lot || null, note: note || null,
        results: plan.items.map((it) => ({ seq: it.seq, value: it.charId ? Number(values[it.seq]) : null, passed: it.charId ? null : pass[it.seq] ?? null })),
      } });
      onDone(r.result === "Pass" ? `${plan.name}: passed.` : `${plan.name}: FAILED (${r.failed.join(", ")})${r.ncId ? " — a non-conformance was opened." : "."}`);
    } catch (err) {
      setError(errorText(err));
    }
  }
  return (
    <form onSubmit={submit} className="space-y-4 text-sm">
      <ErrorNote error={error} />
      <ul className="space-y-3">
        {plan.items.map((it) => (
          <li key={it.seq} className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-line p-3">
            <div><div className="font-medium">{it.text}</div>{it.charId && <div className="text-xs text-muted">spec {it.lsl ?? "—"} – {it.usl ?? "—"} {it.unit}</div>}</div>
            {it.charId ? (
              <div className="flex items-center gap-2"><Input required type="number" step="any" className={cx("w-32", out(it) && "border-bad")} value={values[it.seq] ?? ""} aria-label={`Measured ${it.text}`}
                onChange={(e) => setValues({ ...values, [it.seq]: e.target.value })} />{out(it) && <Badge tone="bad">out of spec</Badge>}</div>
            ) : (
              <div className="flex gap-1">
                <Button type="button" variant={pass[it.seq] === true ? "primary" : "outline"} className="py-1 text-xs" onClick={() => setPass({ ...pass, [it.seq]: true })}>Pass</Button>
                <Button type="button" variant={pass[it.seq] === false ? "danger" : "outline"} className="py-1 text-xs" onClick={() => setPass({ ...pass, [it.seq]: false })}>Fail</Button>
              </div>
            )}
          </li>
        ))}
      </ul>
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label="Lot"><Input value={lot} onChange={(e) => setLot(e.target.value)} /></Field>
        <Field label="Note"><Input value={note} maxLength={300} onChange={(e) => setNote(e.target.value)} /></Field>
      </div>
      <div className="flex justify-end"><Button type="submit" disabled={plan.items.some((it) => !it.charId && pass[it.seq] === undefined)}>Record inspection</Button></div>
    </form>
  );
}

function PlanEditor({ plan, onSaved }: { plan: Partial<Plan>; onSaved: () => void }) {
  const [p, setP] = useState(plan);
  const [items, setItems] = useState<{ text: string; charId: string }[]>((plan.items ?? []).map((i) => ({ text: i.text, charId: i.charId ?? "" })));
  const [products, setProducts] = useState<Product[]>([]);
  const [chars, setChars] = useState<Char[]>([]);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    api<Product[]>("/api/v1/production/products").then(setProducts).catch(() => undefined);
    api<Char[]>("/api/v1/quality/characteristics").then(setChars).catch(() => undefined);
  }, []);
  async function save(e: React.FormEvent) {
    e.preventDefault();
    const body = { productId: p.productId, name: p.name, trigger: p.trigger, everyN: p.trigger === "EveryN" ? p.everyN : null, createNcrOnFail: p.createNcrOnFail ?? true,
      isActive: p.isActive ?? true, items: items.filter((i) => i.text.trim()).map((i) => ({ text: i.text, charId: i.charId || null })) };
    try {
      await api(p.planId ? `/api/v1/quality/inspection-plans/${p.planId}` : "/api/v1/quality/inspection-plans", { method: p.planId ? "PUT" : "POST", json: body });
      onSaved();
    } catch (err) {
      setError(errorText(err));
    }
  }
  return (
    <form onSubmit={save} className="space-y-4 text-sm">
      <ErrorNote error={error} />
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label="Name"><Input required value={p.name ?? ""} onChange={(e) => setP({ ...p, name: e.target.value })} /></Field>
        <Field label="Product"><Select required value={p.productId ?? ""} onChange={(e) => setP({ ...p, productId: e.target.value })}><option value="">Choose…</option>{products.map((x) => <option key={x.productId} value={x.productId}>{x.name}</option>)}</Select></Field>
        <Field label="When"><Select value={p.trigger} onChange={(e) => setP({ ...p, trigger: e.target.value as Plan["trigger"] })}><option value="FirstPiece">First piece of each order</option><option value="EveryN">Every N parts</option><option value="Final">When the order completes</option></Select></Field>
        {p.trigger === "EveryN" && <Field label="N (parts)"><Input required type="number" min="1" value={p.everyN ?? ""} onChange={(e) => setP({ ...p, everyN: Number(e.target.value) })} /></Field>}
      </div>
      <div className="space-y-2">
        <div className="flex items-center justify-between"><span className="font-medium">Checks</span><Button type="button" variant="outline" className="py-1 text-xs" onClick={() => setItems([...items, { text: "", charId: "" }])}><Plus size={12} /> Check</Button></div>
        {items.map((it, i) => (
          <div key={i} className="grid grid-cols-[1fr_200px_auto] gap-2">
            <Input value={it.text} placeholder="What to check" aria-label="Check" onChange={(e) => setItems(items.map((x, j) => (j === i ? { ...x, text: e.target.value } : x)))} />
            <Select value={it.charId} aria-label="Measured characteristic" onChange={(e) => setItems(items.map((x, j) => (j === i ? { ...x, charId: e.target.value } : x)))}>
              <option value="">Pass / fail</option>{chars.map((c) => <option key={c.charId} value={c.charId}>Measure: {c.name}</option>)}</Select>
            <Button type="button" variant="ghost" className="text-bad" onClick={() => setItems(items.filter((_, j) => j !== i))} aria-label="Remove check"><Trash2 size={14} /></Button>
          </div>
        ))}
      </div>
      <div className="flex flex-wrap gap-4">
        <label className="flex items-center gap-2"><input type="checkbox" checked={p.createNcrOnFail ?? true} onChange={(e) => setP({ ...p, createNcrOnFail: e.target.checked })} /> Open a non-conformance when it fails</label>
        <label className="flex items-center gap-2"><input type="checkbox" checked={p.isActive ?? true} onChange={(e) => setP({ ...p, isActive: e.target.checked })} /> Active</label>
      </div>
      <div className="flex justify-end"><Button type="submit">Save plan</Button></div>
    </form>
  );
}
