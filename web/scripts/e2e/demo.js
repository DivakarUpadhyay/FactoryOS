// Checks the hosted-demo build (NEXT_PUBLIC_DEMO=1) the way a visitor uses it: demo sign-in, every page and tab, then the
// on-demand views (EnPIs, inventory scans, machine cameras, shop-floor machines). Reports any request the snapshot cannot answer.
// usage: PLAYWRIGHT_PATH=... node web/scripts/e2e/demo.js [site]   (default http://localhost:3123)
const { chromium } = require(process.env.PLAYWRIGHT_PATH);
const SITE = process.argv[2] || "http://localhost:3123";
const PAGES = ["/app", "/app/dashboards", "/app/assistant", "/app/plant", "/app/floor", "/app/twin", "/app/connectivity", "/app/energy", "/app/power",
  "/app/carbon", "/app/enms", "/app/production", "/op", "/app/robotics", "/app/maintenance", "/app/alarms", "/app/ai", "/app/quality", "/app/inventory",
  "/app/reports", "/app/settings", "/app/account", "/app/security"];

(async () => {
  const browser = await chromium.launch({ channel: "chrome", headless: !!process.env.HEADLESS });
  const page = await (await browser.newContext({ viewport: { width: 1440, height: 900 } })).newPage();
  const problems = [], passed = [];
  let where = "";
  page.on("response", (r) => {
    const u = new URL(r.url());
    // 403 is the demo refusing changes or a page the viewer may not open; 404 means the snapshot has no answer
    if (u.pathname.startsWith("/api/") && r.status() >= 400 && r.status() !== 403 && !(u.pathname.includes("/inventory/lookup") && where.includes("unknown code")))
      problems.push(`${where}: ${r.status()} ${u.pathname}${u.search}`);
  });
  page.on("pageerror", (e) => problems.push(`${where}: page crash ${e.message.slice(0, 140)}`));
  const check = (ok, what) => (ok ? passed : problems).push(`${where}: ${what}`);

  await page.goto(`${SITE}/login`);
  await page.fill('input[type="email"]', "demo@divakar-demo.local");
  await page.fill('input[type="password"]', "FactoryDemo@2026");
  await page.locator('button[type="submit"]').first().click();
  for (let i = 0; i < 180 && !page.url().includes("/app"); i++) await page.waitForTimeout(1000);

  for (const p of PAGES) {
    where = p;
    await page.goto(`${SITE}${p}`, { waitUntil: "networkidle", timeout: 120000 }).catch(() => {});
    await page.waitForTimeout(800);
    const tabs = page.locator("button.-mb-px");
    for (let i = 0; i < await tabs.count(); i++) {
      where = `${p} [${(await tabs.nth(i).innerText()).trim()}]`;
      await tabs.nth(i).click().catch(() => {});
      await page.waitForLoadState("networkidle", { timeout: 15000 }).catch(() => {});
      await page.waitForTimeout(400);
    }
    where = p;
    check((await page.locator("h1").count()) > 0 || p === "/op", "loaded");
  }

  where = "ISO 50001 EnPIs";
  await page.goto(`${SITE}/app/enms`, { waitUntil: "networkidle" });
  await page.locator("button.-mb-px", { hasText: "EnPIs" }).click();
  const rows = page.locator("tr.cursor-pointer");
  await rows.first().waitFor({ timeout: 20000 }).catch(() => {});
  for (let i = 0; i < await rows.count(); i++) {
    await rows.nth(i).click();
    const ok = await page.getByText("Expected (baseline, normalised)").waitFor({ timeout: 20000 }).then(() => true, () => false);
    check(ok, `EnPI ${i + 1} results`);
  }

  const scan = async (code) => {
    await page.goto(`${SITE}/app/inventory`, { waitUntil: "networkidle" });
    await page.getByLabel("Scan", { exact: true }).fill(code);
    await page.getByRole("button", { name: "Look up" }).click();
    await page.waitForTimeout(1500);
  };
  const lots = await page.evaluate(async () => (await (await fetch("/api/v1/inventory/lots")).json()).map((l) => l.lotNo)).catch(() => []);
  const items = await page.evaluate(async () => (await (await fetch("/api/v1/inventory/items")).json()).map((i) => i.code)).catch(() => []);
  if (lots[0]) {
    where = `inventory scan lot ${lots[0]}`;
    await scan(lots[0]);
    check(await page.getByText(new RegExp(`Lot ${lots[0]}`)).count() > 0, "lot found and its traceability opens");
  }
  if (items[0]) {
    where = `inventory scan item ${items[0]}`;
    await scan(items[0].toUpperCase());
    check(await page.getByText(/on hand/).count() > 0, "item found (case-insensitive)");
  }
  where = "inventory scan unknown code";
  await scan("NO-SUCH-CODE-123");
  check(await page.getByText(/was not found/).count() > 0, "unknown code gives a clear message");

  where = "machine camera";
  const chiller = await page.evaluate(async () => (await (await fetch("/api/v1/plant/floor")).json()).find((a) => a.code === "demo-ch-01")?.assetId);
  await page.goto(`${SITE}/app/plant/assets/${chiller}`, { waitUntil: "networkidle" });
  await page.locator("button.-mb-px", { hasText: "Camera" }).click();
  const img = await page.locator('img[alt="Camera picture"]').waitFor({ timeout: 20000 }).then(() => true, () => false);
  check(img, "Chiller 1 camera picture shows");

  where = "shop-floor machine";
  await page.goto(`${SITE}/op`, { waitUntil: "networkidle" });
  const firstMachine = page.locator("main button").first();
  await firstMachine.click().catch(() => {});
  await page.waitForLoadState("networkidle").catch(() => {});
  await page.waitForTimeout(800);
  check(await page.getByLabel("Back").count() > 0, "machine opens with its entries");

  await browser.close();
  console.log(`PASSED (${passed.length})`); passed.forEach((x) => console.log("  ✓ " + x));
  console.log(`PROBLEMS (${problems.length})`); problems.forEach((x) => console.log("  ✗ " + x));
})().catch((e) => { console.error(e); process.exit(1); });
