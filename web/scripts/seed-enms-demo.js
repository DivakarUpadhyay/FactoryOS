// Fills the demo factory's ISO 50001 records through the API, signed in as the factory admin.
// usage (API running on :5080): node web/scripts/seed-enms-demo.js LOGINS.local.txt
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
  // start clean so the script can be run again
  for (const [list, key, path] of [["/api/v1/enms/actions", "actionId", "actions"], ["/api/v1/enms/objectives", "objectiveId", "objectives"],
    ["/api/v1/enms/enpis", "enpiId", "enpis"], ["/api/v1/enms/seus", "seuId", "seus"], ["/api/v1/enms/documents", "documentId", "documents"],
    ["/api/v1/enms/findings", "findingId", "findings"], ["/api/v1/enms/entries", "entryId", "entries"]]) {
    for (const x of await call("GET", list)) await call("DELETE", `/api/v1/enms/${path}/${x[key]}`);
  }
  const floor = await call("GET", "/api/v1/plant/floor");
  const id = (code) => floor.find((a) => a.code === code).assetId;

  const docs = [
    ["Policy", "Energy policy", `Divakar Demo Factory commits to:
1. Continually improve energy performance and the energy management system (EnMS).
2. Make information and resources available to achieve energy objectives and targets.
3. Comply with applicable legal requirements and other requirements related to energy efficiency, energy use and consumption.
4. Support the procurement of energy-efficient products and services, and design for energy performance improvement.
This policy is communicated to all employees and contractors and reviewed every year at management review.`, "Plant manager", "Plant manager", "2026-10-20", "2027-10-20"],
    ["Scope", "EnMS scope and boundaries", "Whole Taoyuan site: machining hall, SMT hall, moulding area, utilities (compressed air, chilled water, steam boiler), warehouse and offices. Energy types: grid electricity, on-site solar, natural gas, diesel (forklifts, emergency generator), LPG. Excluded: none.", "Energy manager", "Plant manager", "2026-10-20", "2027-10-20"],
    ["Parties", "Interested parties and their requirements", "Taipower (contract capacity, power factor), MOEA Energy Administration (energy-saving obligations of large energy users), Ministry of Environment (GHG inventory under the Climate Change Response Act), customers (product carbon footprint requests), employees (comfort, safety), parent company (ESG reporting).", "Energy manager", null, null, "2027-04-30"],
    ["Team", "Energy management team", "Top management sponsor: Plant manager. Energy manager: Facilities lead (EnMS owner, EnPIs, reporting). Members: Maintenance lead (SEU operating criteria, compressed air), Production planner (shift and load planning), Purchasing (energy-efficient procurement), EHS officer (legal register, GHG inventory).", "Plant manager", "Plant manager", "2026-10-20", "2027-10-20"],
    ["Risk", "Energy risks and opportunities register", "Risks: peak-demand penalty above contract capacity; grid emission factor and carbon fee exposure; compressor failure. Opportunities: compressed-air leak programme, VSD on Air compressor 2, chiller set-point optimisation, idle shutdown of CNCs, more rooftop PV, T-REC purchase.", "Energy manager", null, null, "2027-04-30"],
    ["Legal", "Legal and other requirements register", "1. Energy Administration Act and energy-saving rules for large energy users (contract capacity ≥ 800 kW) — annual reporting and saving target.\n2. Climate Change Response Act (2023) — GHG inventory, registration and verification for designated emission sources.\n3. Ministry of Environment GHG emission factor announcement (2024-02-05) and the latest MOEA electricity emission factor — used in this system.\n4. Customer code of conduct (RBA) — energy and GHG disclosure.\nCompliance is evaluated every six months (9.1.2).", "EHS officer", "Plant manager", "2026-10-20", "2027-04-20"],
    ["DataPlan", "Energy data collection plan", "Main incomer and hall sub-meters: 1-minute kWh and demand via Modbus TCP. Machines: kWh and part counters via OPC UA / MQTT. Gas: main gas meter (Nm³) pulse. Diesel and LPG: delivery notes entered monthly under Energy data. Accuracy: revenue-grade main meter; sub-meters class 1, calibrated every 2 years. Data reviewed weekly by the energy manager.", "Energy manager", "Plant manager", "2026-10-20", "2027-10-20"],
    ["Competence", "Competence and awareness", "SEU operators trained on operating criteria (idle shutdown, compressor pressure set-point). Energy awareness in new-hire induction. Energy manager: ISO 50001 internal auditor course.", "HR", null, null, "2027-10-20"],
    ["Communication", "Communication plan", "Monthly energy dashboard on the shop-floor screens; quarterly EnPI results to all staff; suggestions box for energy ideas; external: annual ESG report.", "Energy manager", null, null, "2027-10-20"],
    ["Procurement", "Energy-efficient procurement criteria", "Motors IE3 or better; compressors with VSD where load varies; LED lighting only; energy use over the life cycle is part of the evaluation for any equipment above NT$ 500,000.", "Purchasing", "Plant manager", "2026-10-25", "2027-10-25"],
  ];
  for (const [kind, title, body, owner, approvedBy, approvedOn, reviewDue] of docs) {
    await call("POST", "/api/v1/enms/documents", { kind, title, body, owner, approvedBy, approvedOn, reviewDue });
  }

  const seuCnc = (await call("POST", "/api/v1/enms/seus", {
    name: "CNC machining", assetIds: ["demo-cnc-01", "demo-cnc-02", "demo-cnc-03", "demo-cnc-04"].map(id), relevantVariables: "parts made, spindle hours",
    operatingCriteria: "Spindle and coolant off after 10 min idle; warm-up max 15 min; chip conveyor interlocked with spindle.", owner: "Maintenance lead",
  })).seuId;
  await call("POST", "/api/v1/enms/seus", {
    name: "Compressed air", assetIds: ["demo-ac-01", "demo-ac-02"].map(id), relevantVariables: "air demand (parts made), leak rate",
    operatingCriteria: "Header pressure 6.5 bar; compressors off outside shifts; monthly ultrasonic leak survey.", owner: "Maintenance lead",
  });
  await call("POST", "/api/v1/enms/seus", {
    name: "Injection moulding", assetIds: ["demo-inj-01", "demo-inj-02", "demo-inj-03"].map(id), relevantVariables: "shots, barrel temperature, product mix",
    operatingCriteria: "Barrel heaters to standby after 20 min stop; insulated barrel jackets fitted.", owner: "Production planner",
  });
  await call("POST", "/api/v1/enms/seus", {
    name: "HVAC and chilled water", assetIds: ["demo-ch-01", "demo-hvac-01", "demo-hvac-02"].map(id), relevantVariables: "outdoor temperature (CDD), occupancy",
    operatingCriteria: "Chilled water 7 °C supply; SMT hall 23 ± 2 °C, machining hall 26 ± 2 °C; AHUs on schedule.", owner: "Facilities lead",
  });

  const site = await call("POST", "/api/v1/enms/enpis", { name: "Site electricity per part", kind: "Ratio", baselineFrom: "2026-10-24", baselineTo: "2026-10-31" });
  const cnc = await call("POST", "/api/v1/enms/enpis", { name: "CNC electricity vs parts made", seuId: seuCnc, kind: "Regression", baselineFrom: "2026-10-24", baselineTo: "2026-10-31" });
  const o1 = (await call("POST", "/api/v1/enms/objectives", { title: "Reduce site electricity per unit of production by 5 %", enpiId: site.enpiId, targetPct: 5, targetDate: "2027-06-30", owner: "Energy manager" })).objectiveId;
  const o2 = (await call("POST", "/api/v1/enms/objectives", { title: "Cut CNC energy per part by 8 %", enpiId: cnc.enpiId, targetPct: 8, targetDate: "2027-12-31", owner: "Maintenance lead" })).objectiveId;

  const actions = [
    [o1, "Compressed-air leak repair programme", "Maintenance lead", "2026-10-30", 18000, 25000, "Compressor kWh 2 weeks before/after, normalised to parts made (ISO 50015)", 15600, "Verified"],
    [o1, "VSD retrofit on Air compressor 2", "Maintenance lead", "2027-02-28", 42000, 380000, "Compressor kWh vs air demand regression", null, "InProgress"],
    [o2, "Automatic idle shutdown on CNCs", "Maintenance lead", "2026-11-15", 21000, 0, "CNC idle kWh from the Energy page before/after", null, "Done"],
    [o1, "LED high-bay lighting in machining hall", "Facilities lead", "2027-03-31", 35000, 650000, null, null, "Planned"],
    [o1, "Chilled-water supply set-point 7 → 9 °C in winter", "Facilities lead", "2026-12-15", 12000, 0, null, null, "Planned"],
  ];
  for (const [objectiveId, title, owner, dueDate, expectedKwhYear, investment, verification, verifiedKwhYear, status] of actions) {
    await call("POST", "/api/v1/enms/actions", { objectiveId, title, owner, dueDate, expectedKwhYear, investment, verification, verifiedKwhYear, status });
  }

  const entries = [
    ["diesel", "2026-10-01", "2026-10-31", 320, 9600, null, "CPC delivery 2026-10-05", "Forklifts"],
    ["diesel", "2026-10-15", "2026-10-15", 40, 1200, null, "Monthly generator test", "Emergency generator"],
    ["lpg", "2026-10-01", "2026-10-31", 180, 4100, null, "Gas cylinder invoice 10/2026", "Canteen kitchen"],
    ["electricity", "2026-10-01", "2026-10-31", 2400, 8900, null, "Taipower bill (warehouse, unmetered)", "Warehouse building"],
    ["green_power", "2026-10-01", "2026-10-31", 20000, 22000, "T-REC", "T-REC certificates 20 MWh", "Market-based Scope 2"],
  ];
  for (const [source, periodFrom, periodTo, quantity, cost, instrument, reference, note] of entries) {
    await call("POST", "/api/v1/enms/entries", { source, periodFrom, periodTo, quantity, cost, instrument, reference, note });
  }

  const findings = [
    ["Audit", "9.2", "Internal EnMS audit 2026 (clauses 4–10)", "Audited by the EHS officer (trained internal auditor). 2 minor nonconformities raised, 3 opportunities for improvement.", null, null, "EHS officer", "2026-10-28", null, null],
    ["Nonconformity", "7.1.5", "Machining hall sub-meter calibration certificate expired", "Certificate from 2024 not renewed.", "Calibration not in the PM schedule.", "Calibration added as a 2-year PM plan in Maintenance; meter recalibrated.", "Maintenance lead", "2026-10-28", "2026-11-15", "2026-11-04"],
    ["Nonconformity", "8.1", "Idle-shutdown criteria not posted at CNC 3 and 4", "Operators unaware of the 10-minute rule on the turning centres.", null, null, "Production planner", "2026-10-28", "2026-11-20", null],
    ["ManagementReview", "9.3", "Management review Q4 2026", "Inputs: EnPI results, objectives status, audit results, legal compliance, energy review. Decisions: approve VSD investment; set 2027 target 5 %; purchase 20 MWh T-REC per month.", null, null, "Plant manager", "2026-11-03", null, null],
  ];
  for (const [kind, clause, title, detail, rootCause, correctiveAction, owner, heldOn, dueDate, closedOn] of findings) {
    await call("POST", "/api/v1/enms/findings", { kind, clause, title, detail, rootCause, correctiveAction, owner, heldOn, dueDate, closedOn });
  }

  const o = await call("GET", "/api/v1/enms/overview");
  console.log("readiness", o.readinessPct, "%");
  for (const c of o.clauses) console.log(c.state.padEnd(8), c.clause.padEnd(8), c.evidence);
  for (const e of o.enpis) console.log("EnPI", e.name, "|", e.model, "| actual", e.actualKwh, "expected", e.expectedKwh, "impr", e.improvementPct, "%", e.notes.join(" "));
  const enpis = await call("GET", "/api/v1/enms/enpis");
  for (const e of enpis) console.log("baseline", e.name, e.baselineDays, "days", e.baselineKwh, "kWh", e.baselineParts, "parts R2", e.r2);
})().catch((e) => { console.error(e.message); process.exit(1); });
