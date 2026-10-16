"use client";

import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import { MODULES, Plan } from "@/lib/types";
import { Badge, Button, Card, ErrorNote, Field, Input, Modal, PageHeader, errorText } from "@/components/ui";

export default function PlansPage() {
  const [plans, setPlans] = useState<Plan[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [edit, setEdit] = useState<Plan | null>(null);
  const [busy, setBusy] = useState(false);

  const load = () => api<Plan[]>("/api/v1/platform/plans").then(setPlans).catch((e) => setError(errorText(e)));
  useEffect(() => {
    load();
  }, []);

  async function save(e: React.FormEvent) {
    e.preventDefault();
    if (!edit) return;
    setBusy(true);
    setError(null);
    try {
      await api(`/api/v1/platform/plans/${edit.planCode}`, {
        method: "PUT",
        json: { name: edit.name, priceMonthly: edit.priceMonthly, maxPlants: edit.maxPlants, maxAssets: edit.maxAssets, maxUsers: edit.maxUsers, modules: edit.modules, isActive: edit.isActive },
      });
      setEdit(null);
      load();
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  }

  const num = (k: "priceMonthly" | "maxPlants" | "maxAssets" | "maxUsers") => (e: React.ChangeEvent<HTMLInputElement>) =>
    edit && setEdit({ ...edit, [k]: Number(e.target.value) });

  return (
    <>
      <PageHeader title="Plans & pricing" sub="What each subscription costs and which modules and limits it includes." />
      <ErrorNote error={error} />
      <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
        {plans.map((p) => (
          <Card key={p.planCode} title={p.name} actions={<Button variant="outline" className="py-1" onClick={() => setEdit(p)}>Edit</Button>}>
            <div className="text-2xl font-semibold">{p.priceMonthly > 0 ? `${p.currency} ${p.priceMonthly.toLocaleString()}` : p.planCode === "trial" ? "Free" : "Custom"}</div>
            <div className="text-xs text-muted">per month {p.isActive ? "" : "· not on sale"}</div>
            <ul className="mt-3 space-y-1 text-sm">
              <li>{p.maxPlants} plants · {p.maxAssets} assets · {p.maxUsers} users</li>
            </ul>
            <div className="mt-3 flex flex-wrap gap-1">{p.modules.map((m) => <Badge key={m} tone="brand">{m}</Badge>)}</div>
          </Card>
        ))}
      </div>

      <Modal open={!!edit} title={`Edit plan: ${edit?.name ?? ""}`} onClose={() => setEdit(null)}>
        {edit && (
          <form onSubmit={save} className="space-y-3">
            <Field label="Name"><Input value={edit.name} onChange={(e) => setEdit({ ...edit, name: e.target.value })} /></Field>
            <div className="grid grid-cols-2 gap-3">
              <Field label={`Price per month (${edit.currency})`}><Input type="number" min={0} value={edit.priceMonthly} onChange={num("priceMonthly")} /></Field>
              <Field label="Max plants"><Input type="number" min={1} value={edit.maxPlants} onChange={num("maxPlants")} /></Field>
              <Field label="Max assets"><Input type="number" min={1} value={edit.maxAssets} onChange={num("maxAssets")} /></Field>
              <Field label="Max users"><Input type="number" min={1} value={edit.maxUsers} onChange={num("maxUsers")} /></Field>
            </div>
            <div>
              <div className="mb-1.5 text-sm font-medium">Modules</div>
              <div className="flex flex-wrap gap-2">
                {MODULES.map((m) => {
                  const on = edit.modules.includes(m);
                  return (
                    <button type="button" key={m} onClick={() => setEdit({ ...edit, modules: on ? edit.modules.filter((x) => x !== m) : [...edit.modules, m] })}
                      className={`rounded-full border px-3 py-1 text-xs ${on ? "border-brand bg-brand/15 text-brand" : "border-line text-muted"}`}>
                      {m}
                    </button>
                  );
                })}
              </div>
            </div>
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={edit.isActive} onChange={(e) => setEdit({ ...edit, isActive: e.target.checked })} /> On sale
            </label>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" onClick={() => setEdit(null)}>Cancel</Button>
              <Button type="submit" disabled={busy}>Save</Button>
            </div>
          </form>
        )}
      </Modal>
    </>
  );
}
