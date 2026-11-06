// Validation sweep: on every page and tab, opens each "add" form (buttons with a + icon), submits it empty and checks the
// app refuses it clearly (disabled save, browser-required field, or an error message) without a server error.
const { chromium } = require(process.env.PLAYWRIGHT_PATH);
const fs = require("fs");
const SITE = process.argv[3] || "http://localhost:3000";
const pw = fs.readFileSync(process.argv[2], "utf8").split("admin@divakar-demo.local")[1].match(/Password:\s*(\S+)/)[1];
const PAGES = ["/app/dashboards", "/app/plant", "/app/connectivity", "/app/energy", "/app/enms", "/app/production", "/app/robotics", "/app/maintenance",
  "/app/alarms", "/app/quality", "/app/inventory", "/app/reports", "/app/users", "/app/roles", "/app/settings", "/app/account"];

(async () => {
  const browser = await chromium.launch({ channel: "chrome", headless: !!process.env.HEADLESS });
  const page = await (await browser.newContext({ viewport: { width: 1440, height: 900 } })).newPage();
  const serverErrors = [];
  let where = "";
  page.on("response", (r) => { if (r.url().includes("/api/") && r.status() >= 500) serverErrors.push(`${where}: ${r.status()} ${r.request().method()} ${new URL(r.url()).pathname}`); });
  page.on("pageerror", (e) => serverErrors.push(`${where}: page crash ${e.message.slice(0, 120)}`));
  await page.goto(`${SITE}/login`);
  await page.fill('input[type="email"]', "admin@divakar-demo.local");
  await page.fill('input[type="password"]', pw);
  await page.locator('button[type="submit"]').first().click();
  for (let i = 0; i < 180 && !page.url().includes("/app"); i++) await page.waitForTimeout(1000);

  const ok = [], gaps = [];
  for (const p of PAGES) {
    await page.goto(`${SITE}${p}`, { waitUntil: "networkidle", timeout: 120000 }).catch(() => {});
    await page.waitForTimeout(800);
    const tabs = page.locator("main button.-mb-px");
    const nTabs = Math.max(1, await tabs.count());
    for (let t = 0; t < nTabs; t++) {
      let tabName = "";
      if (await tabs.count()) {
        tabName = (await tabs.nth(t).innerText()).trim();
        await tabs.nth(t).click().catch(() => {});
        await page.waitForLoadState("networkidle", { timeout: 15000 }).catch(() => {});
        await page.waitForTimeout(400);
      }
      const adders = page.locator("main button:has(svg.lucide-plus)");
      const nAdd = await adders.count();
      for (let a = 0; a < nAdd; a++) {
        const btn = adders.nth(a);
        if (!(await btn.isVisible().catch(() => false)) || !(await btn.isEnabled().catch(() => false))) continue;
        const name = ((await btn.innerText().catch(() => "")) || (await btn.getAttribute("aria-label")) || "+").trim();
        where = `${p}${tabName ? ` [${tabName}]` : ""} "${name}"`;
        await btn.click().catch(() => {});
        await page.waitForTimeout(500);
        const modal = page.locator("div.fixed.inset-0 >> div.rounded-xl").last();
        if (!(await modal.isVisible().catch(() => false))) { ok.push(`${where}: opens an inline form/action (no dialog)`); continue; }
        const save = modal.locator("button").filter({ hasNotText: /^$/ }).last();
        const saveText = (await save.innerText().catch(() => "")).trim();
        if (!(await save.isEnabled().catch(() => true))) {
          ok.push(`${where}: "${saveText}" stays disabled until the form is filled`);
        } else {
          const before = serverErrors.length;
          await save.click().catch(() => {});
          await page.waitForTimeout(1200);
          const stillOpen = await modal.isVisible().catch(() => false);
          const invalid = await modal.locator("input:invalid, select:invalid, textarea:invalid").count().catch(() => 0);
          const message = (await page.locator("div.border-bad\\/30, .text-bad").allInnerTexts().catch(() => [])).filter((x) => x.trim()).join(" | ").slice(0, 140);
          if (serverErrors.length > before) gaps.push(`${where}: empty "${saveText}" caused a server error`);
          else if (stillOpen && (invalid > 0 || message)) ok.push(`${where}: empty "${saveText}" refused — ${invalid ? `${invalid} required field(s) flagged` : message}`);
          else if (stillOpen) gaps.push(`${where}: empty "${saveText}" did nothing visible (no message)`);
          else gaps.push(`${where}: empty "${saveText}" was ACCEPTED (dialog closed)`);
        }
        await page.keyboard.press("Escape");
        await page.waitForTimeout(300);
        if (await modal.isVisible().catch(() => false)) await page.locator("div.fixed.inset-0").first().click({ position: { x: 5, y: 5 } }).catch(() => {});
      }
    }
  }
  await browser.close();
  console.log(`REFUSED CORRECTLY (${ok.length})`); ok.forEach((x) => console.log("  ✓ " + x));
  console.log(`GAPS (${gaps.length + serverErrors.length})`); [...gaps, ...serverErrors].forEach((x) => console.log("  ✗ " + x));
})().catch((e) => { console.error(e); process.exit(1); });
