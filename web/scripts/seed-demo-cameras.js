// Points each demo machine's camera at its picture in web/public/demo-cameras (made by make-demo-cameras.py), signed in
// as the factory admin. The API fetches the snapshot from the web app, so run it with `npm run dev` on :3000 and the API on :5080.
// usage (from the repo root): node web/scripts/seed-demo-cameras.js LOGINS.local.txt
const fs = require("fs");
const path = require("path");
const API = "http://localhost:5080";
const WEB = "http://localhost:3000";
const password = fs.readFileSync(process.argv[2], "utf8").split("admin@divakar-demo.local")[1].match(/Password:\s*(\S+)/)[1];

(async () => {
  const login = await fetch(`${API}/api/v1/auth/login`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ email: "admin@divakar-demo.local", password }),
  });
  const { accessToken } = await login.json();
  const headers = { "Content-Type": "application/json", Authorization: `Bearer ${accessToken}` };
  const floor = await (await fetch(`${API}/api/v1/plant/floor`, { headers })).json();
  const pictures = new Set(fs.readdirSync(path.join(__dirname, "..", "public", "demo-cameras")).map((f) => f.replace(/\.jpg$/, "")));
  let n = 0;
  for (const a of floor.filter((x) => pictures.has(x.code))) {
    const r = await fetch(`${API}/api/v1/plant/assets/${a.assetId}/camera`, {
      method: "PUT", headers, body: JSON.stringify({ snapshotUrl: `${WEB}/demo-cameras/${a.code}.jpg`, username: null, password: null, snapOnAlarm: true }),
    });
    const snap = await fetch(`${API}/api/v1/plant/assets/${a.assetId}/camera/snapshot`, { headers });
    console.log(r.status, snap.status, snap.headers.get("content-type"), a.name);
    n += r.ok && snap.ok ? 1 : 0;
  }
  console.log(`${n} cameras connected`);
})().catch((e) => { console.error(e); process.exit(1); });
