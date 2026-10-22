"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { Factory, Zap, Bot, Gauge } from "lucide-react";
import { useAuth } from "@/lib/auth";
import { api } from "@/lib/api";
import { Button, ErrorNote, Field, Input, errorText } from "@/components/ui";

// Public read-only account in the demo factory (Viewer role; the API blocks password and 2FA changes for it).
const DEMO = { email: "demo@divakar-demo.local", password: "FactoryDemo@2026" };

export default function LoginPage() {
  const { login, verifyCode, me } = useAuth();
  const router = useRouter();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [needsSetup, setNeedsSetup] = useState(false);
  const [mfaToken, setMfaToken] = useState<string | null>(null);
  const [code, setCode] = useState("");

  // ?next=/op sends a signed-in operator back to the shop-floor app
  const next = () => {
    const n = new URLSearchParams(window.location.search).get("next");
    return n && n.startsWith("/") && !n.startsWith("//") ? n : null;
  };

  useEffect(() => {
    if (me) router.replace(me.isSuperAdmin ? "/platform" : next() ?? "/app");
  }, [me, router]);

  useEffect(() => {
    api<{ needsSetup: boolean }>("/api/v1/auth/setup").then((r) => setNeedsSetup(r.needsSetup)).catch(() => undefined);
  }, []);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const result = mfaToken ? await verifyCode(mfaToken, code) : await login(email, password);
      if ("mfaToken" in result) {
        setMfaToken(result.mfaToken);   // password accepted: now the code from the authenticator app
        return;
      }
      router.replace(result.isSuperAdmin ? "/platform" : next() ?? "/app");
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="grid min-h-screen lg:grid-cols-2">
      <div className="hidden flex-col justify-between bg-gradient-to-br from-[#0b1017] via-[#0f2a2a] to-[#0f766e] p-10 text-white lg:flex">
        <div className="flex items-center gap-2">
          <div className="grid h-9 w-9 place-items-center rounded-lg bg-white/15 font-bold">D</div>
          <span className="font-semibold">Divakar FactoryOS</span>
        </div>
        <div>
          <h1 className="text-3xl font-semibold leading-tight">Every machine, meter and robot in your factory, in one place.</h1>
          <p className="mt-3 max-w-md text-white/70">
            Live plant status, energy and power management, OEE, robotics, maintenance and quality, connected over OPC UA, MQTT,
            Modbus, SECS/GEM and 30 other industrial protocols.
          </p>
          <div className="mt-8 grid max-w-md grid-cols-2 gap-3 text-sm">
            {[
              [Factory, "ISA-95 plant model"],
              [Zap, "Energy & power"],
              [Gauge, "OEE & downtime"],
              [Bot, "Robots & AGV fleet"],
            ].map(([Icon, text]) => {
              const I = Icon as React.ElementType;
              return (
                <div key={text as string} className="flex items-center gap-2 rounded-lg bg-white/10 px-3 py-2">
                  <I size={16} /> {text as string}
                </div>
              );
            })}
          </div>
        </div>
        <p className="text-xs text-white/50">© 2026 Divakar Upadhyay</p>
      </div>

      <div className="flex items-center justify-center p-6">
        <form onSubmit={submit} className="w-full max-w-sm space-y-4">
          <div>
            <h2 className="text-2xl font-semibold">Sign in</h2>
            <p className="mt-1 text-sm text-muted">Use the account your administrator created for you.</p>
          </div>
          <ErrorNote error={error} />
          {mfaToken ? (
            <>
              <p className="rounded-lg bg-panel-2 p-3 text-sm">Two-step sign-in is on for this account. Enter the 6-digit code from your authenticator app, or one of your recovery codes.</p>
              <Field label="Code">
                <Input autoFocus inputMode="numeric" autoComplete="one-time-code" required value={code} onChange={(e) => setCode(e.target.value)} placeholder="123 456" />
              </Field>
              <Button type="submit" disabled={busy} className="w-full">{busy ? "Checking…" : "Verify"}</Button>
              <button type="button" className="w-full text-center text-sm text-muted hover:text-ink" onClick={() => { setMfaToken(null); setCode(""); setError(null); }}>Back</button>
            </>
          ) : (
            <>
              <Field label="Email">
                <Input type="email" autoComplete="username" required value={email} onChange={(e) => setEmail(e.target.value)} />
              </Field>
              <Field label="Password">
                <Input type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
              </Field>
              <Button type="submit" disabled={busy} className="w-full">
                {busy ? "Signing in…" : "Sign in"}
              </Button>
              <p className="text-center text-sm"><Link href="/forgot" className="text-muted hover:text-brand">Forgot password?</Link></p>
              <div className="rounded-lg border border-line bg-panel-2 p-3 text-sm">
                <p className="font-medium">Try the demo (read only)</p>
                <p className="mt-1 text-muted">
                  Email: <span className="font-mono text-ink">{DEMO.email}</span>
                  <br />
                  Password: <span className="font-mono text-ink">{DEMO.password}</span>
                </p>
                <Button type="button" variant="outline" className="mt-2 w-full" onClick={() => { setEmail(DEMO.email); setPassword(DEMO.password); setError(null); }}>
                  Use demo login
                </Button>
              </div>
            </>
          )}
          {needsSetup && (
            <p className="rounded-lg bg-info/10 p-3 text-sm text-info">
              No platform operator exists yet. <Link href="/setup" className="font-medium underline">Run first-time setup</Link> (only
              works on the server itself).
            </p>
          )}
        </form>
      </div>
    </div>
  );
}
