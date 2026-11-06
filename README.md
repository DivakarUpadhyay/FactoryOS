# Divakar FactoryOS

A multi-tenant smart manufacturing platform: plant structure (ISA-95), machines, robots, meters and sensors
over 36 industrial protocols, and, in later phases, energy and power management, carbon/ESG, production and OEE,
robotics and AGV fleets, maintenance, alarms, quality/SPC and inventory.

**Stack:** SQL Server 2022 · .NET 10 REST API (Dapper, JWT, row-level security) · Next.js 15 (App Router, Tailwind 4)

## Run it

1. **API** (creates and migrates the `DivakarFactoryOS` database on start):
   ```
   cd src/Divakar.FactoryOS.Api
   dotnet user-secrets set "Jwt:SigningKey" "<at least 32 random characters>"   # already done on this PC
   dotnet run --launch-profile http                                             # http://localhost:5080
   ```
2. **Web**:
   ```
   cd web
   npm install
   npm run dev                                                                  # http://localhost:3000
   ```
3. Open http://localhost:3000/setup **on the same machine** and create the platform operator (works once).
4. In the platform console, create a tenant (a factory company) with its administrator, then sign in as that
   administrator to build the plant and add assets.

Settings can also come from environment variables prefixed `FACTORYOS_`, e.g. `FACTORYOS_ConnectionStrings__FactoryOS`.

Easiest: double-click `start-factoryos.bat`; it opens the API, the ingestion worker and the web app in three windows.

**Hosted demo.** https://factoryos-alpha.vercel.app runs on Vercel alone, with `NEXT_PUBLIC_DEMO=1` set: the site answers the API
itself from `web/demo-data/snapshot.json`, a recording of the demo factory as the read-only demo viewer sees it, with timestamps moved
to the present and a simulated live feed. It needs no backend, so it stays up when this PC is off. Only the demo login works there,
and changes are switched off. To refresh the snapshot, run the API, the worker and `npm run dev` for a few minutes, then from `web/`:
`node scripts/record-demo.js demo-data/snapshot.json http://localhost:3000` (needs Playwright and Chrome) and push.

To point the hosted site at the real API instead, remove `NEXT_PUBLIC_DEMO` on Vercel and double-click `start-factoryos-public.bat`:
it starts the API and the worker, opens a Cloudflare quick tunnel to the API (`winget install Cloudflare.cloudflared`), sets
`NEXT_PUBLIC_API_URL` on Vercel to the tunnel's new address and rebuilds the site. That works only while those windows stay open.

The **worker** (`src/Divakar.FactoryOS.Worker`) reads every connected asset and stores its readings; without it there is no live data.

## Tests

```
dotnet test        # 92 unit tests + 63 integration tests (real SQL Server, in-process MQTT broker and Modbus server, local webhook)
```

## What is built (all 9 phases)

| Phase | Modules |
|---|---|
| 1 | Tenants, plans, users, roles (row-level security), plant model, audit |
| 2 | Native drivers (OPC UA, Modbus TCP, MQTT, S7, MTConnect, HTTP push), simulator, ingestion |
| 3 | Live push (SignalR), floor plan with drag-to-arrange |
| 4 | Energy (TOU cost, peak demand, idle waste, kWh/part), power quality, carbon scope 1/2, settings |
| 5 | OEE with PackML, automatic downtime + reasons + Pareto, work orders, shift reports |
| 6 | Robot/AGV fleet map and health, transport missions, VDA 5050 orders over MQTT |
| 7 | Alarm rules, escalation, predictive anomalies, preventive/corrective/predictive maintenance, MTBF/MTTR, e-mail + webhook |
| 8 | SPC (X-bar-R, I-MR, Cpk, run rules), NCR/CAPA, inventory by lot, barcode/QR, lot traceability |
| 9 | Reports (CSV/PDF, daily e-mail), dashboard builder, AI assistant, 3D digital twin, billing, API keys, on-prem install (`deploy/`) |

**Full manual: [docs/GUIDE.md](docs/GUIDE.md)** · selling and onboarding: [docs/ONBOARDING-AND-MONETIZATION.md](docs/ONBOARDING-AND-MONETIZATION.md) · screenshots: `docs/test-screenshots/index.html`

© 2026 Divakar Upadhyay
