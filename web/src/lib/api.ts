// Divakar FactoryOS API client.
// The access token lives in memory only; the refresh token is kept in localStorage so a reload stays signed in.
// A 401 triggers one refresh attempt, shared by all requests that fail at the same moment.

export const API_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5080";
const REFRESH_KEY = "factoryos.refresh";

export type Me = {
  userId: string;
  email: string;
  displayName: string;
  isSuperAdmin: boolean;
  tenantId: string | null;
  tenantName: string | null;
  planCode: string | null;
  modules: string[];
  permissions: string[];
};

type AuthResult = { accessToken: string; accessTokenExpiresAt: string; refreshToken: string; user: Me };

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

let accessToken: string | null = null;
let refreshing: Promise<boolean> | null = null;
let onSignedOut: (() => void) | null = null;

export function setSignedOutHandler(fn: () => void) {
  onSignedOut = fn;
}

function storedRefresh(): string | null {
  try {
    return localStorage.getItem(REFRESH_KEY);
  } catch {
    return null;
  }
}

function storeRefresh(token: string | null) {
  try {
    if (token) localStorage.setItem(REFRESH_KEY, token);
    else localStorage.removeItem(REFRESH_KEY);
  } catch {
    /* private mode: stays signed in until reload */
  }
}

function accept(result: AuthResult): Me {
  accessToken = result.accessToken;
  storeRefresh(result.refreshToken);
  return result.user;
}

async function readError(res: Response): Promise<ApiError> {
  let message = res.statusText || "Request failed";
  try {
    const body = await res.json();
    message = body.detail ?? body.title ?? message;
  } catch {
    /* not JSON */
  }
  if (res.status === 429) message = "Too many attempts. Wait a minute and try again.";
  return new ApiError(res.status, message);
}

async function refresh(): Promise<boolean> {
  const token = storedRefresh();
  if (!token) return false;
  refreshing ??= (async () => {
    try {
      const res = await fetch(`${API_URL}/api/v1/auth/refresh`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ refreshToken: token }),
      });
      if (res.status === 401) {
        // the server says the session is over; anything else (rate limit, outage) keeps the token for a retry
        storeRefresh(null);
        accessToken = null;
        return false;
      }
      if (!res.ok) return false;
      accept(await res.json());
      return true;
    } finally {
      setTimeout(() => (refreshing = null), 0);
    }
  })();
  return refreshing;
}

/** The current access token for the live hub (WebSockets cannot use the Authorization header). */
export async function hubToken(): Promise<string> {
  if (!accessToken) await refresh();
  return accessToken ?? "";
}

export async function api<T>(path: string, init: RequestInit & { json?: unknown } = {}): Promise<T> {
  const send = () =>
    fetch(`${API_URL}${path}`, {
      ...init,
      headers: {
        ...(init.json !== undefined ? { "Content-Type": "application/json" } : {}),
        ...(accessToken ? { Authorization: `Bearer ${accessToken}` } : {}),
        ...init.headers,
      },
      body: init.json !== undefined ? JSON.stringify(init.json) : init.body,
    });
  let res = await send();
  if (res.status === 401 && (await refresh())) res = await send();
  if (res.status === 401) {
    onSignedOut?.();
  }
  if (!res.ok) throw await readError(res);
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export const auth = {
  async login(email: string, password: string): Promise<Me> {
    const res = await fetch(`${API_URL}/api/v1/auth/login`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ email, password }),
    });
    if (!res.ok) throw await readError(res);
    return accept(await res.json());
  },

  /** Restores a session after a page reload. */
  async resume(): Promise<Me | null> {
    if (!(await refresh())) return null;
    try {
      return await api<Me>("/api/v1/auth/me");
    } catch {
      return null;
    }
  },

  async logout() {
    const token = storedRefresh();
    accessToken = null;
    storeRefresh(null);
    if (token) {
      await fetch(`${API_URL}/api/v1/auth/logout`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ refreshToken: token }),
      }).catch(() => undefined);
    }
  },
};
