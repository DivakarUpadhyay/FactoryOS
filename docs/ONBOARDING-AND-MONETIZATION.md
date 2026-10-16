# Divakar FactoryOS: who to onboard, how to earn, how to set up a factory

> Status as of 2026-10-16: Phases 1–2 are built (tenants, users, roles, plant model, 36-protocol catalogue,
> native drivers, simulator, live data, connectivity monitor). Energy dashboards, OEE, alarms, maintenance,
> quality and billing arrive in Phases 3–9. The tables below mark what a customer gets **today** (✅) and what
> needs a later phase (🔜), so you never promise something that is not built yet.

---

## 1. Which companies you can onboard

Any company that runs machines, meters or sensors and wants to see them live in one place. One company =
one **tenant**; each tenant can have several sites and plants (Site → Plant → Area → Line → Cell).

### 1.1 Manufacturing (core market)

| Industry | Typical Taiwan examples (types) | What they connect | What they pay for | Today |
|---|---|---|---|---|
| Electronics / SMT / PCB assembly | EMS contract makers, PCB, PCBA lines in Taoyuan, Hsinchu, Taichung | SMT lines, reflow ovens, AOI, pick-and-place, conveyors | Line status, output count, downtime, energy per board | ✅ live data · 🔜 OEE |
| Semiconductor back-end & equipment | Packaging/test houses, equipment makers, cleanroom suppliers | Tools via SECS/GEM, chillers, vacuum pumps, cleanroom sensors | Tool uptime, utilities energy, particle/humidity alarms | ✅ via OPC UA gateway · 🔜 SECS/GEM native |
| Machine tools / CNC job shops | Taichung machine-tool cluster, precision parts shops | CNC (FANUC, Mitsubishi, Siemens) via MTConnect / OPC UA, spindle load, part counters | Machine utilisation, cycle time, spindle hours | ✅ MTConnect, OPC UA, S7 |
| Plastics & injection moulding | Moulding shops, packaging makers | Moulding machines (Euromap 77/OPC UA), dryers, chillers, mould temperature | Cycle time, scrap, energy per shot | ✅ live data · 🔜 OEE, SPC |
| Metal, die casting, stamping, forging | Auto-parts suppliers, fastener makers (Kaohsiung, Tainan) | Presses, furnaces, compressors, power meters | Press stroke count, furnace energy, compressor efficiency | ✅ |
| Automotive & EV parts | Tier-1/Tier-2 suppliers, battery-pack and motor makers | Robots, welders, testers, AGVs | Traceability, robot uptime, energy | ✅ live data · 🔜 robotics fleet, traceability |
| Bicycle & sporting goods | Frame and component makers in central Taiwan | Welding robots, paint lines, ovens | Paint-line energy, robot status | ✅ |
| Food & beverage | Bakeries, beverage bottlers, frozen food, dairy | Cold rooms, ovens, fillers, boilers, temperature/humidity sensors | Cold-chain temperature alarms, energy, line speed | ✅ live data · 🔜 alarms |
| Pharmaceutical & medical devices | GMP plants, device assemblers | Cleanroom pressure, temperature, humidity, autoclaves | Environmental monitoring records, audit trail | ✅ audit log · 🔜 reports |
| Textile & garment | Functional-fabric mills, dyeing plants | Looms, dyeing machines, boilers, steam meters | Steam & power per batch, machine status | ✅ |
| Chemicals, coatings, paper | Specialty chemicals, paper mills | Tanks, pumps, flow/pressure/level sensors, boilers | Process values, utilities energy, carbon | ✅ live data · 🔜 carbon report |
| Toys, furniture, consumer goods | Toy and furniture factories | Moulding, CNC routers, spray booths, compressors | Machine utilisation, energy | ✅ |
| Packaging & printing | Corrugated, label and print shops | Printing presses, die-cutters, compressors | Output, downtime, energy | ✅ |

### 1.2 Energy-only customers (easiest first sale)

These do not need machine integration — only power meters, so setup takes a day or two.

| Customer type | What they connect | Why they buy |
|---|---|---|
| Any factory with a high electricity bill | Main incomer + sub-meters (Modbus TCP/RTU power meters) | See which line/machine uses the power, cut peak demand, avoid contract-capacity penalties |
| Cold storage & logistics warehouses | Compressors, cold rooms, temperature sensors, AGVs | Energy + temperature alarms |
| Commercial buildings, hotels, hospitals, schools | Chillers, AHUs, meters via BACnet/Modbus | Energy dashboards, ESG reporting |
| Solar / rooftop PV owners | Inverters (Modbus/SunSpec), irradiance sensors | Production vs. expected, inverter faults |
| Data centres / server rooms | UPS, PDUs, CRAC units, temperature | PUE, power alarms |

### 1.3 Partners who bring customers (channel)

| Partner | What they get | What you get |
|---|---|---|
| System integrators (SI) / PLC & SCADA contractors | White-label FactoryOS under their brand, wholesale price | Many factories through one contract |
| Machine builders (OEMs) | Remote monitoring for machines they sell (one tenant per end customer) | Recurring fee per machine sold |
| Power-meter / sensor distributors | A software bundle to sell with their hardware | Hardware-led leads |
| ESCOs (energy service companies) | A measurement platform to prove savings | Share of energy-saving contracts |
| Industrial parks & science parks | Park-wide energy dashboard for tenants | Many small factories at once |

### 1.4 Who is **not** a good fit (yet)

- Companies that need a full ERP (accounting, purchasing) — FactoryOS is shop-floor software; integrate with their ERP instead.
- Safety-critical control (sending commands to machines). FactoryOS **reads** data; it does not control machines.
- Plants that forbid any network connection from the shop floor — offer an on-prem install later (Phase 9).

---

## 2. How to make money

### 2.1 Subscription plans (already in the database, editable at **Platform → Plans & pricing**)

| Plan | Price / month | Plants | Assets | Users | Modules |
|---|---|---|---|---|---|
| Trial (30 days) | NT$0 | 1 | 25 | 5 | plant, energy, production |
| Energy Starter | NT$6,000 | 1 | 50 | 10 | plant, energy, power, carbon |
| Factory Pro | NT$18,000 | 3 | 300 | 50 | + production, maintenance, alarms, robotics |
| Enterprise | quoted | unlimited | unlimited | unlimited | everything + quality, inventory, EHS, AI, API |

Yearly billing at two months free (pay 10, get 12) improves cash flow.

### 2.2 Other income streams

| Stream | How it works | Typical size |
|---|---|---|
| **Set-up / onboarding fee** | Site survey, plant model, connecting machines, tag mapping, training | NT$30,000–300,000 per site, depending on machine count |
| **Extra assets** | Per asset above the plan limit | e.g. NT$100–200 per asset per month |
| **Hardware resale** | Edge gateways (OPC UA/MQTT), Modbus power meters, CT clamps, vibration and temperature sensors, installed | 20–35% margin on hardware |
| **Energy-savings share** | You find savings (peak shaving, compressed-air leaks, idle machines); the customer pays a share of what they save | 20–30% of verified savings for 1–3 years |
| **White-label licence** | System integrators sell it under their brand | Monthly fee per SI + per tenant |
| **On-premise licence** | For plants that cannot use the cloud | One-time licence + 18–22% yearly support |
| **Custom integration** | ERP/MES links, special protocols, custom reports | Hourly or fixed price |
| **Carbon / ESG reports** | Scope 1–2 reports for buyers and regulators (Taiwan carbon fee, EU CBAM exporters) | Add-on per report or per year |
| **Training & certification** | Courses for operators and SI engineers | Per seat |
| **Support SLA** | Faster response, named engineer | 10–20% of subscription |

### 2.3 A simple first-year path

1. **Month 1–2:** 3 free pilots (energy only) with factories you can visit. Prove one number each, e.g. "found NT$40,000/month of idle power".
2. **Month 3–6:** convert pilots to Energy Starter; ask each for one referral; publish the case studies.
3. **Month 6–12:** sign 1–2 system integrators for white-label; move good customers to Factory Pro when OEE and alarms ship.

Target example: 20 customers × NT$6,000 + 5 × NT$18,000 + set-up fees ≈ NT$2.6M+ per year of recurring revenue.

### 2.4 Where to find customers

- Taiwan industrial-park and science-park tenant directories, TAMI / machine-tool association member lists, trade shows (TIMTOS, Automation Taipei, Taipei AMPA, TPCA Show).
- Government smart-manufacturing and energy-saving subsidy programmes for SMEs — customers often get part of the cost covered; check the current programmes each year with MOEA / the Industrial Development Administration.
- LinkedIn: plant managers, facility/energy managers, factory digital-transformation leads.
- Power-meter and automation distributors (they meet factories every day).

---

## 3. How to get information from a factory (site survey)

Send this questionnaire before the visit, then walk the floor with the plant engineer.

### 3.1 Company and goals
- Products, shifts, working days, number of plants/lines.
- Top 3 problems: high power bill? unplanned downtime? unknown output? quality complaints? customer ESG requests?
- Who decides and who uses it (owner, plant manager, energy manager, IT).

### 3.2 Machines and equipment list (one row per machine)

| Field | Example |
|---|---|
| Name / code | CNC-01 |
| Type | 5-axis CNC, injection moulder, robot, compressor, chiller, power meter… |
| Make / model / year | FANUC 0i-F, 2019 |
| Controller / PLC | Siemens S7-1500, Mitsubishi Q, Omron NJ, FANUC, Allen-Bradley |
| Communication port | Ethernet, RS-485, none |
| Protocol it speaks | OPC UA, Modbus TCP/RTU, MTConnect, S7, MQTT, SECS/GEM, none |
| Rated power (kW) | 22 |
| Ideal cycle time | 45 s per part |
| Where it sits | Plant A → Area Machining → Line 2 |

Ask for existing documents: PLC tag lists, Modbus register maps, single-line electrical diagram, meter list.

### 3.3 Energy
- Last 12 months of Taipower bills (contract capacity, peak demand, time-of-use rates).
- Main incomer meter, sub-meters, CT locations; which loads are biggest (compressors, chillers, furnaces, ovens).

### 3.4 Network and IT
- Is the shop floor on its own network? Can a small gateway PC reach the internet (outbound HTTPS/MQTT only)?
- Firewall rules, IT security policy, any existing SCADA/MES/ERP to integrate with.
- Old machines with no port → plan add-on sensors (power CT clamp, vibration, stack-light reader).

### 3.5 Outcome of the survey
A one-page proposal: assets to connect, gateway/sensors needed, plan, set-up fee, timeline, and the one KPI the pilot will prove.

---

## 4. How to configure a factory in FactoryOS (step by step)

Start everything with `start-factoryos.bat` (API on :5080, worker, web on :3000).

### 4.1 Platform operator (you)
1. Sign in at http://localhost:3000/login as the platform operator.
2. **Platform → Tenants → New tenant**: company name, code (e.g. `acme-tw`), plan, country, and the factory administrator's name, email and a temporary password (12+ characters, letters and digits). Share it securely.
3. Trial tenants expire after 30 days; use **Mark paid / Activate / Suspend** as payments come in.

### 4.2 Factory administrator (the customer, or you during set-up)
1. Sign in with the tenant admin account.
2. **Plant** → build the tree: Site → Plant → Area → Line → Cell.
3. **Add assets** to lines/cells: choose the asset type (33 types: CNC, robot, AGV, power meter, compressor, chiller, sensor…), protocol, endpoint, rated power and ideal cycle time.
4. Open each asset:
   - **Tags** → *Apply template* (each asset type has a ready list) and adjust the addresses, or add tags by hand: sensor type, unit, address, scale/offset, deadband.
   - **Connection** → protocol settings (username/password are stored and shown masked) → **Test connection** shows live values straight away.
   - **Device keys** → for devices that push data over HTTP instead of being polled.
5. **Connectivity** page: every asset's online/offline state, last data and errors (refreshes every 5 s).
6. **Users / Roles**: invite plant managers, supervisors, operators, technicians, energy managers; 8 built-in roles or your own.
7. **Audit**: every sign-in and change is recorded.

Want to show a customer quickly? On an empty tenant's dashboard click **Generate demo factory** (44 simulated assets, 239 tags).

### 4.3 Address formats per protocol

| Protocol | Endpoint (asset) | Tag address examples |
|---|---|---|
| OPC UA | `opc.tcp://10.0.0.5:4840` | `ns=2;s=Line1.Temperature`, `ns=3;i=1005` |
| Modbus TCP | `10.0.0.20:502` (unit id in settings) | `hr:40001:float32`, `ir:30010:int16`, `coil:1`, `di:5`, add `:swap` for low-word-first devices |
| Siemens S7 | `10.0.0.30` (rack/slot in settings) | `DB1.DBD0:real`, `DB1.DBW4:int`, `DB2.DBX0.1`, `MW10`, `I0.0` |
| MQTT | `mqtt://broker:1883` | `factory/line1/temp` (number payload) or `factory/line1/state\|$.data.temp` (JSON path); `+` and `#` wildcards |
| MTConnect | `http://agent:5000` | data item id, e.g. `Xact`, `spindle_speed`, `execution` |
| HTTP push | — (device posts to `/api/v1/ingest` with its device key) | tag code |
| Simulator | — | built in; used by the demo factory |
| Others (EtherNet/IP, MC, FINS, BACnet, DNP3, IEC 61850, LoRaWAN…) | Through an OPC UA or MQTT gateway (e.g. Kepware, Node-RED, an IoT gateway) | gateway's node id / topic |

### 4.4 Go-live checklist
- [ ] All assets show **Online** on the Connectivity page; no red errors.
- [ ] Values match the machine's own screen (check 3 machines by eye).
- [ ] Rated power and ideal cycle time filled in (needed for energy and OEE).
- [ ] Customer users created with the right roles; temporary passwords changed.
- [ ] The pilot KPI agreed in writing, with a baseline number.

---

## 5. What is coming next (so you can sell the roadmap honestly)

| Phase | Adds | Sells to |
|---|---|---|
| 3 | Instant live updates (SignalR), floor-plan view of the factory | Everyone — the "wow" screen for demos |
| 4 | Energy & power-quality dashboards, peak-demand alerts, carbon (Scope 1–2) | Energy-only customers, exporters with ESG demands |
| 5 | Production / OEE, downtime reasons, shift reports, PackML | Manufacturers |
| 6 | Robot & AGV fleet (VDA 5050) | Automotive, electronics, logistics |
| 7 | Maintenance, rules engine, alarms, LINE/email notifications | Everyone |
| 8 | Quality / SPC, inventory, traceability | Pharma, food, automotive |
| 9 | Report builder, billing, AI assistant, 3D digital twin, on-prem | Enterprise, white-label partners |
