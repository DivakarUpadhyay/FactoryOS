"use client";

import { useCallback, useEffect, useState } from "react";
import { Banknote, FileText, Receipt, Users } from "lucide-react";
import { api } from "@/lib/api";
import { Badge, Button, Card, ErrorNote, Field, Input, Modal, PageHeader, Stat, Table, errorText } from "@/components/ui";

type Line = { description: string; quantity: number; unitPrice: number; amount: number };
type Invoice = { invoiceId: string; tenantId: string; tenantName: string; number: string; periodStart: string; periodEnd: string; currency: string; lines: Line[]; subtotal: number; taxRate: number; tax: number; total: number; status: "Issued" | "Paid" | "Void"; issuedAt: string; dueOn: string; paidAt: string | null; paymentRef: string | null };
type Overview = { mrr: number; currency: string; payingTenants: number; trialTenants: number; outstanding: number; paidThisMonth: number; invoices: Invoice[] };

/** Platform operator: monthly recurring revenue, invoice run and payments (recorded by hand until a payment provider is connected). */
export default function BillingPage() {
  const [o, setO] = useState<Overview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [month, setMonth] = useState(() => new Date().toISOString().slice(0, 7));
  const [pay, setPay] = useState<Invoice | null>(null);
  const [ref, setRef] = useState("");
  const [view, setView] = useState<Invoice | null>(null);
  const load = useCallback(() => api<Overview>("/api/v1/platform/billing").then(setO).catch((e) => setError(errorText(e))), []);
  useEffect(() => { load(); }, [load]);

  async function generate() {
    try {
      const r = await api<{ created: number }>(`/api/v1/platform/billing/generate?month=${month}-01`, { method: "POST" });
      setNotice(r.created ? `${r.created} invoice(s) issued for ${month}.` : o?.payingTenants ? `Every paying company already has an invoice for ${month}.`
        : "No paying companies yet — trial and price-on-request (Enterprise) plans are not invoiced automatically.");
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }
  async function mark(inv: Invoice, action: "paid" | "void") {
    try {
      await api(`/api/v1/platform/billing/invoices/${inv.invoiceId}/${action}`, { method: "POST", json: { paymentRef: action === "paid" ? ref || null : null } });
      setPay(null);
      setRef("");
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }
  const money = (n: number) => `${o?.currency ?? "TWD"} ${n.toLocaleString()}`;

  return (
    <>
      <PageHeader title="Billing" sub="Invoices are issued automatically on the first of each month for active paying companies (5 % business tax). Record payments here."
        actions={<div className="flex items-end gap-2"><Input type="month" value={month} onChange={(e) => setMonth(e.target.value)} className="w-40 py-1" aria-label="Month" /><Button className="py-1" onClick={generate}><Receipt size={14} /> Issue invoices</Button></div>} />
      <ErrorNote error={error} />
      {notice && <div className="mb-4 rounded-lg bg-ok/10 px-3 py-2 text-sm text-ok">{notice}</div>}
      {o && (
        <>
          <div className="mb-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
            <Stat label="MRR" value={money(o.mrr)} sub="Monthly recurring revenue" icon={<Banknote size={16} />} />
            <Stat label="Paying companies" value={o.payingTenants} sub={`${o.trialTenants} on trial`} icon={<Users size={16} />} />
            <Stat label="Outstanding" value={money(o.outstanding)} sub="Issued, not paid" icon={<FileText size={16} />} />
            <Stat label="Paid this month" value={money(o.paidThisMonth)} icon={<Receipt size={16} />} />
          </div>
          <Card title="Invoices">
            <Table head={["Number", "Company", "Period", "Total", "Due", "Status", ""]} empty={o.invoices.length === 0}>
              {o.invoices.map((i) => (
                <tr key={i.invoiceId}>
                  <td className="px-3 py-2"><button className="font-mono text-xs text-brand hover:underline" onClick={() => setView(i)}>{i.number}</button></td>
                  <td className="px-3 py-2">{i.tenantName}</td>
                  <td className="px-3 py-2 text-xs">{i.periodStart.slice(0, 7)}</td>
                  <td className="px-3 py-2 tabular-nums">{i.currency} {i.total.toLocaleString()}</td>
                  <td className="px-3 py-2 text-xs">{i.dueOn}</td>
                  <td className="px-3 py-2"><Badge tone={i.status === "Paid" ? "ok" : i.status === "Void" ? "muted" : new Date(i.dueOn) < new Date() ? "bad" : "info"}>{i.status}</Badge>{i.paymentRef && <div className="text-[11px] text-muted">{i.paymentRef}</div>}</td>
                  <td className="whitespace-nowrap px-3 py-2 text-right">{i.status === "Issued" && <>
                    <Button variant="outline" className="py-1 text-xs" onClick={() => setPay(i)}>Mark paid</Button>
                    <Button variant="ghost" className="py-1 text-xs text-bad" onClick={() => confirm(`Void ${i.number}?`) && mark(i, "void")}>Void</Button></>}</td>
                </tr>
              ))}
            </Table>
          </Card>
        </>
      )}
      <Modal open={!!pay} title={`Record payment for ${pay?.number}`} onClose={() => setPay(null)}>
        <div className="space-y-4">
          <Field label="Payment reference" hint="Bank transfer number, cheque, card receipt…"><Input value={ref} onChange={(e) => setRef(e.target.value)} /></Field>
          <div className="flex justify-end gap-2"><Button variant="ghost" onClick={() => setPay(null)}>Cancel</Button><Button onClick={() => pay && mark(pay, "paid")}>Mark paid</Button></div>
        </div>
      </Modal>
      <Modal open={!!view} title={`Invoice ${view?.number}`} onClose={() => setView(null)}>{view && <InvoiceView inv={view} />}</Modal>
    </>
  );
}

function InvoiceView({ inv }: { inv: Invoice }) {
  return (
    <div className="space-y-3 text-sm">
      <div className="flex justify-between"><div><div className="font-semibold">Divakar FactoryOS</div><div className="text-muted">Bill to: {inv.tenantName}</div></div>
        <div className="text-right"><div className="font-mono">{inv.number}</div><div className="text-muted">Period {inv.periodStart} – {inv.periodEnd}</div><div className="text-muted">Due {inv.dueOn}</div></div></div>
      <table className="w-full text-sm"><thead><tr className="border-b border-line text-left text-xs text-muted"><th className="py-1">Description</th><th className="py-1 text-right">Qty</th><th className="py-1 text-right">Price</th><th className="py-1 text-right">Amount</th></tr></thead>
        <tbody>{inv.lines.map((l, i) => <tr key={i}><td className="py-1">{l.description}</td><td className="py-1 text-right">{l.quantity}</td><td className="py-1 text-right">{l.unitPrice.toLocaleString()}</td><td className="py-1 text-right">{l.amount.toLocaleString()}</td></tr>)}</tbody></table>
      <div className="ml-auto w-56 space-y-1 border-t border-line pt-2">
        <div className="flex justify-between"><span>Subtotal</span><span>{inv.currency} {inv.subtotal.toLocaleString()}</span></div>
        <div className="flex justify-between"><span>Tax {(inv.taxRate * 100).toFixed(0)}%</span><span>{inv.currency} {inv.tax.toLocaleString()}</span></div>
        <div className="flex justify-between font-semibold"><span>Total</span><span>{inv.currency} {inv.total.toLocaleString()}</span></div>
      </div>
      <div className="flex justify-end"><Button variant="outline" onClick={() => window.print()}>Print</Button></div>
    </div>
  );
}
