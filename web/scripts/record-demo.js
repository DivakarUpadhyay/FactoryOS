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
  await page.waitForURL(/\/app/, { timeout: 180000 });
  await page.waitForTimeout(3000);

  const paths = ["/app", "/app/plant", "/app/floor", "/app/connectivity", "/app/energy", "/app/power", "/app/carbon",
    "/app/production", "/app/robotics", "/app/maintenance", "/app/alarms", "/app/quality", "/app/inventory",
    "/app/reports", "/app/dashboards", "/app/twin", "/app/assistant", "/app/audit", "/app/users", "/app/roles",
    "/app/security", "/app/settings", "/app/account"];

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


