"use client";

import { useCallback, useEffect, useState } from "react";
import { Copy, MessageCircle, Send } from "lucide-react";
import { api } from "@/lib/api";
import { Badge, Button, Card, ErrorNote, Field, Input, errorText } from "./ui";

type Contact = { sourceId: string; kind: "user" | "group" | "room"; lastText: string | null; lastSeen: string };
type View = { configured: boolean; enabled: boolean; recipients: string[]; webhookUrl: string; contacts: Contact[] };

/**
 * LINE Messaging API: alarms pushed to LINE users and groups through the company's own LINE Official Account.
 * The channel secret and access token go to the server once and are stored encrypted; they are never shown again.
 */
export function LineSettings() {
  const [v, setV] = useState<View | null>(null);
  const [secret, setSecret] = useState("");
  const [token, setToken] = useState("");
  const [recipients, setRecipients] = useState<string[]>([]);
  const [extra, setExtra] = useState("");
  const [enabled, setEnabled] = useState(true);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(() => api<View>("/api/v1/integrations/line").then((x) => {
    setV(x);
    setRecipients(x.recipients);
    setEnabled(x.configured ? x.enabled : true);
  }).catch((e) => setError(errorText(e))), []);
  useEffect(() => { load(); }, [load]);

  async function save() {
    try {
      const typed = extra.split(/[\s,;]+/).filter(Boolean);
      await api("/api/v1/integrations/line", { method: "PUT", json: { channelSecret: secret || null, accessToken: token || null, recipients: [...new Set([...recipients, ...typed])], enabled } });
      setSecret(""); setToken(""); setExtra("");
      setNotice("LINE settings saved.");
      setError(null);
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }
  async function test() {
    try {
      const r = await api<{ queued: number }>("/api/v1/integrations/line/test", { method: "POST" });
      setNotice(`Test message queued to ${r.queued} recipient${r.queued === 1 ? "" : "s"}; it arrives within a few seconds.`);
    } catch (err) {
      setError(errorText(err));
    }
  }
  if (!v) return <ErrorNote error={error} />;
  const toggle = (id: string) => setRecipients(recipients.includes(id) ? recipients.filter((x) => x !== id) : [...recipients, id]);
  const known = new Set(v.contacts.map((c) => c.sourceId));

  return (
    <Card title={<span className="flex items-center gap-2"><MessageCircle size={16} className="text-ok" /> LINE alarms {v.configured ? (v.enabled ? <Badge tone="ok">on</Badge> : <Badge tone="muted">off</Badge>) : <Badge tone="muted">not set up</Badge>}</span>}>
      <ErrorNote error={error} />
      {notice && <div className="mb-3 rounded-lg bg-ok/10 px-3 py-2 text-sm text-ok">{notice}</div>}
      <ol className="mb-4 list-decimal space-y-1 pl-5 text-sm text-muted">
        <li>In the LINE Developers console, create a Messaging API channel for your LINE Official Account.</li>
        <li>Paste its <b>channel secret</b> and a <b>channel access token</b> below. They are stored encrypted and never shown again.</li>
        <li>Set the webhook URL below in the channel and turn on &quot;Use webhook&quot;. The server must be reachable over HTTPS.</li>
        <li>Ask staff to add the bot as a friend and send it a message, or invite it to a group. They then appear here to tick.</li>
      </ol>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Channel secret" hint={v.configured ? "Leave empty to keep the stored one" : undefined}><Input type="password" autoComplete="off" value={secret} onChange={(e) => setSecret(e.target.value)} /></Field>
        <Field label="Channel access token" hint={v.configured ? "Leave empty to keep the stored one" : undefined}><Input type="password" autoComplete="off" value={token} onChange={(e) => setToken(e.target.value)} /></Field>
        <div className="sm:col-span-2">
          <div className="flex items-end gap-2">
            <div className="flex-1"><Field label="Webhook URL"><Input readOnly value={v.webhookUrl} className="font-mono text-xs" /></Field></div>
            <Button type="button" variant="outline" onClick={() => navigator.clipboard?.writeText(v.webhookUrl)} aria-label="Copy webhook URL"><Copy size={14} /></Button>
          </div>
        </div>
      </div>
      <div className="mt-4">
        <div className="mb-2 text-sm font-medium">Send alarms to</div>
        {v.contacts.length === 0 && recipients.length === 0 && <p className="text-sm text-muted">Nobody has written to the bot yet.</p>}
        <ul className="space-y-1">
          {v.contacts.map((c) => (
            <li key={c.sourceId}><label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={recipients.includes(c.sourceId)} onChange={() => toggle(c.sourceId)} />
              <Badge tone={c.kind === "user" ? "info" : "warn"}>{c.kind}</Badge>
              <span className="font-mono text-xs">{c.sourceId}</span>
              {c.lastText && <span className="truncate text-muted">“{c.lastText}”</span>}
            </label></li>
          ))}
          {recipients.filter((r) => !known.has(r)).map((r) => (
            <li key={r}><label className="flex items-center gap-2 text-sm"><input type="checkbox" checked onChange={() => toggle(r)} /><span className="font-mono text-xs">{r}</span></label></li>
          ))}
        </ul>
        <div className="mt-3"><Field label="Add ids by hand" hint="U… user, C… group or R… room ids, comma separated"><Input value={extra} onChange={(e) => setExtra(e.target.value)} /></Field></div>
      </div>
      <label className="mt-4 flex items-center gap-2 text-sm"><input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} /> Send alarms on LINE</label>
      <div className="mt-4 flex gap-2">
        <Button type="button" onClick={save}>Save LINE settings</Button>
        {v.configured && <Button type="button" variant="outline" onClick={test} disabled={v.recipients.length === 0}><Send size={14} /> Send test</Button>}
      </div>
    </Card>
  );
}
