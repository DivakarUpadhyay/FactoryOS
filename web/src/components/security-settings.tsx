"use client";

import { useCallback, useEffect, useState } from "react";
import QRCode from "qrcode";
import { Download, KeyRound, ShieldCheck } from "lucide-react";
import { api } from "@/lib/api";
import { Badge, Button, Card, ErrorNote, Field, Input, PageHeader, errorText } from "./ui";

type Status = { enabled: boolean; recoveryCodesLeft: number };
type Setup = { secret: string; otpAuthUri: string };

function RecoveryList({ codes }: { codes: string[] }) {
  const download = () => {
    const a = document.createElement("a");
    a.href = URL.createObjectURL(new Blob([`Divakar FactoryOS recovery codes\nEach code works once.\n\n${codes.join("\n")}\n`], { type: "text/plain" }));
    a.download = "factoryos-recovery-codes.txt";
    a.click();
  };
  return (
    <div className="rounded-lg border border-warn/40 bg-warn/5 p-3">
      <p className="mb-2 text-sm font-medium">Recovery codes: keep them somewhere safe. Each works once if you lose your phone. They are shown only now.</p>
      <ul className="grid grid-cols-2 gap-1 font-mono text-sm sm:grid-cols-5">{codes.map((c) => <li key={c}>{c}</li>)}</ul>
      <Button variant="outline" className="mt-3 py-1" onClick={download}><Download size={14} /> Download</Button>
    </div>
  );
}

/** The signed-in user's password and two-step sign-in. */
export function SecuritySettings() {
  const [status, setStatus] = useState<Status | null>(null);
  const [pw, setPw] = useState({ current: "", next: "", again: "" });
  const [setup, setSetup] = useState<Setup | null>(null);
  const [qr, setQr] = useState<string | null>(null);
  const [code, setCode] = useState("");
  const [codes, setCodes] = useState<string[] | null>(null);
  const [off, setOff] = useState({ password: "", code: "" });
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(() => api<Status>("/api/v1/auth/2fa").then(setStatus).catch((e) => setError(errorText(e))), []);
  useEffect(() => { load(); }, [load]);
  useEffect(() => {
    if (setup) QRCode.toDataURL(setup.otpAuthUri, { margin: 1, width: 200 }).then(setQr).catch(() => setQr(null));
  }, [setup]);

  async function run(action: () => Promise<void>, ok: string) {
    try {
      await action();
      setNotice(ok);
      setError(null);
      load();
    } catch (err) {
      setError(errorText(err));
      setNotice(null);
    }
  }
  const changePassword = (e: React.FormEvent) => {
    e.preventDefault();
    if (pw.next !== pw.again) return setError("The two new passwords are not the same.");
    run(async () => {
      await api("/api/v1/auth/password", { method: "POST", json: { currentPassword: pw.current, newPassword: pw.next } });
      setPw({ current: "", next: "", again: "" });
    }, "Password changed. Other devices are signed out.");
  };
  const begin = () => run(async () => { setSetup(await api<Setup>("/api/v1/auth/2fa/setup", { method: "POST" })); setCodes(null); }, "Scan the QR code with your authenticator app.");
  const enable = (e: React.FormEvent) => {
    e.preventDefault();
    run(async () => {
      const r = await api<{ codes: string[] }>("/api/v1/auth/2fa/enable", { method: "POST", json: { code } });
      setCodes(r.codes); setSetup(null); setCode("");
    }, "Two-step sign-in is on.");
  };
  const disable = (e: React.FormEvent) => {
    e.preventDefault();
    run(async () => { await api("/api/v1/auth/2fa/disable", { method: "POST", json: off }); setOff({ password: "", code: "" }); setCodes(null); }, "Two-step sign-in is off.");
  };
  const newCodes = () => {
    const c = prompt("Enter a current code from your authenticator app");
    if (c) run(async () => setCodes((await api<{ codes: string[] }>("/api/v1/auth/2fa/recovery-codes", { method: "POST", json: { code: c } })).codes), "New recovery codes made; the old ones no longer work.");
  };

  return (
    <>
      <PageHeader title="Sign-in & security" sub="Your password and two-step sign-in with an authenticator app (Google Authenticator, Microsoft Authenticator, 1Password…)." />
      <ErrorNote error={error} />
      {notice && <div className="mb-4 rounded-lg bg-ok/10 px-3 py-2 text-sm text-ok">{notice}</div>}
      <div className="grid gap-6 lg:grid-cols-2">
        <Card title={<span className="flex items-center gap-2"><KeyRound size={16} /> Password</span>}>
          <form onSubmit={changePassword} className="space-y-3">
            <Field label="Current password"><Input type="password" required autoComplete="current-password" value={pw.current} onChange={(e) => setPw({ ...pw, current: e.target.value })} /></Field>
            <Field label="New password" hint="At least 12 characters, with letters and digits"><Input type="password" required autoComplete="new-password" value={pw.next} onChange={(e) => setPw({ ...pw, next: e.target.value })} /></Field>
            <Field label="New password again"><Input type="password" required autoComplete="new-password" value={pw.again} onChange={(e) => setPw({ ...pw, again: e.target.value })} /></Field>
            <Button type="submit">Change password</Button>
          </form>
        </Card>
        <Card title={<span className="flex items-center gap-2"><ShieldCheck size={16} /> Two-step sign-in {status && (status.enabled ? <Badge tone="ok">on</Badge> : <Badge tone="muted">off</Badge>)}</span>}>
          {codes && <div className="mb-4"><RecoveryList codes={codes} /></div>}
          {status?.enabled ? (
            <div className="space-y-4">
              <p className="text-sm">Signing in needs your password and a code from your phone. {status.recoveryCodesLeft} recovery codes left.</p>
              <Button variant="outline" onClick={newCodes}>Make new recovery codes</Button>
              <form onSubmit={disable} className="space-y-3 border-t border-line pt-4">
                <div className="text-sm font-medium">Turn off</div>
                <Field label="Password"><Input type="password" required value={off.password} onChange={(e) => setOff({ ...off, password: e.target.value })} /></Field>
                <Field label="Code from the app (or a recovery code)"><Input required value={off.code} onChange={(e) => setOff({ ...off, code: e.target.value })} /></Field>
                <Button type="submit" variant="ghost" className="text-bad">Turn off two-step sign-in</Button>
              </form>
            </div>
          ) : setup ? (
            <form onSubmit={enable} className="space-y-3">
              <p className="text-sm">1. Scan this with your authenticator app.</p>
              {/* eslint-disable-next-line @next/next/no-img-element */}
              {qr && <img src={qr} alt="QR code for the authenticator app" width={200} height={200} className="rounded-lg border border-line bg-white p-1" />}
              <p className="text-xs text-muted">Cannot scan? Type this key: <span className="select-all font-mono">{setup.secret}</span></p>
              <p className="text-sm">2. Enter the 6-digit code it shows.</p>
              <Field label="Code"><Input inputMode="numeric" autoComplete="one-time-code" required value={code} onChange={(e) => setCode(e.target.value)} placeholder="123 456" /></Field>
              <div className="flex gap-2"><Button type="submit">Turn on</Button><Button type="button" variant="ghost" onClick={() => setSetup(null)}>Cancel</Button></div>
            </form>
          ) : (
            <div className="space-y-3">
              <p className="text-sm text-muted">Protects your account even if your password leaks. Recommended for administrators and anyone who can change machines or settings.</p>
              <Button onClick={begin}>Set up two-step sign-in</Button>
            </div>
          )}
        </Card>
      </div>
    </>
  );
}
