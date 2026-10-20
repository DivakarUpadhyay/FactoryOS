"use client";

import { useState } from "react";
import Link from "next/link";
import { api } from "@/lib/api";
import { Button, ErrorNote, Field, Input, errorText } from "@/components/ui";

/** Asks for a password-reset e-mail. The answer is the same whether or not the address has an account. */
export default function ForgotPage() {
  const [email, setEmail] = useState("");
  const [sent, setSent] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    try {
      await api("/api/v1/auth/forgot", { method: "POST", json: { email } });
      setSent(true);
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
        <h1 className="text-xl font-semibold">Reset your password</h1>
        <ErrorNote error={error} />
        {sent ? (
          <div className="space-y-3 text-sm">
            <p className="rounded-lg bg-ok/10 p-3 text-ok">If an account uses {email}, an e-mail with a reset link is on its way. The link works once, for 30 minutes.</p>
            <p className="text-muted">No e-mail? Some factories have no mail server set up. Your administrator can create a reset link for you on the Users page.</p>
          </div>
        ) : (
          <>
            <p className="text-sm text-muted">Enter the e-mail address you sign in with. We send a link to choose a new password.</p>
            <Field label="Email"><Input type="email" required autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} /></Field>
            <Button type="submit" className="w-full" disabled={busy}>{busy ? "Sending…" : "Send reset link"}</Button>
          </>
        )}
        <p className="text-center text-sm"><Link href="/login" className="text-muted hover:text-brand">Back to sign in</Link></p>
      </form>
    </div>
  );
}
