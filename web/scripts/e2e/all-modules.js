// End-to-end check of every FactoryOS module in headed Chrome: each role signs in, opens every page and tab, and the
// ISO 50001 / carbon flows are exercised through the UI. Reports API errors, console errors, page crashes and error notes.
// usage: node e2e.js <LOGINS.local.txt> [site]
const { chromium } = require(process.env.PLAYWRIGHT_PATH);
const fs = require("fs");
const logins = fs.readFileSync(process.argv[2], "utf8");
const SITE = process.argv[3] || "http://localhost:3000";
const pw = (email) => logins.split(email)[1].match(/Password:\s*(\S+)/)[1];

const problems = [];
const passed = [];
let where = "";
const check = (ok, what) => (ok ? passed : problems).push(`${where}: ${what}`);

const APP = ["/app", "/app/dashboards", "/app/assistant", "/app/plant", "/app/floor", "/app/twin", "/app/connectivity", "/app/energy", "/app/power",
  "/app/carbon", "/app/enms", "/app/production", "/op", "/app/robotics", "/app/maintenance", "/app/alarms", "/app/quality", "/app/inventory",
  "/app/reports", "/app/users", "/app/roles", "/app/audit", "/app/settings", "/app/account", "/app/security"];
const PLATFORM = ["/platform", "/platform/tenants", "/platform/plans", "/platform/billing", "/platform/audit", "/platform/security"];

async function session(browser, email, password, home) {
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await ctx.newPage();
  page.on("response", (res) => {
    const u = new URL(res.url());
    if (u.pathname.startsWith("/api/") && res.status() >= 400 && !(u.pathname.endsWith("/auth/refresh") && res.status() === 401)) {
      problems.push(`${where}: API ${res.status()} ${res.request().method()} ${u.pathname}`);
    }
  });
  page.on("console", (m) => { if (m.type() === "error" && !/favicon|Failed to load resource/.test(m.text())) problems.push(`${where}: console ${m.text().slice(0, 160)}`); });
  page.on("pageerror", (e) => problems.push(`${where}: page crash ${e.message.slice(0, 160)}`));
  where = `login ${email}`;
  await page.goto(`${SITE}/login`);
  await page.fill('input[type="email"]', email);
  await page.fill('input[type="password"]', password);
  await page.locator('button[type="submit"]').first().click();
  await page.waitForURL(new RegExp(home), { timeout: 180000, waitUntil: "commit" });
  check(true, "signed in");
  return { ctx, page };
}

async function errorsShown(page) {
  const notes = await page.locator("div.border-bad\\/30").allInnerTexts().catch(() => []);
  const overlay = await page.getByText(/Unhandled Runtime Error|Application error/).count();
  return [...notes, ...(overlay ? ["Next.js error overlay"] : [])];
}

async function visit(page, path, role) {
  where = `${role} ${path}`;
  await page.goto(`${SITE}${path}`, { waitUntil: "networkidle", timeout: 120000 }).catch(() => {});
  await page.waitForTimeout(1200);
  const heading = await page.locator("h1").first().innerText().catch(() => "");
  const tabs = page.locator("button.-mb-px");
  const n = await tabs.count();
  for (let i = 0; i < n; i++) {
    const label = (await tabs.nth(i).innerText().catch(() => "")).trim();
    where = `${role} ${path} [${label}]`;
    await tabs.nth(i).click({ timeout: 5000 }).catch(() => {});
    await page.waitForLoadState("networkidle", { timeout: 20000 }).catch(() => {});
    await page.waitForTimeout(500);
    for (const e of await errorsShown(page)) problems.push(`${where}: error shown "${e.slice(0, 140)}"`);
  }
  where = `${role} ${path}`;
  for (const e of await errorsShown(page)) problems.push(`${where}: error shown "${e.slice(0, 140)}"`);
  check(heading.length > 0 || path === "/op", `loaded "${heading || "shop floor"}" (${n} tabs)`);
}

async function modalSave(page) {
  await page.locator("div.fixed").getByRole("button", { name: /^(Save|Fit baseline)$/ }).click();
  await page.waitForLoadState("networkidle").catch(() => {});
  await page.waitForTimeout(800);
}

async function enmsFlows(page) {
  const tab = async (name) => {
    await page.locator("button.-mb-px", { hasText: name }).click();
    await page.waitForLoadState("networkidle").catch(() => {});
    await page.waitForTimeout(600);
  };
  await page.goto(`${SITE}/app/enms`, { waitUntil: "networkidle" });

  where = "admin ISO 50001 overview";
  const readiness = await page.getByText("EnMS readiness").count();
  const clauses = await page.locator("td.font-mono").allInnerTexts();
  check(readiness === 1 && clauses.includes("5.2") && clauses.includes("10.2"), `readiness ring + ${clauses.length} clauses (4.1–10.2)`);

  where = "admin ISO 50001 documents (create → view → delete)";
  await tab("Documents");
  await page.getByRole("button", { name: "Document", exact: true }).click();
  await page.locator("div.fixed select").first().selectOption("Design");
  await page.locator("div.fixed").getByLabel("Title", { exact: true }).fill("E2E design review checklist");
  await page.locator("div.fixed textarea").fill("Energy performance is considered for every new machine.");
  await modalSave(page);
  const created = await page.getByRole("button", { name: "E2E design review checklist" }).count();
  check(created === 1, "document created and listed");
  await page.getByRole("button", { name: "E2E design review checklist" }).click();
  check(await page.getByText("Energy performance is considered for every new machine.").count() === 1, "document text opens");
  await page.keyboard.press("Escape");
  await tab("Overview");
  const design = await page.locator("tr", { hasText: "Design (energy performance" }).locator("svg.text-ok").waitFor({ timeout: 30000 }).then(() => 1, () => 0);
  check(design === 1, "clause 8.2 turns green once a design document exists");
  await tab("Documents");
  await page.locator("tr", { hasText: "E2E design review checklist" }).getByRole("button", { name: "Delete" }).click();
  await page.waitForTimeout(800);
  check(await page.getByRole("button", { name: "E2E design review checklist" }).count() === 0, "document deleted");

  where = "admin ISO 50001 energy data → carbon";
  await tab("Energy data");
  await page.getByRole("button", { name: "Entry", exact: true }).click();
  const today = new Date().toISOString().slice(0, 10);
  await page.locator("div.fixed select").first().selectOption("gasoline");
  await page.locator("div.fixed").getByLabel("From", { exact: true }).fill(today);
  await page.locator("div.fixed").getByLabel("To", { exact: true }).fill(today);
  await page.locator("div.fixed").getByLabel("Quantity", { exact: true }).fill("50");
  await page.locator("div.fixed").getByLabel("Bill / certificate no.", { exact: true }).fill("E2E-GASOLINE");
  await modalSave(page);
  check(await page.getByText("E2E-GASOLINE").count() === 1, "gasoline delivery recorded (unit filled from the factor: L)");
  await page.goto(`${SITE}/app/carbon`, { waitUntil: "networkidle" });
  await page.waitForTimeout(1000);
  const fuelRow = page.locator("tr", { hasText: "gasoline" });
  check(await fuelRow.count() >= 1, "carbon report lists the gasoline as Scope 1 fuel");
  check(await page.getByText("market-based").count() >= 1, "carbon shows market-based Scope 2");
  check(await page.getByText(/MOEA Energy Administration/).count() >= 1, "carbon names the grid factor source");
  await page.goto(`${SITE}/app/enms`, { waitUntil: "networkidle" });
  await tab("Energy data");
  await page.locator("tr", { hasText: "E2E-GASOLINE" }).getByRole("button", { name: "Delete" }).click();
  await page.waitForTimeout(800);
  check(await page.getByText("E2E-GASOLINE").count() === 0, "entry deleted");

  where = "admin ISO 50001 audits (rule: corrective action before closing)";
  await tab("Audits & reviews");
  await page.getByRole("button", { name: "Record", exact: true }).click();
  await page.locator("div.fixed").getByLabel("Title", { exact: true }).fill("E2E nonconformity");
  await page.locator("div.fixed").getByLabel("Closed on", { exact: true }).fill(today);
  await modalSave(page);
  const refused = (await errorsShown(page)).some((e) => e.includes("corrective action"));
  check(refused, "closing without a corrective action is refused with a clear message");
  problems.splice(0, problems.length, ...problems.filter((p) => !(p.includes("E2E") || p.includes("corrective action") || (p.includes("audits") && p.includes("API 400")))));
  await page.keyboard.press("Escape");

  where = "admin ISO 50001 EnPIs";
  await tab("EnPIs & baselines");
  const rows = page.locator("tr.cursor-pointer");
  const count = await rows.count();
  for (let i = 0; i < count; i++) {
    await rows.nth(i).click();
    await page.waitForLoadState("networkidle").catch(() => {});
    await page.waitForTimeout(600);
    const shown = await page.getByText("Expected (baseline, normalised)").waitFor({ timeout: 30000 }).then(() => true, () => false);
    const name = await rows.nth(i).locator("td").first().innerText();
    check(shown && await page.getByText(/CUSUM of savings/).count() === 1, `EnPI "${name}": actual vs expected + CUSUM shown`);
  }

  where = "admin ISO 50001 emission factors";
  await tab("Emission factors");
  check(await page.getByText("Taiwan grid electricity 2024").count() === 1 && await page.getByText("0.4740 /kWh").count() >= 1, "Taiwan 2024 grid factor 0.474 listed");
  check(await page.getByText(/2\.68\d\d \/L/).count() >= 1, "diesel factor ≈ 2.686 kg/L computed from kg/TJ × heat value");
}

(async () => {
  const browser = await chromium.launch({ channel: "chrome", headless: false, slowMo: 40 });

  const op = await session(browser, "upadhyaydivakar1991@gmail.com", pw("upadhyaydivakar1991@gmail.com"), "/platform");
  for (const p of PLATFORM) await visit(op.page, p, "operator");
  await op.ctx.close();

  const admin = await session(browser, "admin@divakar-demo.local", pw("admin@divakar-demo.local"), "/app");
  for (const p of APP) await visit(admin.page, p, "admin");
  await enmsFlows(admin.page);
  where = "admin /op → Main app";
  await admin.page.goto(`${SITE}/op`, { waitUntil: "networkidle" });
  await admin.page.getByText("Main app").click();
  await admin.page.waitForURL(/\/app$/, { timeout: 20000 }).then(() => check(true, "Main app button returns to /app"), () => check(false, "Main app button"));
  await admin.ctx.close();

  const viewer = await session(browser, "demo@divakar-demo.local", "FactoryDemo@2026", "/app");
  for (const p of ["/app", "/app/energy", "/app/carbon", "/app/enms"]) await visit(viewer.page, p, "viewer");
  where = "viewer ISO 50001 read-only";
  await viewer.page.goto(`${SITE}/app/enms`, { waitUntil: "networkidle" });
  await viewer.page.locator("button.-mb-px", { hasText: "Documents" }).click();
  await viewer.page.waitForTimeout(800);
  check(await viewer.page.getByRole("button", { name: "Document", exact: true }).count() === 0, "no add/edit buttons for a viewer");
  await viewer.ctx.close();

  await browser.close();
  console.log(`\nPASSED (${passed.length})`);
  passed.forEach((p) => console.log("  ✓ " + p));
  console.log(`\nPROBLEMS (${problems.length})`);
  problems.forEach((p) => console.log("  ✗ " + p));
})().catch((e) => { console.error("E2E crashed at", where, e.message); process.exit(1); });
