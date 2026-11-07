// Fills the demo factory's meter register and calibrations, M&V plans, GHG activities, material factors and product masses
// through the API, signed in as the factory admin. Run after seed-enms-demo.js (it uses that script's EnPIs and actions).
// usage (API running on :5080): node web/scripts/seed-otcore-demo.js LOGINS.local.txt
const fs = require("fs");
const API = "http://localhost:5080";
const logins = fs.readFileSync(process.argv[2], "utf8");
const password = logins.split("admin@divakar-demo.local")[1].match(/Password:\s*(\S+)/)[1];

let token;
async function call(method, path, body) {
  const r = await fetch(`${API}${path}`, {
    method, headers: { "Content-Type": "application/json", ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await r.text();
  if (!r.ok) throw new Error(`${method} ${path} -> ${r.status} ${text}`);
  return text ? JSON.parse(text) : null;
}

(async () => {
  token = (await call("POST", "/api/v1/auth/login", { email: "admin@divakar-demo.local", password })).accessToken;
  for (const p of await call("GET", "/api/v1/enms/mv")) await call("DELETE", `/api/v1/enms/mv/${p.planId}`);
  for (const a of await call("GET", "/api/v1/enms/activities")) await call("DELETE", `/api/v1/enms/activities/${a.activityId}`);
  for (const m of await call("GET", "/api/v1/enms/meters")) await call("DELETE", `/api/v1/enms/meters/${m.meter.meterId}`);

  // ---- meter register (9.1) ----
  const { added } = await call("POST", "/api/v1/enms/meters/register-connected");
  const meters = await call("GET", "/api/v1/enms/meters");
  let serial = 41870;
  for (const { meter: m } of meters) {
    const main = m.kind === "Main";
    const elec = m.measures === "electricity";
    const body = {
      name: m.name, assetId: m.assetId, measures: m.measures, kind: m.kind, serialNo: `DV-${serial++}`,
      make: elec ? (main ? "Schneider PowerLogic PM8000" : "Schneider iEM3255") : m.measures === "gas" ? "Elster turbine meter" : "Endress+Hauser Promag",
      accuracyClass: elec ? (main ? "IEC 62053-22 class 0.2S" : "IEC 62053-21 class 1") : "±1.0 % of reading",
      uncertaintyPct: elec ? (main ? 0.2 : 1) : 1, intervalMonths: elec ? 24 : 60, location: m.assetName, isActive: true,
    };
    await call("PUT", `/api/v1/enms/meters/${m.meterId}`, body);
  }
  // calibration certificates: main meters this year, sub-meters on a rolling plan (one due this month), utility meters older
  let i = 0;
  for (const { meter: m } of meters) {
    const elec = m.measures === "electricity";
    const on = m.kind === "Main" ? "2026-03-18" : elec ? (i % 5 === 0 ? "2024-11-26" : i % 2 ? "2025-09-12" : "2026-02-04") : "2023-06-20";
    await call("POST", `/api/v1/enms/meters/${m.meterId}/calibrations`, {
      calibratedOn: on, result: i % 7 === 3 ? "Adjusted" : "Pass", asFoundErrorPct: i % 7 === 3 ? 1.4 : 0.1 + (i % 4) * 0.1, asLeftErrorPct: 0.1,
      lab: m.kind === "Main" ? "TAF-accredited calibration laboratory (ISO/IEC 17025)" : "In-house check against reference meter RM-01", certificateNo: `CAL-${on.slice(0, 4)}-${100 + i}`,
    });
    i++;
  }
  console.log(`meters: ${meters.length} (${added} added from connected assets)`);

  // ---- M&V plans ----
  const enpis = await call("GET", "/api/v1/enms/enpis");
  const actions = await call("GET", "/api/v1/enms/actions");
  const site = enpis.find((e) => !e.seuId);
  const cnc = enpis.find((e) => e.seuId);
  const action = (re) => actions.find((a) => re.test(a.title))?.actionId ?? null;
  if (site) {
    await call("POST", "/api/v1/enms/mv", {
      name: "Whole site — first reporting week", enpiId: site.enpiId, mvOption: "C", boundary: "Main incomer meters", reportingFrom: "2026-11-01", reportingTo: "2026-11-07",
      nonRoutineKwh: 0, staticFactors: "Two shifts, six days a week, same product mix as the baseline", confidencePct: 90,
    });
  }
  if (cnc) {
    await call("POST", "/api/v1/enms/mv", {
      name: "CNC cell — idle shutdown", actionId: action(/idle|CNC/i), enpiId: cnc.enpiId, mvOption: "B", boundary: "CNC machining cell feeders", reportingFrom: "2026-11-01",
      reportingTo: "2026-11-07", nonRoutineKwh: 0, staticFactors: "Same programs and spindle loads as the baseline", confidencePct: 90,
    });
  }
  await call("POST", "/api/v1/enms/mv", {
    name: "Compressed-air leak repair — key parameter", actionId: action(/leak|air/i), mvOption: "A", boundary: "Compressor room, air flow meter at the header",
    reportingFrom: "2026-10-01", reportingTo: "2026-10-31", nonRoutineKwh: 0, confidencePct: 90, manualSavingsKwh: 2150, manualUncertaintyPct: 12,
    staticFactors: "Leak flow measured before/after with ultrasonic survey; compressor specific power 0.11 kWh/Nm³ stipulated",
  });

  // ---- GHG activities (refrigerants, Scope 3) ----
  const factors = await call("GET", "/api/v1/enms/factors");
  const f = (source) => factors.find((x) => x.source === source).factorId;
  const acts = [
    ["ref_r410a", "2026-10-28", "2026-10-28", 3.2, "Chiller CH-2 service log 2026-118 (top-up after leak repair)"],
    ["ref_r134a", "2026-10-09", "2026-10-09", 1.5, "Air dryer AD-1 service report"],
    ["s3_steel", "2026-10-01", "2026-10-31", 12.4, "Steel bar deliveries, Oct (supplier invoices)"],
    ["s3_plastics", "2026-10-01", "2026-10-31", 2.6, "ABS resin deliveries, Oct"],
    ["s3_freight_hgv", "2026-10-01", "2026-10-31", 18400, "Inbound road freight, Oct (tonne-km from carrier)"],
    ["s3_freight_ship", "2026-10-01", "2026-10-31", 96000, "Export container, Keelung → Rotterdam"],
    ["s3_waste_landfill", "2026-10-01", "2026-10-31", 1.2, "General waste transfer notes, Oct"],
    ["s3_travel_air_long", "2026-10-12", "2026-10-16", 4300, "Customer audit trip, 2 people"],
    ["s3_commute_car", "2026-10-01", "2026-10-31", 21000, "Commuting survey, Oct"],
    ["s3_water_supply", "2026-10-01", "2026-10-31", 860, "Water bill, Oct"],
  ];
  for (const [source, periodFrom, periodTo, quantity, reference] of acts) {
    await call("POST", "/api/v1/enms/activities", { factorId: f(source), periodFrom, periodTo, quantity, reference });
  }
  console.log(`activities: ${acts.length}`);

  // ---- product footprint inputs ----
  const items = await call("GET", "/api/v1/inventory/items");
  const item = (code) => items.find((x) => x.code === code);
  if (item("steel-bar-40")) {
    // Ø40 mm bar: π × 0.02² m² × 7,850 kg/m³ = 9.865 kg per metre × 2.8639 kg CO2e/kg
    await call("PUT", `/api/v1/enms/items/${item("steel-bar-40").itemId}/co2e`, {
      kgCo2ePerUnit: 28.252, source: "DESNZ 2025 steel, primary production 2,863.9 kg CO2e/t × 9.865 kg/m (Ø40 mm bar, 7,850 kg/m³)",
    });
  }
  if (item("abs-resin")) {
    await call("PUT", `/api/v1/enms/items/${item("abs-resin").itemId}/co2e`, {
      kgCo2ePerUnit: 3.1725, source: "DESNZ 2025 average plastics, primary production (proxy until the supplier's EPD for ABS arrives)",
    });
  }
  const products = await call("GET", "/api/v1/production/products");
  for (const [code, massKg] of [["brkt-01", 0.42], ["cap-01", 0.018]]) {
    const p = products.find((x) => x.code === code);
    if (p) await call("PUT", `/api/v1/enms/products/${p.productId}/mass`, { massKg });
  }
  console.log("item factors and product masses set");
})().catch((e) => { console.error(e); process.exit(1); });
