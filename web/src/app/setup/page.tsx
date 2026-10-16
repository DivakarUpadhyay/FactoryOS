"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { api } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { Button, Card, ErrorNote, Field, Input, errorText } from "@/components/ui";

/** First-time setup: creates the platform operator. The API accepts this only from the server machine, once. */
export default function SetupPage() {
  const { login } = useAuth();
  const router = useRouter();
  const [form, setForm] = useState({ email: "", displayName: "", password: "", confirm: "" });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (form.password !== form.confirm) {
      setError("The two passwords do not match.");
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await api("/api/v1/auth/setup", { method: "POST", json: { email: form.email, displayName: form.displayName, password: form.password } });
      await login(form.email, form.password);
      router.replace("/platform");
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  }

  const set = (k: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [k]: e.target.value });

  return (
    <div className="grid min-h-screen place-items-center p-6">
      <Card title="Divakar FactoryOS: first-time setup" className="w-full max-w-md">
        <form onSubmit={submit} className="space-y-4">
          <p className="text-sm text-muted">Create the platform operator account. It manages tenants (factories), plans and pricing.</p>
          <ErrorNote error={error} />
          <Field label="Your name">
            <Input required value={form.displayName} onChange={set("displayName")} />
          </Field>
          <Field label="Email">
            <Input type="email" required value={form.email} onChange={set("email")} />
          </Field>
          <Field label="Password" hint="At least 12 characters with letters and digits.">
            <Input type="password" autoComplete="new-password" required value={form.password} onChange={set("password")} />
          </Field>
          <Field label="Repeat password">
            <Input type="password" autoComplete="new-password" required value={form.confirm} onChange={set("confirm")} />
          </Field>
          <Button type="submit" disabled={busy} className="w-full">
            {busy ? "Creating…" : "Create operator account"}
          </Button>
        </form>
      </Card>
    </div>
  );
}
