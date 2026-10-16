"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";
import { auth, Me, setSignedOutHandler } from "./api";

type AuthState = {
  me: Me | null;
  loading: boolean;
  login: (email: string, password: string) => Promise<Me>;
  logout: () => Promise<void>;
  can: (permission: string) => boolean;
  hasModule: (module: string) => boolean;
};

const AuthContext = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [me, setMe] = useState<Me | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    setSignedOutHandler(() => setMe(null));
    auth.resume().then((user) => {
      setMe(user);
      setLoading(false);
    });
  }, []);

  const login = useCallback(async (email: string, password: string) => {
    const user = await auth.login(email, password);
    setMe(user);
    return user;
  }, []);

  const logout = useCallback(async () => {
    await auth.logout();
    setMe(null);
  }, []);

  const value = useMemo<AuthState>(
    () => ({
      me,
      loading,
      login,
      logout,
      can: (p) => !!me?.permissions.includes(p),
      hasModule: (m) => !!me && (me.isSuperAdmin || me.modules.includes(m)),
    }),
    [me, loading, login, logout],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used inside AuthProvider");
  return ctx;
}
