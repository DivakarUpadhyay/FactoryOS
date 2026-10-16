"use client";

import { useCallback, useEffect, useState } from "react";
import { Plus } from "lucide-react";
import { api } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { Role, UserSummary, fmtDate } from "@/lib/types";
import { Badge, Button, Card, ErrorNote, Field, Input, Modal, PageHeader, Table, errorText } from "@/components/ui";

type Form = { userId?: string; email: string; displayName: string; password: string; roles: string[]; isActive: boolean };

export default function UsersPage() {
  const { can, me } = useAuth();
  const manage = can("users.manage");
  const [users, setUsers] = useState<UserSummary[]>([]);
  const [roles, setRoles] = useState<Role[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [form, setForm] = useState<Form | null>(null);
  const [formError, setFormError] = useState<string | null>(null);

  const load = useCallback(() => {
    Promise.all([api<UserSummary[]>("/api/v1/users"), api<Role[]>("/api/v1/roles")])
      .then(([u, r]) => {
        setUsers(u);
        setRoles(r);
      })
      .catch((e) => setError(errorText(e)));
  }, []);
  useEffect(load, [load]);

  const roleName = Object.fromEntries(roles.map((r) => [r.code, r.name]));

  async function save(e: React.FormEvent) {
    e.preventDefault();
    if (!form) return;
    setFormError(null);
    try {
      if (form.userId) {
        await api(`/api/v1/users/${form.userId}`, {
          method: "PATCH",
          json: { displayName: form.displayName, isActive: form.isActive, roles: form.roles, newPassword: form.password || null },
        });
      } else {
        await api("/api/v1/users", { method: "POST", json: { email: form.email, displayName: form.displayName, password: form.password, roles: form.roles } });
      }
      setForm(null);
      load();
    } catch (err) {
      setFormError(errorText(err));
    }
  }

  return (
    <>
      <PageHeader
        title="Users"
        sub="People in your company and what they may do."
        actions={manage && <Button onClick={() => setForm({ email: "", displayName: "", password: "", roles: ["operator"], isActive: true })}><Plus size={16} /> Add user</Button>}
      />
      <ErrorNote error={error} />
      <Card>
        <Table head={["Name", "Email", "Roles", "Status", "Last sign-in", ""]} empty={users.length === 0}>
          {users.map((u) => (
            <tr key={u.userId}>
              <td className="px-3 py-2 font-medium">{u.displayName}{u.userId === me?.userId && <span className="ml-2 text-xs text-muted">(you)</span>}</td>
              <td className="px-3 py-2 text-muted">{u.email}</td>
              <td className="px-3 py-2"><div className="flex flex-wrap gap-1">{u.roles.map((r) => <Badge key={r} tone="brand">{roleName[r] ?? r}</Badge>)}</div></td>
              <td className="px-3 py-2"><Badge tone={u.isActive ? "ok" : "muted"}>{u.isActive ? "Active" : "Deactivated"}</Badge></td>
              <td className="px-3 py-2 text-muted">{fmtDate(u.lastLoginAt)}</td>
              <td className="px-3 py-2">
                {manage && <Button variant="outline" className="py-1" onClick={() => setForm({ userId: u.userId, email: u.email, displayName: u.displayName, password: "", roles: u.roles, isActive: u.isActive })}>Edit</Button>}
              </td>
            </tr>
          ))}
        </Table>
      </Card>

      <Modal open={!!form} title={form?.userId ? "Edit user" : "Add user"} onClose={() => setForm(null)}>
        {form && (
          <form onSubmit={save} className="space-y-3">
            <ErrorNote error={formError} />
            <Field label="Name"><Input required value={form.displayName} onChange={(e) => setForm({ ...form, displayName: e.target.value })} /></Field>
            <Field label="Email"><Input type="email" required disabled={!!form.userId} value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} /></Field>
            <Field label={form.userId ? "New password (leave empty to keep)" : "Temporary password"} hint="12+ characters with letters and digits">
              <Input type="password" autoComplete="new-password" required={!form.userId} value={form.password} onChange={(e) => setForm({ ...form, password: e.target.value })} />
            </Field>
            <div>
              <div className="mb-1.5 text-sm font-medium">Roles</div>
              <div className="grid gap-1.5 sm:grid-cols-2">
                {roles.map((r) => (
                  <label key={r.code} className="flex items-center gap-2 text-sm">
                    <input type="checkbox" checked={form.roles.includes(r.code)}
                      onChange={(e) => setForm({ ...form, roles: e.target.checked ? [...form.roles, r.code] : form.roles.filter((x) => x !== r.code) })} />
                    {r.name}
                  </label>
                ))}
              </div>
            </div>
            {form.userId && (
              <label className="flex items-center gap-2 text-sm">
                <input type="checkbox" checked={form.isActive} onChange={(e) => setForm({ ...form, isActive: e.target.checked })} /> Active (can sign in)
              </label>
            )}
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" onClick={() => setForm(null)}>Cancel</Button>
              <Button type="submit">Save</Button>
            </div>
          </form>
        )}
      </Modal>
    </>
  );
}
