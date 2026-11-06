// Hosted demo (NEXT_PUBLIC_DEMO=1): the site answers the API itself from a recorded snapshot of the demo factory
// (demo-data/snapshot.json, made by scripts/record-demo.js while signed in as the read-only demo viewer), so the public
// demo needs no backend. Every timestamp is moved forward by the time since recording, so the data always looks current.
import { readFileSync } from "fs";
import path from "path";

export const dynamic = "force-dynamic";

type Recorded = { status: number; type: string; body?: string; b64?: string };
type Snapshot = { recordedAt: string; responses: Record<string, Recorded> };

const DEMO_USER = { email: "demo@divakar-demo.local", password: "FactoryDemo@2026" };
const ENABLED = process.env.NEXT_PUBLIC_DEMO === "1";

let snapshot: Snapshot | null = null;
let byKey: Map<string, Recorded> | null = null;
let byPath: Map<string, Recorded> | null = null;

/** Timestamps and dates in a query vary with the clock, so they are left out when matching recorded requests. */
function normalise(key: string) {
  return decodeURIComponent(key).replace(/\d{4}-\d{2}-\d{2}(T[\d:.]+(Z|[+-]\d{2}:\d{2})?)?/g, "*");
}

function load() {
  if (snapshot) return;
  snapshot = JSON.parse(readFileSync(path.join(process.cwd(), "demo-data", "snapshot.json"), "utf8")) as Snapshot;
  byKey = new Map();
  byPath = new Map();
  for (const [key, rec] of Object.entries(snapshot.responses)) {
    byKey.set(normalise(key), rec);
    const p = key.split("?")[0];
    if (!byPath.has(p) || rec.status < 400) byPath.set(p, rec);
  }
}

function shift(text: string) {
  const delta = Date.now() - Date.parse(snapshot!.recordedAt);
  const days = Math.round(delta / 86_400_000);
  return text
    .replace(/(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2}:\d{2})(\.\d+)?(Z|[+-]\d{2}:\d{2})?/g, (_, d, t, frac, zone) => {
      const iso = new Date(Date.parse(`${d}T${t}${frac ?? ""}${zone ?? "Z"}`) + delta).toISOString();
      return iso.slice(0, 19) + (frac ? iso.slice(19, 23) : "") + (zone ? "Z" : "");
    })
    .replace(/"(\d{4}-\d{2}-\d{2})"/g, (_, d) => `"${new Date(Date.parse(`${d}T00:00:00Z`) + days * 86_400_000).toISOString().slice(0, 10)}"`);
}

function reply(rec: Recorded) {
  if (rec.b64) return new Response(Buffer.from(rec.b64, "base64"), { status: rec.status, headers: { "Content-Type": rec.type } });
  if (!rec.body) return new Response(null, { status: rec.status === 200 ? 204 : rec.status });
  return new Response(shift(rec.body), { status: rec.status, headers: { "Content-Type": rec.type || "application/json" } });
}

const problem = (status: number, detail: string) => Response.json({ status, detail }, { status });

/** The demo's live feed: every tag's last recorded value, which the browser varies a little to look live. */
function liveSeed() {
  const seed: { assetId: string; tagId: string; sensorTypeCode: string; value: number }[] = [];
  for (const [key, rec] of Object.entries(snapshot!.responses)) {
    if (!/^GET \/api\/v1\/plant\/assets\/[^/]+\/live$/.test(key) || !rec.body) continue;
    const live = JSON.parse(rec.body) as { assetId: string; values: { tagId: string; sensorTypeCode: string; value: number | null }[] };
    for (const v of live.values) if (v.value !== null) seed.push({ assetId: live.assetId, tagId: v.tagId, sensorTypeCode: v.sensorTypeCode, value: v.value });
  }
  return Response.json(seed);
}

/** Barcode / code / lot lookup answered from the recorded stock, with the same rules as the API (item barcode or code, then lot). */
function lookup(code: string) {
  const c = code.trim().toLowerCase();
  const body = (key: string) => { const r = byPath!.get(key); return r?.body ? JSON.parse(r.body) : []; };
  const items = body("GET /api/v1/inventory/items") as { code: string; barcode: string | null }[];
  const item = items.find((i) => i.barcode?.toLowerCase() === c || i.code.toLowerCase() === c);
  if (item) return Response.json({ kind: "item", item, lot: null });
  const lot = (body("GET /api/v1/inventory/lots") as { lotNo: string }[]).find((l) => l.lotNo.toLowerCase() === c);
  if (lot) return Response.json({ kind: "lot", item: null, lot });
  return problem(404, `An item or lot with the code '${code.trim()}' was not found.`);
}

async function handle(req: Request, { params }: { params: Promise<{ path: string[] }> }) {
  if (!ENABLED) return problem(404, "Not found");
  load();
  const url = new URL(req.url);
  const route = `/api/v1/${(await params).path.join("/")}`;
  const method = req.method;

  if (method === "GET" && route === "/api/v1/demo/live-seed") return liveSeed();
  if (method === "POST" && route === "/api/v1/auth/login") {
    const body = await req.json().catch(() => ({}));
    if (body.email?.trim().toLowerCase() !== DEMO_USER.email || body.password !== DEMO_USER.password)
      return problem(401, "This online demo signs in with the demo account shown on this page.");
    return reply(byPath!.get(`POST ${route}`)!);
  }
  if (method === "POST" && route === "/api/v1/auth/refresh") return reply(byPath!.get(`POST ${route}`)!);
  if (method === "POST" && route === "/api/v1/auth/logout") return new Response(null, { status: 204 });
  if (method !== "GET") return problem(403, "This is a read-only demo, so changes are switched off.");
  if (route === "/api/v1/inventory/lookup") return lookup(url.searchParams.get("code") ?? "");

  const rec = byKey!.get(normalise(`GET ${route}${url.search}`)) ?? byPath!.get(`GET ${route}`);
  return rec ? reply(rec) : problem(404, "This view is not part of the online demo.");
}

export { handle as GET, handle as POST, handle as PUT, handle as PATCH, handle as DELETE };
