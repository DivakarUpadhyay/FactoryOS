# Divakar FactoryOS — complete guide

Smart-manufacturing platform for any factory: live machine data over industrial protocols, floor plan and 3D twin,
energy, power quality and carbon, production and OEE, robots and AGVs, alarms, maintenance, quality/SPC, inventory
and traceability, reports, dashboards, an AI assistant, billing and integration APIs.

**Stack:** SQL Server 2022 · .NET 10 REST API + worker (Dapper, row-level security per company) · Next.js 15 web app · SignalR live updates.

> Companion documents: [ONBOARDING-AND-MONETIZATION.md](ONBOARDING-AND-MONETIZATION.md) (who to sell to, pricing, site survey)
> · test screenshots in [test-screenshots/](test-screenshots/) (open `index.html`).

---

## 1. Start and stop (this PC)

| Action | How |
|---|---|
| Start everything | Double-click `start-factoryos.bat` (opens API, Worker and Web windows; the browser opens after ~20 s) |
| Stop | Close the three windows |
| Web address | http://localhost:3000 |
| API address | http://localhost:5080 (OpenAPI document at `/openapi/v1.json` in development) |
| Your sign-ins | `LOGINS.local.txt` in the project folder (platform operator + demo factory admin). Keep it private. |

The **API** serves the web app and runs live push (SignalR). The **Worker** reads machines and runs every background job:
downtime detection, AGV dispatch, alarm rules, anomaly detection, preventive maintenance, SPC sampling, notifications,
daily report e-mails and monthly invoices. Without the worker there is no live data and no alarms.

---

## 2. Roles in the system

| Who | Where they work | What they do |
|---|---|---|
| **Platform operator** (you) | `/platform` | Create customer companies (tenants), set plans and prices, issue invoices, record payments |
| **Factory admin** (customer) | `/app` | Build the plant, connect machines, invite users, configure rules, tariffs, shifts |
| Plant manager, supervisor, operator, technician, quality inspector, energy manager, viewer | `/app` | Built-in roles with sensible permissions; custom roles can be made under *Roles & permissions* |

Each company sees only its own data — enforced in the database itself (row-level security), not just in the screens.
Each company only sees the modules its plan includes (*Trial*, *Energy Starter*, *Factory Pro*, *Enterprise*).

---

## 3. Onboard a customer (platform operator)

1. Sign in as the operator → **Tenants → New tenant**: company name, code, plan, country, and the factory admin's name,
   e-mail and a temporary password (12+ characters, letters and digits). Share it securely.
2. Trial tenants expire after 30 days. Use **Activate / Mark paid / Suspend** as the commercial status changes.
3. **Plans & pricing**: change prices, limits (plants, assets, users) and which modules each plan includes.
4. **Billing**: invoices are issued automatically on the 1st of each month for *active* companies on priced plans
   (5 % business tax). *Issue invoices* runs it on demand (safe to repeat). Click an invoice to view/print it;
   *Mark paid* records the bank transfer or other payment reference. Enterprise (price on request) is not auto-invoiced.

---

## 4. Set up a factory (factory admin)

1. **Plant & assets** → build the tree: Site → Plant → Area → Line → Cell.
2. **Add assets** (33 types: CNC, moulding, press, SMT, reflow, robot arm, cobot, AGV, AMR, power meter, power analyser,
   transformer, UPS, solar, compressor, chiller, boiler, water/gas meter, sensors, cameras, RFID…). Give each one its
   protocol, endpoint, rated power (kW) and ideal cycle time (seconds — needed for OEE performance).
3. Open the asset:
   - **Tags** → *Apply template* (standard tags per asset type) or add tags: sensor type, unit, address, scale/offset, deadband, range.
   - **Connection** → protocol settings (passwords are stored and shown masked) → **Test connection** shows values at once.
   - **Device keys** → for devices that push data over HTTP.
4. **Connectivity** shows every asset's state, last data and errors live.
5. **Floor plan** → *Arrange* and drag machines to where they stand (positions are saved; the 3D twin uses them too).
6. **Settings**: time zone, currency, time-of-use tariff and contract capacity (kW), emission factors, main meter,
   shifts and breaks (used by OEE and shift reports), alarm e-mail addresses and webhook.

Quick demo: on an empty company's **Overview** click **Generate demo factory** — 44 simulated assets, 239 tags, AGV
stations, alarm rules, maintenance plans, products, SPC characteristics and stock.

### Address formats

| Protocol | Endpoint | Tag address |
|---|---|---|
| OPC UA | `opc.tcp://10.0.0.5:4840` | `ns=2;s=Line1.Temperature`, `ns=3;i=1005` |
| Modbus TCP | `10.0.0.20:502` (unit id in settings) | `hr:40001:float32`, `ir:30010:int16`, `coil:1`, `di:5`, `:swap` for low-word-first |
| Siemens S7 | `10.0.0.30` (rack/slot in settings) | `DB1.DBD0:real`, `DB1.DBW4:int`, `DB2.DBX0.1`, `MW10`, `I0.0` |
| MQTT | `mqtt://broker:1883` | `plant/line1/temp` or `plant/line1/state\|$.data.temp` (JSON path; `+`/`#` wildcards) |
| VDA 5050 (AGV/AMR) | `broker:1883`, vendor = manufacturer, serial no. = serial | state topic JSON paths, e.g. `uagv/v2/Hiwin/AGV-07/state\|$.batteryState.batteryCharge` |
| MTConnect | `http://agent:5000` | data item id |
| HTTP push | — | tag code; device posts to `/api/v1/ingest` with header `X-Device-Key` |
| Others (EtherNet/IP, MC, FINS, BACnet, DNP3, IEC 61850, LoRaWAN…) | via an OPC UA or MQTT gateway | the gateway's node / topic |

---

## 5. Using each module

| Module | What you see and do |
|---|---|
| **Overview** | Plants, assets, installed power, protocols; asset states |
| **My dashboards** | Build screens from 10 widgets (key figures, OEE rings, energy/output per hour, alarms, machine states, live value, AGV fleet, low stock, notes). Share with the company; others can copy. |
| **AI assistant** | Ask in English or Chinese: "What should I do first today?", "Why is the energy bill high?", "今天的稼動率?". Built-in analysis always works; free-form AI answers need an Anthropic key on the server (§7). |
| **Floor plan / 3D twin** | Machines coloured by live state with live kW; click to open. 3D: drag to orbit, scroll to zoom; running machines glow with power draw. |
| **Energy** | kWh, time-of-use cost, peak demand vs contract, idle waste, energy per part, per-hour profile, actionable insights |
| **Power quality** | Voltage, current, PF, THD, frequency at each analyser/meter with min/max/avg and limit grading |
| **Carbon / ESG** | Scope 1 (gas) and 2 (grid) CO₂e, solar avoided, per day, by area meter, per part; CSV export |
| **Production & OEE** | Plant and machine OEE (A × P × Q) with PackML states; downtime is detected automatically — operators pick the reason; Pareto; work orders that count from the machine counter; shift reports |
| **Robotics & AGV** | Live fleet map, battery, utilisation, robot joint temperature/torque health; transport missions dispatched to the nearest free vehicle with enough battery (sent as VDA 5050 orders to real vehicles) |
| **Alarms** | Rules: *sensor* *operator* *threshold* for *N seconds* on one asset / a type / all → Info/Warning/Critical, e-mail/webhook, optional work order. Auto-clear, acknowledge with a comment, escalation after 15 min. Predictive anomalies (vibration, bearing/joint temperature, motor current) and SPC quality alarms appear here too. Live pop-ups and the bell in the top bar. |
| **Maintenance** | Preventive plans by days, running hours or cycles (orders open automatically); corrective orders from alarms; predictive orders from anomalies; checklists must be completed; assignment; MTTR, MTBF, PM compliance; reliability per machine |
| **Quality / SPC** | X̄-R (subgroups) and I-MR (sensor-fed) charts with control and spec limits, Western Electric rules, Cp/Cpk/Pp/Ppk; enter subgroups by hand; non-conformances need disposition + root cause + corrective action to close |
| **Inventory** | Raw, WIP, finished, spare, consumable items by lot; receive, issue to work orders or maintenance, book production, adjust/scrap; low-stock flags; scan box (barcode, item code or lot); QR lot labels |
| **Traceability** | Inventory → *Traceability*: a lot's work order, machine and run window, input lots, where it was used, quality records, alarms and stops during the run — forwards and backwards |
| **Reports** | Production, energy, maintenance, quality, alarms — print/PDF or CSV (opens in Excel with Chinese text); daily e-mail of the key figures at a chosen hour |
| **Users / Roles / Audit** | Invite users, assign roles, custom permission sets; every change is in the audit log |
| **Plan, billing & API** | Plan usage vs limits, invoices, API keys for ERP/BI |

---

## 6. Integrations

**Read the data from another system (ERP, Power BI, MES)** — *Plan, billing & API → Create key*, then:

```
curl -H "X-Api-Key: fos_…" http://<server>:5080/api/v1/production/oee
```

Keys are read-only (GET only), cover every module's data of that one company, are stored hashed and can be revoked at any time.

**Receive alarms elsewhere (LINE, Teams, Slack, your system)** — *Settings → Alarm notifications → Webhook*: every alarm is
POSTed as JSON `{ subject, text, data, source }`. (LINE Notify was discontinued in 2025; use a LINE Messaging API bot or a
relay such as Make/Zapier/n8n behind the webhook.)

**Push data from devices** — create a device key on the asset and post:

```
POST /api/v1/ingest      X-Device-Key: fos_…
{ "readings": [ { "tag": "temp", "value": 23.5, "ts": "2026-10-16T08:00:00Z" } ] }
```

---

## 7. Server settings (secrets stay out of the database and out of chat)

Set them as **user secrets** in development, or **environment variables** (prefix `FACTORYOS_`, `__` between sections) on a server.

| Setting | Purpose | Example (development) |
|---|---|---|
| `ConnectionStrings:FactoryOS` | SQL Server database | in `appsettings.json` |
| `Jwt:SigningKey` | Signs sign-in tokens (48+ random characters) | `dotnet user-secrets set "Jwt:SigningKey" "…"` (already set on this PC) |
| `Smtp:Host`, `Smtp:Port`, `Smtp:User`, `Smtp:Password`, `Smtp:From` | Alarm and report e-mails. Without a host, e-mails are recorded as *Skipped* (never faked). | `dotnet user-secrets set "Smtp:Host" "smtp.office365.com"` — set for both the API and the Worker project |
| `Ai:AnthropicApiKey`, `Ai:Model` | Free-form AI answers in the assistant (default model `claude-sonnet-5-5`) | `dotnet user-secrets set "Ai:AnthropicApiKey" "…"` in the API project |
| `Cors:Origins:0` | Web address allowed to call the API | `http://factory-server:3000` |
| `Setup:AllowedNetworks` | Extra networks allowed to run first-time setup (e.g. Docker `172.16.0.0/12`); empty = only the server itself | — |
| `Telemetry:RetentionDays` | Raw readings kept (default 90) | — |

Run `dotnet user-secrets` commands inside `src\Divakar.FactoryOS.Api` (and `src\Divakar.FactoryOS.Worker` for SMTP).

---

## 8. Install on a factory server

**Windows server (recommended for factories):**

1. Install SQL Server 2022 (Express is fine for one plant), .NET 10 runtime (ASP.NET Core hosting bundle) and Node.js 20+.
2. On a build PC: `powershell -ExecutionPolicy Bypass -File deploy\publish.ps1 -ApiUrl http://<server>:5080` → `publish\api`, `publish\worker`, `publish\web`.
3. Copy the project (with `publish`) to the server and, as Administrator:
   `powershell -ExecutionPolicy Bypass -File deploy\install-windows-services.ps1 -ConnectionString "Server=.;Database=DivakarFactoryOS;Integrated Security=true;TrustServerCertificate=true" -JwtKey "<48+ random characters>" -WebOrigin http://<server>:3000`
   — installs and starts the **API** and **Worker** as Windows services with automatic restart.
4. Install the web app as a service with NSSM (the script prints the commands), or run `node server.js` in `publish\web`.
5. On the server itself open `http://localhost:3000/setup` and create the platform operator.

If Windows blocks the new `.exe` (SmartScreen/AppLocker), run `Unblock-File` on it or start it as `dotnet Divakar.FactoryOS.Api.dll`.

**Docker:** `deploy/docker/` has Dockerfiles and `docker-compose.yml` (SQL Server + API + worker + web). Copy `.env.example`
to `.env`, fill it in, `docker compose up -d --build`. *These Docker files were not run on the development PC (Docker is not
installed there) — test them on your server first.*

---

## 9. Troubleshooting

| Symptom | Fix |
|---|---|
| "localhost refused to connect" | The windows were closed or the PC restarted → run `start-factoryos.bat` |
| Page looks unstyled, buttons do nothing (URL ends with `?`) | The web build cache was overwritten. Close the Web window, delete `web\.next`, start again. (Production builds now go to `web\.next-prod`, so `deploy\publish.ps1` can no longer cause this.) |
| Assets show *Offline* | Worker not running, wrong endpoint/address, or firewall. *Asset → Connection → Test connection* shows the exact error. |
| No OEE performance | Set the asset's ideal cycle time |
| Energy cost looks wrong | Check *Settings → tariff, peak hours, main meter* |
| Alarm e-mails "Skipped" | SMTP not configured (§7) |
| "Your plan does not include …" | The company's plan lacks that module — change it under *Platform → Plans* or upgrade the tenant |
| Sign-in "Too many attempts" | 10 password attempts per minute per address; wait a minute |

---

## 10. Quality checks done

- **154 automated tests** pass: 92 unit tests (maths for OEE, SPC, energy, tariffs, alarms, fleet dispatch, VDA 5050,
  AI client) and 63 integration tests against a real SQL Server, an in-process MQTT broker and Modbus server, and a local webhook
  receiver (company isolation, plan gating, every module's API). Run them with `dotnet test`.
- Every phase was clicked through in Chrome with Playwright; screenshots are in `docs/test-screenshots/` (`phase3` … `phase9`, `final`).
- Final run in a visible Chrome (`docs/test-screenshots/final/`): all 27 pages load without browser errors; all 22 factory
  pages fit a phone screen (390 px) without sideways scrolling.

**Responsiveness (demo factory, 44 machines, this PC):**

| | Time |
|---|---|
| Pages on the production build (`deploy\publish.ps1`) | 70–450 ms for most pages; Energy and Production ≈ 0.95 s (they analyse 24 h of readings) |
| Live values (SignalR) | pushed every second |
| Plant tree, floor, connectivity, alarms, work orders, quality, inventory APIs | 5–40 ms median |
| Power quality, fleet, maintenance KPIs | 135–170 ms |
| Energy summary | 0.67 s (was 1.6 s; the idle-energy query was rewritten, 4× faster) |
| OEE, carbon, production report | 0.43–0.51 s |

The development server (`npm run dev`) is slower (2–7 s on first open) because it compiles each page on demand; use the
production build for real use. Raw numbers: `final/results.json` and `final/production-page-times.json`.

## 11. Honest limits

- **Payments** are recorded by hand; connecting ECPay/Stripe needs your merchant account.
- **E-mail** needs an SMTP server; **AI free-form answers** need an Anthropic API key (built-in answers work without).
- Protocols marked *Gateway* or *Planned* in the catalogue (e.g. Sparkplug B, ROS 2, UR RTDE, SECS/GEM) need a gateway today.
- The simulator drives the demo; real machines need their addresses and network access.
- Docker files are provided but were not run on the development PC.

© 2026 Divakar Upadhyay
