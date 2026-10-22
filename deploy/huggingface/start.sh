#!/bin/bash
# Starts the public demo: SQL Server Express, then the API (which creates the database), builds the demo factory
# and the read-only demo login, then serves the API on port 7860 and runs the worker.
# The database lives inside the container, so every restart begins with a fresh demo factory.
set -uo pipefail

rand() { head -c 96 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c "$1"; }
wait_for() { for _ in $(seq 1 "$2"); do eval "$1" >/dev/null 2>&1 && return 0; sleep 2; done; echo "timed out: $1"; return 1; }

export MSSQL_SA_PASSWORD="Sa$(rand 24)9!"
/opt/mssql/bin/sqlservr >/tmp/sqlserver.log 2>&1 &
SQLCMD=$(ls /opt/mssql-tools18/bin/sqlcmd /opt/mssql-tools/bin/sqlcmd 2>/dev/null | head -1)
wait_for "$SQLCMD -C -S localhost -U sa -P '$MSSQL_SA_PASSWORD' -Q 'SELECT 1'" 90 || { tail -50 /tmp/sqlserver.log; exit 1; }
echo "SQL Server is up"

export ASPNETCORE_ENVIRONMENT=Production
export FACTORYOS_ConnectionStrings__FactoryOS="Server=localhost;Database=DivakarFactoryOS;User Id=sa;Password=$MSSQL_SA_PASSWORD;TrustServerCertificate=true;Application Name=Divakar FactoryOS"
export FACTORYOS_Jwt__SigningKey="$(rand 64)"
export FACTORYOS_Secrets__Key="$(head -c 32 /dev/urandom | base64)"
export FACTORYOS_Cors__Origins__0="https://factoryos-alpha.vercel.app"
export FACTORYOS_Cors__Origins__1="http://localhost:3000"
export FACTORYOS_Security__WebBaseUrl="https://factoryos-alpha.vercel.app"
export FACTORYOS_Proxy__TrustForwardedHeaders=true
export FACTORYOS_RateLimit__AuthPerMinute=30

# 1. API on a private port, only to build the demo data
cd /app/api
ASPNETCORE_URLS=http://127.0.0.1:5080 ./Divakar.FactoryOS.Api &
API=$!
wait_for "curl -sf http://127.0.0.1:5080/health" 120 || exit 1

call() {   # call METHOD PATH TOKEN JSON
  curl -sS -X "$1" "http://127.0.0.1:5080/api/v1$2" -H "Content-Type: application/json" \
    ${3:+-H "Authorization: Bearer $3"} ${4:+-d "$4"}
}
login() { call POST /auth/login "" "{\"email\":\"$1\",\"password\":\"$2\"}" | jq -r .accessToken; }

OP_PW="Op$(rand 24)9!"
ADMIN_PW="Ad$(rand 24)9!"
call POST /auth/setup "" "{\"email\":\"operator@divakar-demo.local\",\"displayName\":\"Platform Operator\",\"password\":\"$OP_PW\"}"
OP=$(login operator@divakar-demo.local "$OP_PW")
call POST /platform/tenants "$OP" "{\"code\":\"divakar-demo\",\"name\":\"Divakar Demo Factory\",\"planCode\":\"enterprise\",\"adminEmail\":\"admin@divakar-demo.local\",\"adminName\":\"Plant Admin\",\"adminPassword\":\"$ADMIN_PW\",\"country\":\"TW\",\"timeZone\":\"Asia/Taipei\",\"currency\":\"TWD\"}"
ADMIN=$(login admin@divakar-demo.local "$ADMIN_PW")
call POST /plant/demo "$ADMIN" "" >/dev/null
DEMO_ID=$(call POST /users "$ADMIN" '{"email":"demo@divakar-demo.local","displayName":"Demo Viewer","password":"FactoryDemo@2026","roles":["viewer"]}' | jq -r .userId)
echo "demo factory ready, demo user $DEMO_ID"
kill "$API"; wait "$API" 2>/dev/null

# 2. the public API, with the demo account locked against password and two-step changes
export FACTORYOS_Demo__UserId="$DEMO_ID"
ASPNETCORE_URLS=http://0.0.0.0:7860 ./Divakar.FactoryOS.Api &
wait_for "curl -sf http://127.0.0.1:7860/health" 120 || exit 1
echo "API is serving on port 7860"

# 3. the worker reads the demo factory's simulated machines, sensors and robots
cd /app/worker && ./Divakar.FactoryOS.Worker &

wait -n
echo "a process stopped; exiting so the Space restarts"
exit 1
