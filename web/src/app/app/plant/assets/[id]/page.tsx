"use client";

import { use, useCallback, useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { ArrowLeft, KeyRound, Pencil, PlugZap, Plus, Trash2, Wand2 } from "lucide-react";
import { api } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import {
  ADDRESS_HELP, Asset, AssetLive, Catalogue, HistoryPoint, QUALITY, SETTINGS_HELP, SensorType, Tag, TestResult, ago, fmtValue, statusTone,
} from "@/lib/types";
import { Badge, Button, Card, ErrorNote, Field, Input, LiveBadge, Modal, PageHeader, Select, Table, cx, errorText } from "@/components/ui";
import { Sparkline } from "@/components/sparkline";
import { useLive } from "@/lib/live";
import { PowerSenseTab } from "@/components/power-sense";
import { CameraTab } from "@/components/camera";

type Tab = "live" | "tags" | "power" | "camera" | "connection";
type TagForm = { tagId?: string; code: string; name: string; sensorTypeCode: string; unit: string; address: string; scale: string; offset: string; deadband: string; minValue: string; maxValue: string; isActive: boolean };

const qualityTone = ["ok", "warn", "bad", "muted"] as const;

export default function AssetPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params);
  const { can } = useAuth();
  const configure = can("connectivity.manage");
  const [asset, setAsset] = useState<Asset | null>(null);
  const [cat, setCat] = useState<Catalogue | null>(null);
  const [tab, setTab] = useState<Tab>("live");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    Promise.all([api<Asset>(`/api/v1/plant/assets/${id}`), api<Catalogue>("/api/v1/plant/catalogue")])
      .then(([a, c]) => {
        setAsset(a);
        setCat(c);
      })
      .catch((e) => setError(errorText(e)));
  }, [id]);

  const protocol = cat?.protocols.find((p) => p.protocolCode === asset?.protocolCode);
  const type = cat?.assetTypes.find((t) => t.assetTypeCode === asset?.assetTypeCode);

  return (
    <>
      <Link href="/app/plant" className="mb-3 inline-flex items-center gap-1 text-sm text-muted hover:text-ink"><ArrowLeft size={14} /> Plant & assets</Link>
      <PageHeader
        title={asset?.name ?? "Asset"}
        sub={asset ? `${type?.name ?? asset.assetTypeCode} · ${asset.code}${asset.vendor ? ` · ${asset.vendor}` : ""}` : undefined}
        actions={protocol && (
          <div className="flex items-center gap-2 text-sm">
            <span className="text-muted">{protocol.name}</span>
            <Badge tone={protocol.supportLevel === "Native" ? "ok" : protocol.supportLevel === "Push" ? "info" : "warn"}>{protocol.supportLevel}</Badge>
          </div>
        )}
      />
      <ErrorNote error={error} />
      <div className="mb-4 flex gap-1 border-b border-line">
        {(["live", "tags", "power", "camera", "connection"] as Tab[]).filter((t) => t !== "connection" || configure).map((t) => (
          <button key={t} onClick={() => setTab(t)}
            className={cx("-mb-px border-b-2 px-4 py-2 text-sm capitalize", tab === t ? "border-brand font-medium text-brand" : "border-transparent text-muted hover:text-ink")}>
            {t === "live" ? "Live data" : t === "power" ? "Power sense" : t}
          </button>
        ))}
      </div>
      {asset && tab === "live" && <LiveTab assetId={id} />}
      {asset && tab === "tags" && <TagsTab asset={asset} configure={configure} />}
      {asset && tab === "power" && <PowerSenseTab assetId={id} canEdit={configure} />}
      {asset && tab === "camera" && <CameraTab assetId={id} canEdit={configure} />}
      {asset && tab === "connection" && <ConnectionTab asset={asset} protocolName={protocol?.name} supportLevel={protocol?.supportLevel} />}
    </>
  );
}

// ---------------------------------------------------------------- live

function LiveTab({ assetId }: { assetId: string }) {
  const [live, setLive] = useState<AssetLive | null>(null);
  const [history, setHistory] = useState<Record<string, HistoryPoint[]>>({});
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const l = await api<AssetLive>(`/api/v1/plant/assets/${assetId}/live`);
      setLive(l);
      setError(null);
      return l;
    } catch (e) {
      setError(errorText(e));
      return null;
    }
  }, [assetId]);

  const loadHistory = useCallback(async (l: AssetLive) => {
    const from = new Date(Date.now() - 30 * 60_000).toISOString();
    const entries = await Promise.all(l.values.map(async (v) =>
      [v.tagId, await api<HistoryPoint[]>(`/api/v1/telemetry/history?tagId=${v.tagId}&from=${from}`).catch(() => [])] as const));
    setHistory(Object.fromEntries(entries));
  }, []);

  // instant updates from the live hub; the slower poll below only covers a dropped connection
  const hub = useLive({
    values: (msgs) => setLive((cur) => {
      if (!cur) return cur;
      const mine = new Map(msgs.filter((m) => m.assetId === assetId).map((m) => [m.tagId, m]));
      if (mine.size === 0) return cur;
      return { ...cur, lastSeenAt: [...mine.values()][0].ts, values: cur.values.map((v) => {
        const m = mine.get(v.tagId);
        return m ? { ...v, ts: m.ts, value: m.value, quality: m.quality } : v;
      }) };
    }),
    status: (msgs) => setLive((cur) => {
      const m = cur && msgs.find((x) => x.assetId === assetId);
      return m ? { ...cur, status: m.status as AssetLive["status"], lastError: m.lastError } : cur;
    }),
  });

  useEffect(() => {
    let alive = true;
    load().then((l) => {
      if (l && alive) loadHistory(l);
    });
    const fast = setInterval(load, 15_000);
    const slow = setInterval(() => load().then((l) => {
      if (l) loadHistory(l);
    }), 30_000);
    return () => {
      alive = false;
      clearInterval(fast);
      clearInterval(slow);
    };
  }, [load, loadHistory]);

  if (!live) return <ErrorNote error={error} />;
  return (
    <>
      <ErrorNote error={error} />
      <div className="mb-4 flex flex-wrap items-center gap-3 text-sm">
        <Badge tone={statusTone[live.status]}>{live.status}</Badge>
        <span className="text-muted">Last data {ago(live.lastSeenAt)}</span>
        {live.lastError && <span className="text-bad">⚠ {live.lastError}</span>}
        <span className="ml-auto"><LiveBadge state={hub} /></span>
      </div>
      {live.values.length === 0 ? (
        <Card><p className="text-sm text-muted">This asset has no tags yet. Add them on the Tags tab, or apply the standard template.</p></Card>
      ) : (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
          {live.values.map((v) => {
            const pts = (history[v.tagId] ?? []).map((h) => ({ t: new Date(h.ts + "Z").getTime(), v: h.value }));
            if (v.ts && v.value !== null) pts.push({ t: new Date(v.ts + "Z").getTime(), v: v.value });
            return (
              <div key={v.tagId} className="rounded-xl border border-line bg-panel p-3">
                <div className="flex items-center justify-between text-xs text-muted">
                  <span className="truncate">{v.name}</span>
                  {v.quality !== null && v.quality !== 0 && <Badge tone={qualityTone[v.quality]}>{QUALITY[v.quality]}</Badge>}
                </div>
                <div className="mt-1 text-xl font-semibold tabular-nums">
                  {v.sensorTypeCode === "machine_state" ? stateName(v.value) : fmtValue(v.value, v.unit)}
                </div>
                <Sparkline points={pts} />
                <div className="text-[10px] text-muted">{v.code} · {ago(v.ts)}</div>
              </div>
            );
          })}
        </div>
      )}
    </>
  );
}

function stateName(v: number | null) {
  return ["Offline", "Idle", "Running", "Stopped", "Fault", "Maintenance"][v ?? 0] ?? String(v);
}

// ---------------------------------------------------------------- tags

function TagsTab({ asset, configure }: { asset: Asset; configure: boolean }) {
  const [tags, setTags] = useState<Tag[]>([]);
  const [sensors, setSensors] = useState<SensorType[]>([]);
  const [form, setForm] = useState<TagForm | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [formError, setFormError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const [t, s] = await Promise.all([api<Tag[]>(`/api/v1/plant/assets/${asset.assetId}/tags`), api<SensorType[]>("/api/v1/plant/sensor-types")]);
      setTags(t);
      setSensors(s);
    } catch (e) {
      setError(errorText(e));
    }
  }, [asset.assetId]);
  useEffect(() => {
    load();
  }, [load]);

  const byCategory = useMemo(() => sensors.reduce<Record<string, SensorType[]>>((m, s) => ((m[s.category] ??= []).push(s), m), {}), [sensors]);
  const sensorName = Object.fromEntries(sensors.map((s) => [s.sensorTypeCode, s.name]));

  async function applyTemplate() {
    setError(null);
    try {
      const r = await api<{ added: number }>(`/api/v1/plant/assets/${asset.assetId}/tags/template`, { method: "POST" });
      setNote(r.added ? `Added ${r.added} standard tags.` : "All standard tags are already there.");
      await load();
    } catch (e) {
      setError(errorText(e));
    }
  }

  async function save(e: React.FormEvent) {
    e.preventDefault();
    if (!form) return;
    setFormError(null);
    const num = (s: string) => (s.trim() === "" ? null : Number(s));
    const body = {
      code: form.code, name: form.name, sensorTypeCode: form.sensorTypeCode, unit: form.unit || null, address: form.address || null,
      scale: num(form.scale) ?? 1, offset: num(form.offset) ?? 0, deadband: num(form.deadband) ?? 0, minValue: num(form.minValue), maxValue: num(form.maxValue), isActive: form.isActive,
    };
    try {
      await api(`/api/v1/plant/assets/${asset.assetId}/tags${form.tagId ? `/${form.tagId}` : ""}`, { method: form.tagId ? "PUT" : "POST", json: body });
      setForm(null);
      await load();
    } catch (err) {
      setFormError(errorText(err));
    }
  }

  async function remove(t: Tag) {
    if (!confirm(`Delete tag "${t.name}"? Its history stays in the database.`)) return;
    try {
      await api(`/api/v1/plant/assets/${asset.assetId}/tags/${t.tagId}`, { method: "DELETE" });
      await load();
    } catch (e) {
      setError(errorText(e));
    }
  }

  const blank: TagForm = { code: "", name: "", sensorTypeCode: sensors[0]?.sensorTypeCode ?? "temperature", unit: "", address: "", scale: "1", offset: "0", deadband: "0", minValue: "", maxValue: "", isActive: true };
  const s = (v: number | null) => (v === null ? "" : String(v));

  return (
    <>
      <ErrorNote error={error} />
      {note && <div className="mb-3 rounded-lg bg-ok/10 px-3 py-2 text-sm text-ok">{note}</div>}
      <Card
        title={`${tags.length} tags`}
        actions={configure && (
          <>
            <Button variant="outline" className="py-1" onClick={applyTemplate}><Wand2 size={14} /> Apply standard tags</Button>
            <Button className="py-1" onClick={() => setForm(blank)}><Plus size={14} /> Add tag</Button>
          </>
        )}
      >
        <Table head={["Code", "Name", "Sensor type", "Unit", "Address", "Scale / offset", "Deadband", "Range", ""]} empty={tags.length === 0}>
          {tags.map((t) => (
            <tr key={t.tagId} className={t.isActive ? "" : "opacity-50"}>
              <td className="px-3 py-2 font-mono text-xs">{t.code}</td>
              <td className="px-3 py-2">{t.name}</td>
              <td className="px-3 py-2 text-xs">{sensorName[t.sensorTypeCode] ?? t.sensorTypeCode}</td>
              <td className="px-3 py-2">{t.unit}</td>
              <td className="px-3 py-2 font-mono text-xs">{t.address ?? "—"}</td>
              <td className="px-3 py-2 text-xs tabular-nums">×{t.scale} {t.offset >= 0 ? "+" : "−"} {Math.abs(t.offset)}</td>
              <td className="px-3 py-2 tabular-nums">{t.deadband}</td>
              <td className="px-3 py-2 text-xs tabular-nums">{t.minValue ?? "…"} – {t.maxValue ?? "…"}</td>
              <td className="px-3 py-2">
                {configure && (
                  <div className="flex gap-1">
                    <button className="rounded p-1 hover:bg-panel-2" aria-label="Edit" onClick={() => setForm({
                      tagId: t.tagId, code: t.code, name: t.name, sensorTypeCode: t.sensorTypeCode, unit: t.unit, address: t.address ?? "",
                      scale: String(t.scale), offset: String(t.offset), deadband: String(t.deadband), minValue: s(t.minValue), maxValue: s(t.maxValue), isActive: t.isActive,
                    })}><Pencil size={14} /></button>
                    <button className="rounded p-1 text-bad hover:bg-panel-2" aria-label="Delete" onClick={() => remove(t)}><Trash2 size={14} /></button>
                  </div>
                )}
              </td>
            </tr>
          ))}
        </Table>
      </Card>

      <Modal open={!!form} title={form?.tagId ? "Edit tag" : "Add tag"} onClose={() => setForm(null)}>
        {form && (
          <form onSubmit={save} className="space-y-3">
            <ErrorNote error={formError} />
            <div className="grid gap-3 sm:grid-cols-2">
              <Field label="Name"><Input required value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} /></Field>
              <Field label="Code"><Input required value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value })} /></Field>
              <Field label="Sensor type">
                <Select value={form.sensorTypeCode} onChange={(e) => {
                  const st = sensors.find((x) => x.sensorTypeCode === e.target.value);
                  setForm({ ...form, sensorTypeCode: e.target.value, unit: st?.unit ?? form.unit, minValue: st ? s(st.minValue) : form.minValue, maxValue: st ? s(st.maxValue) : form.maxValue });
                }}>
                  {Object.entries(byCategory).map(([c, list]) => (
                    <optgroup key={c} label={c}>{list.map((x) => <option key={x.sensorTypeCode} value={x.sensorTypeCode}>{x.name}</option>)}</optgroup>
                  ))}
                </Select>
              </Field>
              <Field label="Unit"><Input value={form.unit} onChange={(e) => setForm({ ...form, unit: e.target.value })} /></Field>
            </div>
            <Field label="Address" hint={ADDRESS_HELP[asset.protocolCode ?? ""] ?? "Device-specific address of this value"}>
              <Input value={form.address} onChange={(e) => setForm({ ...form, address: e.target.value })} className="font-mono" />
            </Field>
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-5">
              <Field label="Scale"><Input type="number" step="any" value={form.scale} onChange={(e) => setForm({ ...form, scale: e.target.value })} /></Field>
              <Field label="Offset"><Input type="number" step="any" value={form.offset} onChange={(e) => setForm({ ...form, offset: e.target.value })} /></Field>
              <Field label="Deadband"><Input type="number" step="any" min={0} value={form.deadband} onChange={(e) => setForm({ ...form, deadband: e.target.value })} /></Field>
              <Field label="Min"><Input type="number" step="any" value={form.minValue} onChange={(e) => setForm({ ...form, minValue: e.target.value })} /></Field>
              <Field label="Max"><Input type="number" step="any" value={form.maxValue} onChange={(e) => setForm({ ...form, maxValue: e.target.value })} /></Field>
            </div>
            <p className="text-xs text-muted">Stored value = raw × scale + offset. History keeps a value when it moves more than the deadband (plus once a minute).</p>
            <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={form.isActive} onChange={(e) => setForm({ ...form, isActive: e.target.checked })} /> Collect this tag</label>
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

// ---------------------------------------------------------------- connection

function ConnectionTab({ asset, protocolName, supportLevel }: { asset: Asset; protocolName?: string; supportLevel?: string }) {
  const [pollMs, setPollMs] = useState("5000");
  const [settings, setSettings] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [test, setTest] = useState<TestResult | null>(null);
  const [testing, setTesting] = useState(false);
  const [key, setKey] = useState<string | null>(null);

  useEffect(() => {
    api<{ pollMs: number; connectorSettings: string | null }>(`/api/v1/plant/assets/${asset.assetId}/connection`)
      .then((c) => {
        setPollMs(String(c.pollMs));
        setSettings(c.connectorSettings ? JSON.stringify(JSON.parse(c.connectorSettings), null, 2) : "");
      })
      .catch((e) => setError(errorText(e)));
  }, [asset.assetId]);

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    setSaved(false);
    try {
      await api(`/api/v1/plant/assets/${asset.assetId}/connection`, { method: "PUT", json: { pollMs: Number(pollMs), connectorSettings: settings.trim() || null } });
      setSaved(true);
    } catch (err) {
      setError(errorText(err));
    }
  }

  async function runTest() {
    setTesting(true);
    setTest(null);
    try {
      setTest(await api<TestResult>(`/api/v1/plant/assets/${asset.assetId}/test`, { method: "POST" }));
    } catch (e) {
      setError(errorText(e));
    } finally {
      setTesting(false);
    }
  }

  async function newKey() {
    if (!confirm("Create a new device key? The current key stops working at once.")) return;
    try {
      setKey((await api<{ key: string }>(`/api/v1/plant/assets/${asset.assetId}/device-keys`, { method: "POST" })).key);
    } catch (e) {
      setError(errorText(e));
    }
  }

  const code = asset.protocolCode ?? "";
  return (
    <div className="grid gap-4 lg:grid-cols-2">
      <Card title="Connection">
        <form onSubmit={save} className="space-y-3">
          <ErrorNote error={error} />
          {saved && <div className="rounded-lg bg-ok/10 px-3 py-2 text-sm text-ok">Saved. The worker reconnects with the new settings within 30 seconds.</div>}
          <dl className="grid grid-cols-2 gap-2 text-sm">
            <dt className="text-muted">Protocol</dt><dd>{protocolName ?? "not set"} {supportLevel && <Badge>{supportLevel}</Badge>}</dd>
            <dt className="text-muted">Endpoint</dt><dd className="font-mono text-xs">{asset.endpoint ?? "—"}</dd>
          </dl>
          {supportLevel === "Gateway" && <p className="rounded-lg bg-warn/10 p-3 text-xs text-warn">This protocol has no built-in driver. Connect the device through an OPC UA or MQTT gateway (Kepware, Node-RED, Ignition) and set the asset&apos;s protocol to OPC UA or MQTT.</p>}
          {supportLevel === "Planned" && <p className="rounded-lg bg-info/10 p-3 text-xs text-info">The native driver for this protocol arrives in a later phase. Until then, use an OPC UA or MQTT gateway.</p>}
          <Field label="Poll interval (ms)" hint="How often the device is read: 250 ms to 1 hour">
            <Input type="number" min={250} value={pollMs} onChange={(e) => setPollMs(e.target.value)} />
          </Field>
          <Field label="Connector settings (JSON)" hint={SETTINGS_HELP[code] ? `Example: ${SETTINGS_HELP[code]}. Passwords are stored and shown masked.` : "Not needed for this protocol."}>
            <textarea value={settings} onChange={(e) => setSettings(e.target.value)} rows={5}
              className="w-full rounded-lg border border-line bg-panel px-3 py-2 font-mono text-xs outline-none focus:border-brand" />
          </Field>
          <div className="flex gap-2">
            <Button type="submit">Save</Button>
            <Button type="button" variant="outline" onClick={runTest} disabled={testing}><PlugZap size={14} /> {testing ? "Testing…" : "Test connection"}</Button>
          </div>
          <p className="text-xs text-muted">Change the endpoint or protocol from the asset&apos;s edit form on the Plant page.</p>
        </form>
      </Card>

      <div className="space-y-4">
        {test && (
          <Card title={test.connected ? "Connected ✔" : "Could not connect"}>
            {test.error && <p className="mb-2 text-sm text-bad">{test.error}</p>}
            {test.values.length > 0 && (
              <Table head={["Tag", "Value", "Quality"]}>
                {test.values.map((v) => (
                  <tr key={v.tagId}>
                    <td className="px-3 py-1.5">{v.name}</td>
                    <td className="px-3 py-1.5 tabular-nums">{fmtValue(v.value, v.unit)}</td>
                    <td className="px-3 py-1.5"><Badge tone={qualityTone[v.quality ?? 2]}>{QUALITY[v.quality ?? 2]}</Badge></td>
                  </tr>
                ))}
              </Table>
            )}
            {test.connected && test.values.length === 0 && <p className="text-sm text-muted">Connected, but no tag returned a value. Check the tag addresses.</p>}
          </Card>
        )}
        {(code === "http" || code === "nbiot" || code === "lorawan") && (
          <Card title="Device key (HTTP push)" actions={<Button variant="outline" className="py-1" onClick={newKey}><KeyRound size={14} /> New key</Button>}>
            {key ? (
              <div className="space-y-2 text-sm">
                <p className="text-warn">Copy this key now; it will not be shown again.</p>
                <code className="block break-all rounded bg-panel-2 p-2 text-xs">{key}</code>
              </div>
            ) : <p className="text-sm text-muted">Devices send readings with this key. Creating a new key revokes the old one.</p>}
            <pre className="mt-3 overflow-x-auto rounded bg-panel-2 p-2 text-[11px]">{`POST /api/v1/ingest
X-Device-Key: fos_…
{"readings":[{"tag":"temp","value":23.5}]}`}</pre>
          </Card>
        )}
      </div>
    </div>
  );
}
