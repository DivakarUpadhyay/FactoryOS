// Opens every page (and tab) at phone, tablet and desktop widths and reports horizontal page overflow and the elements causing it.
const { chromium } = require(process.env.PLAYWRIGHT_PATH);
const fs = require("fs");
const SITE = process.argv[3] || "http://localhost:3000";
const pw = fs.readFileSync(process.argv[2], "utf8").split("admin@divakar-demo.local")[1].match(/Password:\s*(\S+)/)[1];
const PAGES = ["/app", "/app/dashboards", "/app/assistant", "/app/plant", "/app/floor", "/app/twin", "/app/connectivity", "/app/energy", "/app/power",
  "/app/carbon", "/app/enms", "/app/production", "/op", "/app/robotics", "/app/maintenance", "/app/alarms", "/app/quality", "/app/inventory",
  "/app/reports", "/app/users", "/app/roles", "/app/audit", "/app/settings", "/app/account", "/app/security"];
const WIDTHS = [390, 768, 1366];
const shots = process.env.SHOTS;

(async () => {
  const browser = await chromium.launch({ channel: "chrome", headless: !!process.env.HEADLESS ? true : false });
  const ctx = await browser.newContext({ viewport: { width: 1366, height: 900 } });
  const page = await ctx.newPage();
  await page.goto(`${SITE}/login`);
  await page.fill('input[type="email"]', "admin@divakar-demo.local");
  await page.fill('input[type="password"]', pw);
  await page.locator('button[type="submit"]').first().click();
  for (let i = 0; i < 180 && !page.url().includes("/app"); i++) await page.waitForTimeout(1000);
  const asset = await page.evaluate(async () => location.origin);
  void asset;
  const issues = [];
  for (const w of WIDTHS) {
    await page.setViewportSize({ width: w, height: w < 500 ? 844 : 1000 });
    for (const p of PAGES) {
      await page.goto(`${SITE}${p}`, { waitUntil: "networkidle", timeout: 120000 }).catch(() => {});
      await page.waitForTimeout(700);
      const tabs = page.locator("button.-mb-px");
      const n = Math.max(1, await tabs.count());
      for (let i = 0; i < n; i++) {
        let label = "";
        if (await tabs.count()) {
          label = (await tabs.nth(i).innerText()).trim();
          await tabs.nth(i).scrollIntoViewIfNeeded().catch(() => {});
          await tabs.nth(i).click({ timeout: 4000 }).catch(() => {});
          await page.waitForLoadState("networkidle", { timeout: 15000 }).catch(() => {});
          await page.waitForTimeout(400);
        }
        const r = await page.evaluate(() => {
          const vw = document.documentElement.clientWidth;
          const over = document.documentElement.scrollWidth - vw;
          const culprits = [];
          if (over > 1) {
            for (const el of document.querySelectorAll("body *")) {
              const b = el.getBoundingClientRect();
              if (b.right > vw + 1 && b.width > 0) {
                // skip elements inside a scroll container (they scroll, the page does not)
                let p = el.parentElement, scrolls = false;
                while (p && p !== document.body) { const s = getComputedStyle(p); if (/(auto|scroll|hidden)/.test(s.overflowX)) { scrolls = true; break; } p = p.parentElement; }
                if (!scrolls) culprits.push(`${el.tagName.toLowerCase()}.${String(el.className).split(" ").slice(0, 4).join(".")} (${Math.round(b.right - vw)}px)`);
              }
            }
          }
          return { over, culprits: culprits.slice(0, 4) };
        });
        if (r.over > 1) issues.push(`${w}px ${p}${label ? ` [${label}]` : ""}: page overflows by ${r.over}px → ${r.culprits.join(" | ")}`);
        if (shots && w === 390 && i === 0) await page.screenshot({ path: `${shots}/m${p.replace(/\//g, "_")}.png` });
      }
    }
    console.log(`${w}px done`);
  }
  await browser.close();
  console.log(issues.length ? issues.join("\n") : "No horizontal overflow on any page or tab at 390, 768 and 1366 px.");
})().catch((e) => { console.error(e); process.exit(1); });
