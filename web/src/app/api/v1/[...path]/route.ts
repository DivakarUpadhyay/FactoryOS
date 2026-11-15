// Hosted demo (NEXT_PUBLIC_DEMO=1): the site answers the API itself from a recorded snapshot of the demo factory
// (demo-data/snapshot.json, made by scripts/record-demo.js while signed in as the read-only demo viewer), so the public
// demo needs no backend. Every timestamp is moved forward by the time since recording, so the data always looks current.
import { readFileSync } from "fs";
import path from "path";
import { DEMO_ACCOUNTS, DEMO_PASSWORD, demoAccountByEmail, type DemoAccount } from "@/lib/demo-accounts";
import { allPermissions, audit, missingPermission, report, roles, schedules, toCsv, users } from "@/lib/demo-extras";

export const dynamic = "force-dynamic";

type Recorded = { status: number; type: string; body?: string; b64?: string };
type Snapshot = { recordedAt: string; responses: Record<string, Recorded> };

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
  const seed: { assetId: string; status: string; tagId: string; sensorTypeCode: string; value: number }[] = [];
  for (const [key, rec] of Object.entries(snapshot!.responses)) {
    if (!/^GET \/api\/v1\/plant\/assets\/[^/]+\/live$/.test(key) || !rec.body) continue;
    const live = JSON.parse(rec.body) as { assetId: string; status: string; values: { tagId: string; sensorTypeCode: string; value: number | null }[] };
    for (const v of live.values) if (v.value !== null) seed.push({ assetId: live.assetId, status: live.status, tagId: v.tagId, sensorTypeCode: v.sensorTypeCode, value: v.value });
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

/** The demo cameras are still pictures in public/demo-cameras (the recording keeps no image bytes), so a snapshot
 *  request is sent on to the picture named in the asset's recorded camera settings. */
function cameraPicture(assetId: string, url: URL) {
  const rec = byPath!.get(`GET /api/v1/plant/assets/${assetId}/camera`);
  const file = rec?.body ? (JSON.parse(rec.body).snapshotUrl as string | null)?.match(/\/demo-cameras\/[\w-]+\.jpg$/)?.[0] : null;
  if (!file) return problem(404, "This machine has no camera in the online demo.");
  return Response.redirect(new URL(file, url.origin), 302);
}

// ---------- one sign-in per role ----------
// The recorded sign-in belongs to the viewer. For each demo role the token and /auth/me are rebuilt with that role's
// name, email and permissions, so the app shows exactly the menus and buttons the role would get from the real API.
const b64url = (o: object) => Buffer.from(JSON.stringify(o)).toString("base64url");

function tokenFor(a: DemoAccount) {
  const now = Math.floor(Date.now() / 1000);
  const payload = { sub: a.userId, email: a.email, name: a.displayName, sa: "0", tid: "4acf7dd5-a3f0-4958-afaa-c19dd909074c",
    perm: a.permissions, jti: crypto.randomUUID().replace(/-/g, ""), nbf: now, exp: now + 3600, iss: "divakar-factoryos", aud: "divakar-factoryos" };
  return `${b64url({ alg: "HS256", typ: "JWT" })}.${b64url(payload)}.online-demo`;
}

function accountFromToken(req: Request): DemoAccount | null {
  const token = req.headers.get("authorization")?.replace(/^Bearer\s+/i, "");
  try {
    const payload = JSON.parse(Buffer.from(token!.split(".")[1], "base64url").toString("utf8"));
    return demoAccountByEmail(payload.email) ?? null;
  } catch { return null; }
}

function meFor(a: DemoAccount) {
  const me = JSON.parse(byPath!.get("GET /api/v1/auth/me")!.body!);
  return { ...me, userId: a.userId, email: a.email, displayName: a.displayName, permissions: a.permissions };
}

function signIn(a: DemoAccount) {
  const recorded = JSON.parse(byPath!.get("POST /api/v1/auth/login")!.body!);
  return Response.json({ ...recorded, accessToken: tokenFor(a), accessTokenExpiresAt: new Date(Date.now() + 3_600_000).toISOString(),
    refreshToken: `online-demo.${a.role}`, user: { ...recorded.user, ...meFor(a) } });
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
    const account = demoAccountByEmail(body.email);
    if (!account || body.password !== DEMO_PASSWORD)
      return problem(401, "This online demo signs in with one of the demo accounts shown on this page.");
    return signIn(account);
  }
  if (method === "POST" && route === "/api/v1/auth/refresh") {
    const body = await req.json().catch(() => ({}));
    const account = DEMO_ACCOUNTS.find((a) => body.refreshToken === `online-demo.${a.role}`);
    return account ? signIn(account) : problem(401, "Please sign in again.");
  }
  if (method === "POST" && route === "/api/v1/auth/logout") return new Response(null, { status: 204 });

  const account = accountFromToken(req);
  if (route === "/api/v1/auth/me") return account ? Response.json(meFor(account)) : problem(401, "Please sign in.");
  if (account) {
    const need = missingPermission(route, account);
    if (need) return problem(403, `The ${account.roleName} role does not have the ${need} permission.`);
  }
  if (method !== "GET") return problem(403, `This is a read-only online demo, so changes are switched off (signed in as ${account?.roleName ?? "guest"}).`);

  if (route === "/api/v1/users") return Response.json(users());
  if (route === "/api/v1/roles") return Response.json(roles());
  if (route === "/api/v1/roles/permissions") return Response.json(allPermissions());
  if (route === "/api/v1/audit") return Response.json(audit(url));
  if (route === "/api/v1/reports/schedules") return Response.json(schedules());
  const rep = /^\/api\/v1\/reports\/(production|energy|maintenance|quality|alarms)$/.exec(route);
  if (rep) {
    const r = report(rep[1], url, (k) => { const x = byPath!.get(k); return x?.body ? JSON.parse(x.body) : null; });
    if (!r) return problem(404, "This report is not part of the online demo.");
    if (url.searchParams.get("format") === "csv")
      return new Response(toCsv(r), { headers: { "Content-Type": "text/csv", "Content-Disposition": `attachment; filename="${rep[1]}-report.csv"` } });
    return Response.json(r);
  }
  if (route === "/api/v1/inventory/lookup") return lookup(url.searchParams.get("code") ?? "");
  const cam = /^\/api\/v1\/plant\/assets\/([^/]+)\/camera\/snapshot$/.exec(route);
  if (cam) return cameraPicture(cam[1], url);

  const rec = byKey!.get(normalise(`GET ${route}${url.search}`)) ?? byPath!.get(`GET ${route}`);
  return rec ? reply(rec) : problem(404, "This view is not part of the online demo.");
}

export { handle as GET, handle as POST, handle as PUT, handle as PATCH, handle as DELETE };
