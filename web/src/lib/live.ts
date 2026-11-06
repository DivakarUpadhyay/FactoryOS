// One shared SignalR connection per browser tab. Pages subscribe to "values" (changed tag values) and
// "status" (changed asset states); the connection starts with the first subscriber and stops with the last.
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";
import { useEffect, useRef, useState } from "react";
import { API_URL, DEMO, hubToken } from "./api";

export type LiveValueMsg = { assetId: string; tagId: string; ts: string; value: number | null; quality: number };
export type LiveStatusMsg = { assetId: string; status: string; lastSeenAt: string | null; lastError: string | null };
export type LiveAlarmMsg = { alarmId: number; assetId: string; severity: "Info" | "Warning" | "Critical"; message: string };

type Handlers = { values?: (v: LiveValueMsg[]) => void; status?: (s: LiveStatusMsg[]) => void; alarms?: (a: LiveAlarmMsg[]) => void };

let connection: HubConnection | null = null;
const subscribers = new Set<Handlers>();
const stateListeners = new Set<(s: string) => void>();

function setState(s: string) {
  stateListeners.forEach((l) => l(s));
}

// Hosted demo: no hub to connect to, so the feed is simulated from each tag's recorded value (a small wobble for
// measurements, a slow climb for counters), with the same messages the hub would send.
let demoTimer: ReturnType<typeof setInterval> | null = null;

function startDemo() {
  if (demoTimer) return;
  type Seed = { assetId: string; tagId: string; sensorTypeCode: string; value: number };
  let seed: Seed[] = [];
  const counters = new Map<string, number>();
  let tick = 0;
  fetch(`${API_URL}/api/v1/demo/live-seed`).then((r) => r.json()).then((s: Seed[]) => (seed = s)).catch(() => undefined);
  setState("live");
  demoTimer = setInterval(() => {
    if (!seed.length) return;
    const ts = new Date().toISOString();
    const values: LiveValueMsg[] = seed
      .filter(() => Math.random() < 0.4)
      .map((s) => {
        let value: number;
        if (/kwh|count|total|hours|runtime/i.test(s.sensorTypeCode)) {
          value = (counters.get(s.tagId) ?? s.value) + Math.abs(s.value) * 0.00002 + Math.random() * 0.05;
          counters.set(s.tagId, value);
        } else value = s.value * (1 + (Math.random() - 0.5) * 0.03);
        return { assetId: s.assetId, tagId: s.tagId, ts, value: Math.round(value * 1000) / 1000, quality: 0 };
      });
    subscribers.forEach((h) => h.values?.(values));
    if (tick++ % 5 === 0) {
      const status = [...new Set(seed.map((s) => s.assetId))].map((assetId) => ({ assetId, status: "Running", lastSeenAt: ts, lastError: null }));
      subscribers.forEach((h) => h.status?.(status));
    }
  }, 2000);
}

function ensure() {
  if (DEMO) return startDemo();
  if (connection) return;
  connection = new HubConnectionBuilder()
    .withUrl(`${API_URL}/hubs/live`, { accessTokenFactory: hubToken })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(LogLevel.Warning)
    .build();
  connection.on("values", (v: LiveValueMsg[]) => subscribers.forEach((s) => s.values?.(v)));
  connection.on("status", (v: LiveStatusMsg[]) => subscribers.forEach((s) => s.status?.(v)));
  connection.on("alarms", (v: LiveAlarmMsg[]) => subscribers.forEach((s) => s.alarms?.(v)));
  connection.onreconnecting(() => setState("reconnecting"));
  connection.onreconnected(() => setState("live"));
  connection.onclose(() => setState("offline"));
  const start = () =>
    connection!.start().then(() => setState("live")).catch(() => {
      setState("offline");
      setTimeout(() => connection && connection.state === HubConnectionState.Disconnected && start(), 5000);
    });
  start();
}

/** Subscribes the component to live messages; returns the connection state ("live", "reconnecting", "offline", "connecting"). */
export function useLive(handlers: Handlers): string {
  const ref = useRef(handlers);
  ref.current = handlers;
  const [state, setLocal] = useState(connection?.state === HubConnectionState.Connected ? "live" : "connecting");
  useEffect(() => {
    const proxy: Handlers = { values: (v) => ref.current.values?.(v), status: (s) => ref.current.status?.(s), alarms: (a) => ref.current.alarms?.(a) };
    subscribers.add(proxy);
    stateListeners.add(setLocal);
    ensure();
    if (connection?.state === HubConnectionState.Connected || demoTimer) setLocal("live");
    return () => {
      subscribers.delete(proxy);
      stateListeners.delete(setLocal);
      // stop a moment later, so moving between pages (or React's double mount in development) keeps one connection
      setTimeout(() => {
        if (subscribers.size === 0 && demoTimer) {
          clearInterval(demoTimer);
          demoTimer = null;
        }
        if (subscribers.size === 0 && connection) {
          const c = connection;
          connection = null;
          c.stop();
        }
      }, 1500);
    };
  }, []);
  return state;
}
