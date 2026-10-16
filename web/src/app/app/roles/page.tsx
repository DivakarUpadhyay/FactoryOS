"use client";

import { Fragment, useCallback, useEffect, useMemo, useState } from "react";
import { Plus, Save, Trash2 } from "lucide-react";
import { api } from "@/lib/api";
import { Role } from "@/lib/types";
import { Badge, Button, Card, ErrorNote, Field, Input, Modal, PageHeader, errorText } from "@/components/ui";

/** Permission matrix: one column per role, one row per permission, grouped by module. */
export default function RolesPage() {
  const [roles, setRoles] = useState<Role[]>([]);
  const [perms, setPerms] = useState<string[]>([]);
  const [draft, setDraft] = useState<Record<string, Set<string>>>({});
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  const [newRole, setNewRole] = useState<{ code: string; name: string } | null>(null);

  const load = useCallback(async () => {
    try {
      const [r, p] = await Promise.all([api<Role[]>("/api/v1/roles"), api<string[]>("/api/v1/roles/permissions")]);
      setRoles(r);
      setPerms(p);
      setDraft(Object.fromEntries(r.map((x) => [x.roleId, new Set(x.permissions)])));
    } catch (e) {
      setError(errorText(e));
    }
  }, []);
  useEffect(() => {
    load();
  }, [load]);

  const grouped = useMemo(() => perms.reduce<Record<string, string[]>>((m, p) => {
    (m[p.split(".")[0]] ??= []).push(p);
    return m;
  }, {}), [perms]);

  const dirty = (r: Role) => {
    const d = draft[r.roleId];
    return !!d && (d.size !== r.permissions.length || r.permissions.some((p) => !d.has(p)));
  };

  function flip(roleId: string, p: string) {
    setDraft((d) => {
      const s = new Set(d[roleId]);
      if (s.has(p)) s.delete(p);
      else s.add(p);
      return { ...d, [roleId]: s };
    });
  }

  async function save(r: Role) {
    setError(null);
    setSaved(null);
    try {
      await api(`/api/v1/roles/${r.roleId}`, { method: "PUT", json: { code: r.code, name: r.name, permissions: [...draft[r.roleId]] } });
      setSaved(`Saved "${r.name}". Users get the change at their next sign-in or token refresh.`);
      await load();
    } catch (e) {
      setError(errorText(e));
    }
  }

  async function remove(r: Role) {
    if (!confirm(`Delete role "${r.name}"?`)) return;
    try {
      await api(`/api/v1/roles/${r.roleId}`, { method: "DELETE" });
      await load();
    } catch (e) {
      setError(errorText(e));
    }
  }

  async function create(e: React.FormEvent) {
    e.preventDefault();
    if (!newRole) return;
    try {
      await api("/api/v1/roles", { method: "POST", json: { ...newRole, permissions: ["plant.read"] } });
      setNewRole(null);
      await load();
    } catch (err) {
      setError(errorText(err));
    }
  }

  return (
    <>
      <PageHeader
        title="Roles & permissions"
        sub="Tick what each role may do. Built-in roles can be adjusted; the factory administrator always keeps everything."
        actions={<Button onClick={() => setNewRole({ code: "", name: "" })}><Plus size={16} /> New role</Button>}
      />
      <ErrorNote error={error} />
      {saved && <div className="mb-3 rounded-lg bg-ok/10 px-3 py-2 text-sm text-ok">{saved}</div>}
      <Card>
        <div className="overflow-x-auto">
          <table className="text-sm">
            <thead>
              <tr className="border-b border-line">
                <th className="sticky left-0 bg-panel px-3 py-2 text-left text-xs uppercase text-muted">Permission</th>
                {roles.map((r) => (
                  <th key={r.roleId} className="min-w-28 px-2 py-2 text-center align-bottom">
                    <div className="text-xs font-semibold">{r.name}</div>
                    <div className="mt-1 flex justify-center gap-1">
                      {r.isSystem ? <Badge>built-in</Badge> : <button onClick={() => remove(r)} className="text-bad" aria-label="Delete role"><Trash2 size={13} /></button>}
                      {dirty(r) && <button onClick={() => save(r)} className="text-brand" aria-label="Save role"><Save size={13} /></button>}
                    </div>
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {Object.entries(grouped).map(([module, list]) => (
                <Fragment key={module}>
                  <tr className="bg-panel-2">
                    <td colSpan={roles.length + 1} className="sticky left-0 px-3 py-1.5 text-xs font-semibold uppercase tracking-wide text-muted">{module}</td>
                  </tr>
                  {list.map((p) => (
                    <tr key={p} className="border-b border-line/60">
                      <td className="sticky left-0 bg-panel px-3 py-1.5 font-mono text-xs">{p}</td>
                      {roles.map((r) => (
                        <td key={r.roleId} className="text-center">
                          <input type="checkbox" checked={draft[r.roleId]?.has(p) ?? false} disabled={r.code === "factory-admin"} onChange={() => flip(r.roleId, p)} />
                        </td>
                      ))}
                    </tr>
                  ))}
                </Fragment>
              ))}
            </tbody>
          </table>
        </div>
      </Card>

      <Modal open={!!newRole} title="New role" onClose={() => setNewRole(null)}>
        {newRole && (
          <form onSubmit={create} className="space-y-3">
            <Field label="Name"><Input required value={newRole.name} onChange={(e) => setNewRole({ ...newRole, name: e.target.value })} /></Field>
            <Field label="Code" hint="Letters, digits, dashes"><Input required value={newRole.code} onChange={(e) => setNewRole({ ...newRole, code: e.target.value })} /></Field>
            <p className="text-xs text-muted">The role starts with &quot;plant.read&quot;; tick more permissions in the matrix afterwards.</p>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" onClick={() => setNewRole(null)}>Cancel</Button>
              <Button type="submit">Create</Button>
            </div>
          </form>
        )}
      </Modal>
    </>
  );
}
