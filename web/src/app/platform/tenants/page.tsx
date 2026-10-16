"use client";

import { useCallback, useEffect, useState } from "react";
import { Plus } from "lucide-react";
import { api } from "@/lib/api";
import { Plan, TenantSummary } from "@/lib/types";
import { Badge, Button, Card, ErrorNote, Field, Input, Modal, PageHeader, Select, Table, errorText } from "@/components/ui";

const statusTone = { Active: "ok", Trial: "info", Suspended: "warn", Cancelled: "bad" } as const;
const empty = { code: "", name: "", planCode: "trial", adminEmail: "", adminName: "", adminPassword: "", country: "TW" };

export default function TenantsPage() {
  const [tenants, setTenants] = useState<TenantSummary[]>([]);
  const [plans, setPlans] = useState<Plan[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [open, setOpen] = useState(false);
  const [form, setForm] = useState(empty);
  const [formError, setFormError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    Promise.all([api<TenantSummary[]>("/api/v1/platform/tenants"), api<Plan[]>("/api/v1/platform/plans")])
      .then(([t, p]) => {
        setTenants(t);
        setPlans(p);
      })
      .catch((e) => setError(errorText(e)));
  }, []);
  useEffect(load, [load]);

  async function create(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setFormError(null);
    try {
      await api("/api/v1/platform/tenants", { method: "POST", json: form });
      setOpen(false);
      setForm(empty);
      load();
    } catch (err) {
      setFormError(errorText(err));
    } finally {
      setBusy(false);
    }
  }

  async function update(t: TenantSummary, change: { status?: string; planCode?: string }) {
    setError(null);
    try {
      await api(`/api/v1/platform/tenants/${t.tenantId}`, { method: "PATCH", json: change });
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }

  const set = (k: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) => setForm({ ...form, [k]: e.target.value });

  return (
    <>
      <PageHeader
        title="Tenants"
        sub="Each tenant is one factory company with its own users, plants and data."
        actions={<Button onClick={() => setOpen(true)}><Plus size={16} /> New tenant</Button>}
      />
      <ErrorNote error={error} />
      <Card>
        <Table head={["Company", "Code", "Plan", "Status", "Usage", "Trial ends", "Actions"]} empty={tenants.length === 0}>
          {tenants.map((t) => (
            <tr key={t.tenantId}>
              <td className="px-3 py-2 font-medium">{t.name}</td>
              <td className="px-3 py-2 font-mono text-xs">{t.code}</td>
              <td className="px-3 py-2">
                <Select value={t.planCode} onChange={(e) => update(t, { planCode: e.target.value })} className="w-36 py-1">
                  {plans.map((p) => <option key={p.planCode} value={p.planCode}>{p.name}</option>)}
                </Select>
              </td>
              <td className="px-3 py-2"><Badge tone={statusTone[t.status]}>{t.status}</Badge></td>
              <td className="px-3 py-2 text-xs text-muted">{t.users} users · {t.plants} plants · {t.assets} assets</td>
              <td className="px-3 py-2 text-muted">{t.trialEndsAt ? new Date(t.trialEndsAt + "Z").toLocaleDateString() : "—"}</td>
              <td className="px-3 py-2">
                {t.status === "Suspended" || t.status === "Cancelled" ? (
                  <Button variant="outline" className="py-1" onClick={() => update(t, { status: "Active" })}>Activate</Button>
                ) : (
                  <div className="flex gap-1">
                    {t.status === "Trial" && <Button variant="outline" className="py-1" onClick={() => update(t, { status: "Active" })}>Mark paid</Button>}
                    <Button variant="ghost" className="py-1 text-warn" onClick={() => update(t, { status: "Suspended" })}>Suspend</Button>
                  </div>
                )}
              </td>
            </tr>
          ))}
        </Table>
      </Card>

      <Modal open={open} title="New tenant" onClose={() => setOpen(false)}>
        <form onSubmit={create} className="space-y-3">
          <ErrorNote error={formError} />
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Company name"><Input required value={form.name} onChange={set("name")} /></Field>
            <Field label="Code" hint="Letters, digits, dashes"><Input required value={form.code} onChange={set("code")} /></Field>
            <Field label="Plan">
              <Select value={form.planCode} onChange={set("planCode")}>
                {plans.filter((p) => p.isActive).map((p) => <option key={p.planCode} value={p.planCode}>{p.name}</option>)}
              </Select>
            </Field>
            <Field label="Country"><Input maxLength={2} value={form.country} onChange={set("country")} /></Field>
          </div>
          <div className="border-t border-line pt-3 text-sm font-medium">Factory administrator</div>
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Name"><Input required value={form.adminName} onChange={set("adminName")} /></Field>
            <Field label="Email"><Input type="email" required value={form.adminEmail} onChange={set("adminEmail")} /></Field>
          </div>
          <Field label="Temporary password" hint="12+ characters with letters and digits. Share it securely.">
            <Input type="password" autoComplete="new-password" required value={form.adminPassword} onChange={set("adminPassword")} />
          </Field>
          <div className="flex justify-end gap-2 pt-2">
            <Button type="button" variant="ghost" onClick={() => setOpen(false)}>Cancel</Button>
            <Button type="submit" disabled={busy}>{busy ? "Creating…" : "Create tenant"}</Button>
          </div>
        </form>
      </Modal>
    </>
  );
}
