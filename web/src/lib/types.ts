// Shapes returned by the Divakar FactoryOS API (camelCase JSON).

export type Plan = {
  planCode: string;
  name: string;
  priceMonthly: number;
  currency: string;
  maxPlants: number;
  maxAssets: number;
  maxUsers: number;
  modules: string[];
  isActive: boolean;
  sortOrder: number;
};

export type TenantSummary = {
  tenantId: string;
  code: string;
  name: string;
  planCode: string;
  status: "Trial" | "Active" | "Suspended" | "Cancelled";
  country: string;
  trialEndsAt: string | null;
  createdAt: string;
  users: number;
  plants: number;
  assets: number;
};

export type UserSummary = {
  userId: string;
  email: string;
  displayName: string;
  isActive: boolean;
  lastLoginAt: string | null;
  createdAt: string;
  roles: string[];
};

export type Role = { roleId: string; code: string; name: string; isSystem: boolean; permissions: string[]; users: number };

export type AuditEntry = {
  auditId: number;
  tenantId: string | null;
  userId: string | null;
  userEmail: string | null;
  action: string;
  entity: string;
  entityId: string | null;
  detail: string | null;
  ipAddress: string | null;
  at: string;
};

export type PlantNode = {
  nodeId: string;
  parentId: string | null;
  level: "Site" | "Plant" | "Area" | "Line" | "Cell";
  code: string;
  name: string;
  description: string | null;
  isActive: boolean;
};

export type Asset = {
  assetId: string;
  nodeId: string;
  assetTypeCode: string;
  protocolCode: string | null;
  code: string;
  name: string;
  vendor: string | null;
  model: string | null;
  serialNo: string | null;
  endpoint: string | null;
  ratedPowerKw: number | null;
  idealCycleSec: number | null;
  status: "Offline" | "Idle" | "Running" | "Stopped" | "Fault" | "Maintenance";
  installedOn: string | null;
  layoutX?: number | null;
  layoutY?: number | null;
};

export type TreeNode = { node: PlantNode; children: TreeNode[]; assets: Asset[] };

export type AssetType = { assetTypeCode: string; category: string; name: string; icon: string };
export type Protocol = { protocolCode: string; family: string; name: string; transport: string; defaultPort: number | null; notes: string | null; supportLevel: "Native" | "Gateway" | "Push" | "Planned" };
export type Catalogue = { assetTypes: AssetType[]; protocols: Protocol[] };

export const MODULES = [
  "plant", "energy", "power", "carbon", "production", "maintenance", "alarms", "robotics", "quality", "inventory", "ehs", "ai", "api",
] as const;

export const LEVELS = ["Site", "Plant", "Area", "Line", "Cell"] as const;

export function childLevel(level: string): string | null {
  const i = LEVELS.indexOf(level as (typeof LEVELS)[number]);
  return i >= 0 && i < LEVELS.length - 1 ? LEVELS[i + 1] : null;
}

export function fmtDate(iso: string | null): string {
  return iso ? new Date(iso + (iso.endsWith("Z") ? "" : "Z")).toLocaleString() : "—";
}

// ---------------------------------------------------------------- Phase 2: connectivity

export type SensorType = { sensorTypeCode: string; category: string; name: string; unit: string; minValue: number | null; maxValue: number | null; simProfile: string };

export type Tag = {
  tagId: string; assetId: string; code: string; name: string; sensorTypeCode: string; unit: string; address: string | null;
  scale: number; offset: number; deadband: number; minValue: number | null; maxValue: number | null; isActive: boolean;
};

export type LiveValue = { tagId: string; code: string; name: string; sensorTypeCode: string; unit: string; ts: string | null; value: number | null; quality: number | null };
export type AssetLive = { assetId: string; status: Asset["status"]; lastSeenAt: string | null; lastError: string | null; values: LiveValue[] };
export type HistoryPoint = { ts: string; value: number | null; quality: number };

export type ConnectionRow = {
  assetId: string; code: string; name: string; assetTypeCode: string; protocolCode: string | null; supportLevel: string | null;
  endpoint: string | null; pollMs: number; status: Asset["status"]; lastSeenAt: string | null; lastError: string | null; tags: number;
};

export type TestResult = { connected: boolean; error: string | null; values: LiveValue[] };

export const QUALITY = ["Good", "Uncertain", "Bad", "Offline"] as const;

export const statusTone = { Running: "ok", Idle: "info", Stopped: "warn", Fault: "bad", Maintenance: "warn", Offline: "muted" } as const;

/** "12 s ago", "3 min ago" … for timestamps from the API (UTC without a zone). */
export function ago(iso: string | null): string {
  if (!iso) return "never";
  const s = Math.max(0, (Date.now() - new Date(iso + (iso.endsWith("Z") ? "" : "Z")).getTime()) / 1000);
  if (s < 60) return `${Math.round(s)} s ago`;
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  if (s < 86400) return `${Math.round(s / 3600)} h ago`;
  return `${Math.round(s / 86400)} d ago`;
}

export function fmtValue(v: number | null, unit: string): string {
  if (v === null || v === undefined) return "—";
  const abs = Math.abs(v);
  const n = Number.isInteger(v) || abs >= 1000 ? v.toLocaleString(undefined, { maximumFractionDigits: 0 }) : abs >= 100 ? v.toFixed(1) : v.toFixed(2);
  return unit ? `${n} ${unit}` : n;
}

/** What to put in a tag's address for each native protocol. */
export const ADDRESS_HELP: Record<string, string> = {
  opcua: "NodeId, e.g. ns=2;s=Line1.Temperature or ns=3;i=1005",
  "modbus-tcp": "Area:register:type, e.g. hr:40001:float32, ir:30010:int16, coil:1 (add :swap for low-word-first devices)",
  mqtt: "Topic, optionally with a JSON path: plant/line1/temp or factory/+/meter|$.power.kw",
  s7: "S7 address with type, e.g. DB1.DBD0:real, DB1.DBW4:int, DB1.DBX0.0, MW10",
  mtconnect: "The MTConnect dataItemId, e.g. Sspeed or execution",
  http: "Not needed: devices post {\"readings\":[{\"tag\":\"<code>\",\"value\":1.23}]} to /api/v1/ingest",
  simulator: "Not needed: the built-in simulator generates realistic values",
};

export const SETTINGS_HELP: Record<string, string> = {
  opcua: '{"username": "…", "password": "…", "acceptUntrusted": false}',
  "modbus-tcp": '{"unitId": 1}',
  mqtt: '{"username": "…", "password": "…", "tls": false, "clientId": "…"}',
  s7: '{"cpu": "S71500", "rack": 0, "slot": 1}',
  mtconnect: "{}",
};
