"use client";

import { useCallback, useEffect, useState } from "react";
import { Copy, KeyRound, Trash2 } from "lucide-react";
import { api, API_URL } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { Badge, Button, Card, ErrorNote, Field, Input, Modal, PageHeader, Table, errorText } from "@/components/ui";

type Usage = { planCode: string; planName: string; priceMonthly: number; currency: string; users: number; maxUsers: number; plants: number; maxPlants: number; assets: number; maxAssets: number; modules: string[]; status: string; trialEndsAt: string | null };
type Invoice = { invoiceId: string; number: string; periodStart: string; currency: string; total: number; status: string; dueOn: string; paidAt: string | null };
type Key = { prefix: string; name: string; createdAt: string; lastUsedAt: string | null; revokedAt: string | null };

/** The company's plan and limits, its invoices, and API keys for other systems. */
export default function AccountPage() {
  const { can } = useAuth();
  const admin = can("settings.manage");
  const [usage, setUsage] = useState<Usage | null>(null);
  const [invoices, setInvoices] = useState<Invoice[]>([]);
  const [keys, setKeys] = useState<Key[]>([]);
  const [name, setName] = useState("");
  const [created, setCreated] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(() => {
    api<Usage>("/api/v1/billing/usage").then(setUsage).catch((e) => setError(errorText(e)));
    if (admin) {
      api<Invoice[]>("/api/v1/billing/invoices").then(setInvoices).catch(() => undefined);
      api<Key[]>("/api/v1/integrations/api-keys").then(setKeys).catch(() => undefined);
    }
  }, [admin]);
  useEffect(() => { load(); }, [load]);

  async function createKey(e: React.FormEvent) {
    e.preventDefault();
    try {
      const r = await api<{ key: string }>("/api/v1/integrations/api-keys", { method: "POST", json: { name } });
      setCreated(r.key);
      setName("");
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }
  async function revoke(prefix: string) {
    if (!confirm("Revoke this key? Systems using it stop working at once.")) return;
    await api(`/api/v1/integrations/api-keys/${prefix}`, { method: "DELETE" }).catch((e) => setError(errorText(e)));
    load();
  }
  const Meter = ({ label, used, max }: { label: string; used: number; max: number }) => (
    <div>
      <div className="flex justify-between text-sm"><span>{label}</span><span className="tabular-nums text-muted">{used} / {max >= 999 ? "unlimited" : max}</span></div>
      <div className="mt-1 h-2 rounded-full bg-panel-2"><div className="h-2 rounded-full" style={{ width: `${Math.min(100, (used / Math.max(max, 1)) * 100)}%`, background: used / max > 0.9 ? "var(--warn)" : "var(--brand)" }} /></div>
    </div>
  );

  return (
    <>
      <PageHeader title="Plan, billing & API" sub="What your plan includes, how much of it you use, your invoices, and keys for ERP or BI systems." />
      <ErrorNote error={error} />
      {usage && (
        <Card title={<span className="flex items-center gap-2">{usage.planName} <Badge tone={usage.status === "Active" ? "ok" : usage.status === "Trial" ? "info" : "bad"}>{usage.status}</Badge></span>}
          actions={<span className="text-sm text-muted">{usage.priceMonthly > 0 ? `${usage.currency} ${usage.priceMonthly.toLocaleString()} / month` : "Price on request"}</span>} className="mb-6">
          <div className="grid gap-4 md:grid-cols-3">
            <Meter label="Assets" used={usage.assets} max={usage.maxAssets} /><Meter label="Users" used={usage.users} max={usage.maxUsers} /><Meter label="Plants" used={usage.plants} max={usage.maxPlants} />
          </div>
          <div className="mt-4 flex flex-wrap gap-1.5">{usage.modules.map((m) => <Badge key={m} tone="info">{m}</Badge>)}</div>
          {usage.trialEndsAt && <p className="mt-3 text-sm text-muted">Trial ends {new Date(usage.trialEndsAt + "Z").toLocaleDateString()}.</p>}
        </Card>
      )}
      {admin && (
        <>
          <Card title="Invoices" className="mb-6">
            <Table head={["Number", "Period", "Total", "Due", "Status"]} empty={invoices.length === 0}>
              {invoices.map((i) => (
                <tr key={i.invoiceId}><td className="px-3 py-2 font-mono text-xs">{i.number}</td><td className="px-3 py-2 text-sm">{i.periodStart.slice(0, 7)}</td>
                  <td className="px-3 py-2 tabular-nums">{i.currency} {i.total.toLocaleString()}</td><td className="px-3 py-2 text-xs">{i.dueOn}</td>
                  <td className="px-3 py-2"><Badge tone={i.status === "Paid" ? "ok" : i.status === "Void" ? "muted" : "info"}>{i.status}</Badge></td></tr>
              ))}
            </Table>
          </Card>
          <Card title={<span className="flex items-center gap-2"><KeyRound size={16} /> API keys for other systems</span>}>
            <p className="mb-3 text-sm text-muted">A key gives read-only access to this company&apos;s data (send it in the <code>X-Api-Key</code> header to {API_URL}/api/v1/…). It is shown once — store it in the other system right away.</p>
            <form onSubmit={createKey} className="mb-4 flex gap-2"><Input required value={name} onChange={(e) => setName(e.target.value)} placeholder="What uses it, e.g. SAP ERP, Power BI" aria-label="Key name" /><Button type="submit">Create key</Button></form>
            <Table head={["Name", "Key", "Created", "Last used", ""]} empty={keys.length === 0}>
              {keys.map((k) => (
                <tr key={k.prefix} className={k.revokedAt ? "opacity-50" : ""}>
                  <td className="px-3 py-2">{k.name}</td><td className="px-3 py-2 font-mono text-xs">{k.prefix}…</td>
                  <td className="px-3 py-2 text-xs">{new Date(k.createdAt + "Z").toLocaleDateString()}</td>
                  <td className="px-3 py-2 text-xs">{k.lastUsedAt ? new Date(k.lastUsedAt + "Z").toLocaleString() : "never"}</td>
                  <td className="px-3 py-2 text-right">{k.revokedAt ? <Badge tone="muted">revoked</Badge> : <Button variant="ghost" className="py-1 text-bad" onClick={() => revoke(k.prefix)} aria-label="Revoke key"><Trash2 size={14} /></Button>}</td>
                </tr>
              ))}
            </Table>
          </Card>
        </>
      )}
      <Modal open={!!created} title="Your new API key" onClose={() => setCreated(null)}>
        <div className="space-y-3 text-sm">
          <p>Copy it now — it will not be shown again.</p>
          <Field label="Key"><div className="flex gap-2"><Input readOnly value={created ?? ""} className="font-mono" /><Button variant="outline" onClick={() => navigator.clipboard.writeText(created ?? "")}><Copy size={14} /></Button></div></Field>
          <pre className="overflow-x-auto rounded-lg bg-panel-2 p-3 text-xs">curl -H &quot;X-Api-Key: {created?.slice(0, 12)}…&quot; {API_URL}/api/v1/production/oee</pre>
        </div>
      </Modal>
    </>
  );
}
