// Records the FactoryOS demo (every API response the pages ask for) from the live site, signed in as the demo viewer.
// Run it while the site uses the real API (start-factoryos-public.ps1) and the worker has been running for a while.
// usage (from web/): node scripts/record-demo.js demo-data/snapshot.json [site]
const { chromium } = require(process.env.PLAYWRIGHT_PATH || "playwright"); // any Playwright install; uses installed Chrome
const fs = require("fs");

const SITE = process.argv[3] || "https://factoryos-alpha.vercel.app";
const out = process.argv[2];
const responses = {}; // "GET /api/v1/x?y" -> { status, type, body | b64 }
let apiHost = null;

(async () => {
  const browser = await chromium.launch({ channel: "chrome", headless: true });
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });

  page.on("response", async (res) => {
    const u = new URL(res.url());
    if (!u.pathname.startsWith("/api/v1/") || u.origin === new URL(SITE).origin) return;
    apiHost ??= u.origin;
    const req = res.request();
    if (req.method() === "OPTIONS") return;
    const key = `${req.method()} ${u.pathname}${u.search}`;
    try {
      const type = res.headers()["content-type"] || "";
      const buf = await res.body();
      const entry = { status: res.status(), type };
      if (/json|text/.test(type) || buf.length === 0) entry.body = buf.toString("utf8");
      else entry.b64 = buf.toString("base64");
      if (!responses[key] || responses[key].status >= 400) responses[key] = entry;
    } catch {}
  });

  await page.goto(`${SITE}/login`);
  await page.fill('input[type="email"]', "demo@divakar-demo.local");
  await page.fill('input[type="password"]', "FactoryDemo@2026");
  await page.locator('button[type="submit"]').first().click();
  // the app signs in with a client-side route change, so watch the address rather than wait for a page load
  for (let i = 0; i < 180 && !page.url().includes("/app"); i++) await page.waitForTimeout(1000);
  await page.waitForTimeout(3000);

  const paths = ["/app", "/app/plant", "/app/floor", "/app/connectivity", "/app/energy", "/app/power", "/app/carbon",
    "/app/production", "/app/robotics", "/app/maintenance", "/app/alarms", "/app/quality", "/app/inventory",
    "/app/reports", "/app/dashboards", "/app/twin", "/app/assistant", "/app/audit", "/app/users", "/app/roles",
    "/app/security", "/app/settings", "/app/account", "/op", "/app/enms", "/app/ai", "/app/workflows"];

  async function visit(p) {
    try {
      await page.goto(`${SITE}${p}`, { waitUntil: "networkidle", timeout: 45000 });
    } catch {}
    await page.waitForTimeout(1500);
    const tabs = page.locator("button.-mb-px");
    const n = await tabs.count();
    for (let i = 0; i < n; i++) {
      try {
        await tabs.nth(i).click({ timeout: 3000 });
        await page.waitForLoadState("networkidle", { timeout: 15000 }).catch(() => {});
        await page.waitForTimeout(800);
      } catch {}
    }
    console.log(`visited ${p} (${n} tabs), ${Object.keys(responses).length} responses`);
  }

  for (const p of paths) await visit(p);

  // Views the pages open on demand (one per machine, EnPI or lot), fetched straight from the API as the demo viewer
  const login = await (await fetch(`${apiHost}/api/v1/auth/login`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email: "demo@divakar-demo.local", password: "FactoryDemo@2026" }),
  })).json();
  async function grab(path) {
    const r = await fetch(`${apiHost}${path}`, { headers: { Authorization: `Bearer ${login.accessToken}` } });
    const body = await r.text();
    responses[`GET ${path}`] = { status: r.status, type: r.headers.get("content-type") || "application/json", body };
    return r.ok && body ? JSON.parse(body) : null;
  }
  const body = (key) => (responses[key]?.body ? JSON.parse(responses[key].body) : null);
  const home = body("GET /api/v1/production/operator") ?? (await grab("/api/v1/production/operator"));
  for (const m of home?.machines ?? []) await grab(`/api/v1/production/operator/entries?assetId=${m.assetId}`);
  for (const e of body("GET /api/v1/enms/enpis") ?? []) await grab(`/api/v1/enms/enpis/${e.enpiId}/results`);
  for (const p of body("GET /api/v1/enms/mv") ?? (await grab("/api/v1/enms/mv")) ?? []) await grab(`/api/v1/enms/mv/${p.planId}/result`);
  for (const m of body("GET /api/v1/enms/meters") ?? (await grab("/api/v1/enms/meters")) ?? []) await grab(`/api/v1/enms/meters/${m.meter.meterId}/calibrations`);
  await grab("/api/v1/ai/insights?all=true");
  const lotNos = (body("GET /api/v1/inventory/lots") ?? []).map((l) => l.lotNo);
  for (const lotNo of lotNos) await grab(`/api/v1/trace?lot=${encodeURIComponent(lotNo)}`);
  console.log(`extras: ${(home?.machines ?? []).length} machines, ${(body("GET /api/v1/enms/enpis") ?? []).length} EnPIs, ${lotNos.length} lot traces`);

  // every asset's detail page
  const list = Object.entries(responses).find(([k]) => /^GET \/api\/v1\/plant\/floor$/.test(k));
  let ids = [];
  try {
    const body = JSON.parse(list[1].body);
    ids = (Array.isArray(body) ? body : body.items ?? []).map((a) => a.id ?? a.assetId).filter(Boolean);
  } catch {}
  console.log(`${ids.length} assets`);
  for (const id of ids) await visit(`/app/plant/assets/${id}`);

  fs.writeFileSync(out, JSON.stringify({ recordedAt: new Date().toISOString(), apiHost, responses }));
  console.log(`saved ${Object.keys(responses).length} responses`);
  await browser.close();
})().catch((e) => { console.error(e); process.exit(1); });


