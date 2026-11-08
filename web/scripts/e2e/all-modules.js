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
  "/app/carbon", "/app/enms", "/app/production", "/op", "/app/robotics", "/app/maintenance", "/app/alarms", "/app/ai", "/app/workflows", "/app/quality", "/app/inventory",
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
  await page.locator("td.font-mono", { hasText: "10.2" }).waitFor({ timeout: 30000 }).catch(() => {});
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

async function otFlows(page) {
  const tab = async (name) => {
    await page.locator("button.-mb-px", { hasText: name }).click();
    await page.waitForLoadState("networkidle").catch(() => {});
    await page.waitForTimeout(700);
  };
  where = "admin ISO 50001 meters & calibration (9.1)";
  await page.goto(`${SITE}/app/enms`, { waitUntil: "networkidle" });
  await tab("Meters & calibration");
  const meterRows = page.locator("tr", { hasText: "DV-" });
  check(await meterRows.count() >= 10, `meter register lists ${await meterRows.count()} meters with serial numbers`);
  check(await page.getByText("calibrated", { exact: true }).count() >= 1, "calibration state shown");
  await meterRows.first().click();
  await page.waitForTimeout(1000);
  check(await page.getByText(/CAL-20\d\d-/).count() >= 1, "calibration certificate shown for the selected meter");

  where = "admin ISO 50001 savings M&V";
  await tab("Savings M&V");
  check(await page.locator("tr", { hasText: "IPMVP" }).count() >= 3, "three M&V plans (A, B, C) listed");
  await page.locator("tr", { hasText: "IPMVP C" }).first().click();
  await page.waitForTimeout(1500);
  check(await page.getByText("Model CV(RMSE).").count() === 1 && await page.getByText(/ASHRAE 14/).count() >= 1, "option C shows ASHRAE Guideline 14 checks");
  await page.locator("tr", { hasText: "IPMVP A" }).first().click();
  await page.waitForTimeout(1200);
  check(await page.getByText("Documented savings.").count() === 1, "option A shows the documented savings");

  where = "admin ISO 50001 emission factors (regions, refrigerants)";
  await tab("Emission factors");
  check(await page.getByText("In use:").count() === 1 && await page.getByRole("button", { name: "Use UK" }).count() === 1, "grid region card with other regions offered");
  await page.getByLabel("Factor type").selectOption("refrigerant");
  await page.waitForTimeout(400);
  check(await page.getByText("R-410A blend").count() === 1 && await page.getByText("1924.0000 /kg").count() === 1, "R-410A listed with GWP 1,924");
  await page.getByLabel("Factor type").selectOption("scope3");
  await page.waitForTimeout(400);
  check(await page.getByText("Road freight, HGV average laden").count() === 1, "Scope 3 factors listed");

  where = "admin ISO 50001 GHG activities";
  await tab("GHG activities");
  await page.getByRole("button", { name: "Activity", exact: true }).click();
  const ref = page.locator("div.fixed select").first();
  const value = await ref.locator("option", { hasText: "R-410A blend" }).getAttribute("value");
  await ref.selectOption(value);
  await page.locator("div.fixed").getByLabel(/^Quantity/).fill("1");
  await page.locator("div.fixed").getByLabel(/^Reference/).fill("E2E-R410A");
  await modalSave(page);
  check(await page.getByText("E2E-R410A").count() === 1, "refrigerant top-up recorded (1 kg R-410A)");
  await page.locator("tr", { hasText: "E2E-R410A" }).getByRole("button", { name: "Delete" }).click();
  await page.waitForTimeout(800);
  check(await page.getByText("E2E-R410A").count() === 0, "activity deleted");

  where = "admin carbon (Scope 3, footprint)";
  await page.goto(`${SITE}/app/carbon`, { waitUntil: "networkidle" });
  await page.getByText(/Product carbon footprint/).waitFor({ timeout: 30000 }).catch(() => {});
  check(await page.getByText(/Scope 3 \(value chain\)/).count() === 1, "Scope 3 card shown");
  check(await page.getByText(/Product carbon footprint/).count() === 1, "product carbon footprint card shown");
  const fpRow = page.locator("tr", { has: page.getByRole("button", { name: /^Edit footprint data for / }) }).first();
  check(await fpRow.count() === 1, "footprint row has an edit button");
  if (await fpRow.count() === 1) {
    await fpRow.getByRole("button", { name: /^Edit footprint data for / }).click();
    const mass = (0.2 + Math.round(Math.random() * 100) / 1000).toFixed(3);
    await page.locator("div.fixed").getByLabel(/^Mass of one unit/).fill(mass);
    const factor = page.locator("div.fixed").getByLabel(/^Factor for /).first();
    const hasMaterial = await factor.count() === 1;
    if (hasMaterial) {
      await factor.fill("2.5");
      await page.locator("div.fixed").getByLabel(/^Source for /).first().fill("E2E supplier EPD 2025");
    }
    await modalSave(page);
    await page.waitForTimeout(1500);
    check(await page.getByText(/^Footprint data — /).count() === 0, "footprint data saved (dialog closed)");
    check(await fpRow.getByText(/kg\/t$/).count() === 1, "footprint per tonne shown");
    await fpRow.getByRole("button", { name: /^Edit footprint data for / }).click();
    check(await page.locator("div.fixed").getByLabel(/^Mass of one unit/).inputValue() === String(Number(mass)), `unit mass ${mass} kg reloaded from the server`);
    if (hasMaterial) check(await page.locator("div.fixed").getByLabel(/^Source for /).first().inputValue() === "E2E supplier EPD 2025", "material factor and source reloaded");
    if (hasMaterial) {
      // the test factor is not real data: clear it again so it never reaches a recorded demo
      await page.locator("div.fixed").getByLabel(/^Factor for /).first().fill("");
      await modalSave(page);
      await page.waitForTimeout(1200);
      check(await page.getByText(/^Footprint data — /).count() === 0, "material factor cleared again");
    } else {
      await page.locator("div.fixed").getByRole("button", { name: "Cancel" }).click();
    }
  }

  where = "admin workflows";
  await page.goto(`${SITE}/app/workflows`, { waitUntil: "networkidle" });
  const flows = page.locator('nav[aria-label="Workflows"] button');
  check(await flows.count() >= 12, `${await flows.count()} workflows listed`);
  await flows.filter({ hasText: "Alarm to repair" }).click();
  check(await page.locator("ol > li").count() === 6, "alarm-to-repair shows its 6 steps");
  await page.getByLabel("Search workflows").fill("calibration");
  check(await flows.count() === 1 && await flows.first().textContent().then((t) => t.includes("ISO 50001")), "search finds the ISO 50001 cycle");
  await page.locator("ol > li").getByRole("link", { name: /Meters & calibration/ }).click();
  await page.waitForURL(/\/app\/enms/);
  check(page.url().endsWith("/app/enms"), "step link opens the EnMS page");

  where = "admin OT AI insights";
  await page.goto(`${SITE}/app/ai`, { waitUntil: "networkidle" });
  await page.waitForTimeout(1000);
  check(await page.getByText("Open insights").count() === 1, "insights page with summary");

  where = "admin connectivity edge agents";
  await page.goto(`${SITE}/app/connectivity`, { waitUntil: "networkidle" });
  await page.waitForTimeout(1500);
  check(await page.getByText("Machining hall gateway").count() === 1, "edge agent listed");
  check(await page.locator("tr", { hasText: "Machining hall gateway" }).getByText("online").count() === 1, "edge agent online");
}

(async () => {
  const browser = await chromium.launch({ channel: "chrome", headless: process.env.HEADLESS === "1", slowMo: 40 });

  if (process.env.SKIP_OPERATOR !== "1") {
    const op = await session(browser, "upadhyaydivakar1991@gmail.com", pw("upadhyaydivakar1991@gmail.com"), "/platform");
    for (const p of PLATFORM) await visit(op.page, p, "operator");
    await op.ctx.close();
  }

  const admin = await session(browser, "admin@divakar-demo.local", pw("admin@divakar-demo.local"), "/app");
  for (const p of APP) await visit(admin.page, p, "admin");
  await enmsFlows(admin.page);
  await otFlows(admin.page);
  where = "admin /op → Main app";
  await admin.page.goto(`${SITE}/op`, { waitUntil: "networkidle" });
  await admin.page.getByText("Main app").click();
  await admin.page.waitForURL(/\/app$/, { timeout: 20000 }).then(() => check(true, "Main app button returns to /app"), () => check(false, "Main app button"));
  await admin.ctx.close();

  const viewer = await session(browser, "demo@divakar-demo.local", "FactoryDemo@2026", "/app");
  for (const p of ["/app", "/app/energy", "/app/carbon", "/app/enms", "/app/ai", "/app/connectivity"]) await visit(viewer.page, p, "viewer");
  where = "viewer edge agents read-only";
  check(await viewer.page.getByRole("button", { name: "Agent", exact: true }).count() === 0, "no edge agent buttons for a viewer");
  where = "viewer workflows and footprint";
  await viewer.page.goto(`${SITE}/app/workflows`, { waitUntil: "networkidle" });
  const vf = await viewer.page.locator('nav[aria-label="Workflows"] button').count();
  check(vf > 0 && await viewer.page.getByText("not in your role").count() > 0, `viewer sees ${vf} workflows with steps outside the role marked`);
  await viewer.page.goto(`${SITE}/app/carbon`, { waitUntil: "networkidle" });
  await viewer.page.waitForTimeout(1200);
  check(await viewer.page.getByRole("button", { name: /^Edit footprint data for / }).count() === 0, "no footprint edit button for a viewer");
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
