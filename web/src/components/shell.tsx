"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import {
  Activity, AlertTriangle, Bot, Box, Boxes, Building2, Cable, ClipboardCheck, CreditCard, Factory, FileBarChart, Gauge, LayoutDashboard, LayoutGrid, Leaf,
  LogOut, Map as MapIcon, Menu, Receipt, ScrollText, Settings2, ShieldCheck, Sparkles, SunMoon, Users, Wrench, Zap,
  Smartphone,
} from "lucide-react";
import { useAuth } from "@/lib/auth";
import { AlarmBell } from "./alarm-bell";
import { cx } from "./ui";

export type NavItem = { href: string; label: string; icon: React.ElementType; permission?: string; module?: string; soon?: string };

export const platformNav: NavItem[] = [
  { href: "/platform", label: "Overview", icon: LayoutDashboard },
  { href: "/platform/tenants", label: "Tenants", icon: Building2, permission: "platform.tenants.manage" },
  { href: "/platform/plans", label: "Plans & pricing", icon: CreditCard, permission: "platform.plans.manage" },
  { href: "/platform/billing", label: "Billing", icon: Receipt, permission: "platform.tenants.manage" },
  { href: "/platform/audit", label: "Audit log", icon: ScrollText, permission: "audit.read" },
  { href: "/platform/security", label: "Sign-in & security", icon: ShieldCheck },
];

export const appNav: NavItem[] = [
  { href: "/app", label: "Overview", icon: LayoutDashboard },
  { href: "/app/dashboards", label: "My dashboards", icon: LayoutGrid, permission: "plant.read" },
  { href: "/app/assistant", label: "AI assistant", icon: Sparkles, permission: "plant.read" },
  { href: "/app/plant", label: "Plant & assets", icon: Factory, permission: "plant.read", module: "plant" },
  { href: "/app/floor", label: "Floor plan", icon: MapIcon, permission: "plant.read", module: "plant" },
  { href: "/app/twin", label: "3D digital twin", icon: Box, permission: "plant.read", module: "plant" },
  { href: "/app/connectivity", label: "Connectivity", icon: Cable, permission: "plant.read", module: "plant" },
  { href: "/app/energy", label: "Energy", icon: Zap, permission: "energy.read", module: "energy" },
  { href: "/app/power", label: "Power quality", icon: Activity, permission: "energy.read", module: "power" },
  { href: "/app/carbon", label: "Carbon / ESG", icon: Leaf, permission: "energy.read", module: "carbon" },
  { href: "/app/production", label: "Production & OEE", icon: Gauge, permission: "production.read", module: "production" },
  { href: "/op", label: "Shop-floor app", icon: Smartphone, permission: "production.read", module: "production" },
  { href: "/app/robotics", label: "Robotics & AGV", icon: Bot, permission: "robotics.read", module: "robotics" },
  { href: "/app/maintenance", label: "Maintenance", icon: Wrench, permission: "maintenance.read", module: "maintenance" },
  { href: "/app/alarms", label: "Alarms", icon: AlertTriangle, permission: "alarms.read", module: "alarms" },
  { href: "/app/quality", label: "Quality / SPC", icon: ClipboardCheck, permission: "quality.read", module: "quality" },
  { href: "/app/inventory", label: "Inventory", icon: Boxes, permission: "production.read", module: "inventory" },
  { href: "/app/reports", label: "Reports", icon: FileBarChart, permission: "reports.read" },
  { href: "/app/users", label: "Users", icon: Users, permission: "users.read" },
  { href: "/app/roles", label: "Roles & permissions", icon: ShieldCheck, permission: "roles.manage" },
  { href: "/app/audit", label: "Audit log", icon: ScrollText, permission: "audit.read" },
  { href: "/app/settings", label: "Settings", icon: Settings2, permission: "plant.read" },
  { href: "/app/account", label: "Plan, billing & API", icon: CreditCard, permission: "plant.read" },
  { href: "/app/security", label: "Sign-in & security", icon: ShieldCheck },
];

function useTheme() {
  const [theme, setTheme] = useState<"light" | "dark" | null>(null);
  useEffect(() => {
    try {
      const saved = localStorage.getItem("factoryos.theme") as "light" | "dark" | null;
      if (saved) {
        document.documentElement.dataset.theme = saved;
        setTheme(saved);
      }
    } catch {
      /* storage blocked */
    }
  }, []);
  const toggle = () => {
    const dark = theme ? theme === "dark" : window.matchMedia("(prefers-color-scheme: dark)").matches;
    const next = dark ? "light" : "dark";
    document.documentElement.dataset.theme = next;
    setTheme(next);
    try {
      localStorage.setItem("factoryos.theme", next);
    } catch {
      /* storage blocked */
    }
  };
  return toggle;
}

/** Signed-in layout: sidebar filtered by the caller's permissions and plan modules. */
export function Shell({ nav, area, children }: { nav: NavItem[]; area: "platform" | "app"; children: React.ReactNode }) {
  const { me, loading, logout, can, hasModule } = useAuth();
  const router = useRouter();
  const path = usePathname();
  const [open, setOpen] = useState(false);
  const toggleTheme = useTheme();

  useEffect(() => {
    if (loading) return;
    if (!me) router.replace("/login");
    else if (area === "platform" && !me.isSuperAdmin) router.replace("/app");
    else if (area === "app" && me.isSuperAdmin) router.replace("/platform");
  }, [me, loading, area, router]);

  if (loading || !me) {
    return <div className="grid min-h-screen place-items-center text-sm text-muted">Loading Divakar FactoryOS…</div>;
  }

  const items = nav.filter((i) => (!i.permission || can(i.permission)) && (!i.module || hasModule(i.module)));

  return (
    <div className="min-h-screen lg:grid lg:grid-cols-[256px_1fr]">
      <aside
        className={cx(
          "fixed inset-y-0 left-0 z-40 w-64 border-r border-line bg-panel transition-transform lg:static lg:translate-x-0",
          open ? "translate-x-0" : "-translate-x-full",
        )}
      >
        <div className="flex h-14 items-center gap-2 border-b border-line px-4">
          <div className="grid h-8 w-8 place-items-center rounded-lg bg-brand text-sm font-bold text-white">D</div>
          <div className="leading-tight">
            <div className="text-sm font-semibold">Divakar FactoryOS</div>
            <div className="text-xs text-muted">{area === "platform" ? "Platform console" : me.tenantName}</div>
          </div>
        </div>
        <nav className="space-y-0.5 overflow-y-auto p-2" style={{ maxHeight: "calc(100vh - 7.5rem)" }}>
          {items.map((i) => {
            const active = i.href === path || (i.href !== "/app" && i.href !== "/platform" && path.startsWith(i.href));
            const Icon = i.icon;
            return (
              <Link
                key={i.href}
                href={i.href}
                onClick={() => setOpen(false)}
                className={cx(
                  "flex items-center gap-3 rounded-lg px-3 py-2 text-sm transition",
                  active ? "bg-brand/12 font-medium text-brand" : "text-ink/80 hover:bg-panel-2",
                )}
              >
                <Icon size={16} />
                <span className="flex-1">{i.label}</span>
                {i.soon && <span className="text-[10px] uppercase tracking-wide text-muted">{i.soon}</span>}
              </Link>
            );
          })}
        </nav>
        <div className="absolute bottom-0 w-full border-t border-line p-3 text-xs text-muted">
          <div className="truncate font-medium text-ink">{me.displayName}</div>
          <div className="truncate">{me.email}</div>
        </div>
      </aside>
      {open && <div className="fixed inset-0 z-30 bg-black/40 lg:hidden" onClick={() => setOpen(false)} />}

      <div className="min-w-0">
        <header className="sticky top-0 z-20 flex h-14 items-center gap-3 border-b border-line bg-panel/80 px-4 backdrop-blur">
          <button className="rounded p-1.5 hover:bg-panel-2 lg:hidden" onClick={() => setOpen(true)} aria-label="Menu">
            <Menu size={18} />
          </button>
          <div className="flex-1 text-sm text-muted">
            {area === "app" && me.planCode && (
              <span>
                Plan: <span className="font-medium text-ink">{me.planCode}</span>
              </span>
            )}
          </div>
          {area === "app" && hasModule("alarms") && can("alarms.read") && <AlarmBell />}
          <button onClick={toggleTheme} className="rounded p-1.5 hover:bg-panel-2" aria-label="Toggle theme">
            <SunMoon size={16} />
          </button>
          <button
            onClick={async () => {
              await logout();
              router.replace("/login");
            }}
            className="flex items-center gap-1.5 rounded px-2 py-1.5 text-sm hover:bg-panel-2"
          >
            <LogOut size={16} /> Sign out
          </button>
        </header>
        <main className="mx-auto max-w-7xl p-4 md:p-6">{children}</main>
      </div>
    </div>
  );
}
