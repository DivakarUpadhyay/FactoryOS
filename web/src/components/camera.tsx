"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { Camera, Pause, Play, RefreshCw } from "lucide-react";
import { api, apiBlobUrl } from "@/lib/api";
import { Badge, Button, Card, ErrorNote, Field, Input, errorText } from "./ui";

type View = { configured: boolean; snapshotUrl: string | null; username: string | null; hasPassword: boolean; snapOnAlarm: boolean };

/** A picture loaded with the access token; refreshes every `everySec` seconds while `live`. */
export function AuthImage({ path, alt, everySec, live = true, onError, className }: { path: string; alt: string; everySec?: number; live?: boolean; onError?: (msg: string | null) => void; className?: string }) {
  const [src, setSrc] = useState<string | null>(null);
  const [at, setAt] = useState<Date | null>(null);
  const current = useRef<string | null>(null);
  const load = useCallback(() => apiBlobUrl(path).then((url) => {
    if (current.current) URL.revokeObjectURL(current.current);
    current.current = url;
    setSrc(url);
    setAt(new Date());
    onError?.(null);
  }).catch((e) => onError?.(errorText(e))), [path, onError]);
  useEffect(() => {
    load();
    if (!everySec || !live) return;
    const t = setInterval(load, everySec * 1000);
    return () => clearInterval(t);
  }, [load, everySec, live]);
  useEffect(() => () => { if (current.current) URL.revokeObjectURL(current.current); }, []);
  if (!src) return null;
  return (
    <figure>
      {/* eslint-disable-next-line @next/next/no-img-element */}
      <img src={src} alt={alt} className={className ?? "w-full rounded-lg border border-line bg-black object-contain"} />
      {everySec && at && <figcaption className="mt-1 text-xs text-muted">Picture taken {at.toLocaleTimeString()}{live ? ` · refreshes every ${everySec} s` : " · paused"}</figcaption>}
    </figure>
  );
}

/** Asset page tab: the machine's IP camera (snapshot URL), live picture, and pictures kept with alarms. */
export function CameraTab({ assetId, canEdit }: { assetId: string; canEdit: boolean }) {
  const [v, setV] = useState<View | null>(null);
  const [form, setForm] = useState({ snapshotUrl: "", username: "", password: "", snapOnAlarm: true });
  const [live, setLive] = useState(true);
  const [stamp, setStamp] = useState(0);
  const [camError, setCamError] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const load = useCallback(() => api<View>(`/api/v1/plant/assets/${assetId}/camera`).then((x) => {
    setV(x);
    setForm({ snapshotUrl: x.snapshotUrl ?? "", username: x.username ?? "", password: "", snapOnAlarm: x.snapOnAlarm });
  }).catch((e) => setError(errorText(e))), [assetId]);
  useEffect(() => { load(); }, [load]);

  async function save(remove = false) {
    try {
      await api(`/api/v1/plant/assets/${assetId}/camera`, { method: "PUT", json: remove ? { snapshotUrl: "" } : {
        snapshotUrl: form.snapshotUrl, username: form.username || null, password: form.password || null, snapOnAlarm: form.snapOnAlarm } });
      setNotice(remove ? "Camera removed." : "Camera saved.");
      setError(null);
      setStamp((s) => s + 1);
      load();
    } catch (err) {
      setError(errorText(err));
    }
  }
  const onCamError = useCallback((m: string | null) => setCamError(m), []);
  if (!v) return <ErrorNote error={error} />;

  return (
    <div className="grid gap-4 lg:grid-cols-[2fr_1fr]">
      <Card title={<span className="flex items-center gap-2"><Camera size={16} /> Camera {v.configured ? <Badge tone="ok">connected</Badge> : <Badge tone="muted">none</Badge>}</span>}
        actions={v.configured && (
          <div className="flex gap-1">
            <Button variant="ghost" className="py-1" onClick={() => setLive(!live)} aria-label={live ? "Pause" : "Resume"}>{live ? <Pause size={14} /> : <Play size={14} />}</Button>
            <Button variant="ghost" className="py-1" onClick={() => setStamp((s) => s + 1)} aria-label="Refresh picture"><RefreshCw size={14} /></Button>
          </div>
        )}>
        {v.configured ? (
          <>
            <ErrorNote error={camError} />
            <AuthImage key={stamp} path={`/api/v1/plant/assets/${assetId}/camera/snapshot`} alt="Camera picture" everySec={5} live={live} onError={onCamError} />
          </>
        ) : <p className="text-sm text-muted">No camera on this machine. Most IP cameras give a still picture at a snapshot address. Add it on the right to see the machine here and to keep a picture with every alarm.</p>}
      </Card>
      {canEdit && (
        <Card title="Camera settings">
          <ErrorNote error={error} />
          {notice && <div className="mb-3 rounded-lg bg-ok/10 px-3 py-2 text-sm text-ok">{notice}</div>}
          <div className="space-y-3">
            <Field label="Snapshot URL" hint="Hikvision: /ISAPI/Streaming/channels/101/picture · Dahua: /cgi-bin/snapshot.cgi · Axis: /axis-cgi/jpg/image.cgi">
              <Input value={form.snapshotUrl} placeholder="http://192.168.1.64/ISAPI/Streaming/channels/101/picture" onChange={(e) => setForm({ ...form, snapshotUrl: e.target.value })} /></Field>
            <Field label="User name"><Input autoComplete="off" value={form.username} onChange={(e) => setForm({ ...form, username: e.target.value })} /></Field>
            <Field label="Password" hint={v.hasPassword ? "Leave empty to keep the stored one" : "Stored encrypted"}><Input type="password" autoComplete="new-password" value={form.password} onChange={(e) => setForm({ ...form, password: e.target.value })} /></Field>
            <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={form.snapOnAlarm} onChange={(e) => setForm({ ...form, snapOnAlarm: e.target.checked })} /> Keep a picture with every alarm</label>
            <div className="flex gap-2">
              <Button onClick={() => save()} disabled={!form.snapshotUrl.trim()}>Save camera</Button>
              {v.configured && <Button variant="ghost" className="text-bad" onClick={() => save(true)}>Remove</Button>}
            </div>
          </div>
        </Card>
      )}
    </div>
  );
}
