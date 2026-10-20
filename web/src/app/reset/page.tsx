"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { api } from "@/lib/api";
import { Button, ErrorNote, Field, Input, errorText } from "@/components/ui";

/** Choose a new password from a one-time reset link (/reset?token=…). */
export default function ResetPage() {
  const [token, setToken] = useState<string | null>(null);
  const [password, setPassword] = useState("");
  const [again, setAgain] = useState("");
  const [done, setDone] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => setToken(new URLSearchParams(window.location.search).get("token")), []);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (password !== again) return setError("The two passwords are not the same.");
    setBusy(true);
    try {
      await api("/api/v1/auth/reset", { method: "POST", json: { token, newPassword: password } });
      setDone(true);
      setError(null);
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="grid min-h-screen place-items-center bg-bg p-6">
      <form onSubmit={submit} className="w-full max-w-sm space-y-4 rounded-xl border border-line bg-panel p-6">
        <h1 className="text-xl font-semibold">Choose a new password</h1>
        <ErrorNote error={error} />
        {done ? (
          <p className="rounded-lg bg-ok/10 p-3 text-sm text-ok">Your password is changed and you are signed out everywhere. <Link href="/login" className="font-medium underline">Sign in</Link> with the new password.</p>
        ) : token === null ? null : !token ? (
          <p className="text-sm text-bad">This page needs the link from the reset e-mail.</p>
        ) : (
          <>
            <p className="text-sm text-muted">At least 12 characters, with letters and digits.</p>
            <Field label="New password"><Input type="password" required autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} /></Field>
            <Field label="New password again"><Input type="password" required autoComplete="new-password" value={again} onChange={(e) => setAgain(e.target.value)} /></Field>
            <Button type="submit" className="w-full" disabled={busy}>{busy ? "Saving…" : "Save new password"}</Button>
          </>
        )}
        <p className="text-center text-sm"><Link href="/login" className="text-muted hover:text-brand">Back to sign in</Link></p>
      </form>
    </div>
  );
}
