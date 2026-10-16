"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { Building2, Factory, Users, Wallet } from "lucide-react";
import { api } from "@/lib/api";
import { Plan, TenantSummary } from "@/lib/types";
import { Badge, Card, ErrorNote, PageHeader, Stat, Table, errorText } from "@/components/ui";

const statusTone = { Active: "ok", Trial: "info", Suspended: "warn", Cancelled: "bad" } as const;

export default function PlatformOverview() {
  const [tenants, setTenants] = useState<TenantSummary[]>([]);
  const [plans, setPlans] = useState<Plan[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    Promise.all([api<TenantSummary[]>("/api/v1/platform/tenants"), api<Plan[]>("/api/v1/platform/plans")])
      .then(([t, p]) => {
        setTenants(t);
        setPlans(p);
      })
      .catch((e) => setError(errorText(e)));
  }, []);

  const price = Object.fromEntries(plans.map((p) => [p.planCode, p.priceMonthly]));
  const paying = tenants.filter((t) => t.status === "Active");
  const mrr = paying.reduce((sum, t) => sum + (price[t.planCode] ?? 0), 0);

  return (
    <>
      <PageHeader title="Platform overview" sub="All factories on Divakar FactoryOS" />
      <ErrorNote error={error} />
      <div className="mb-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <Stat label="Tenants" value={tenants.length} sub={`${paying.length} active · ${tenants.filter((t) => t.status === "Trial").length} on trial`} icon={<Building2 size={16} />} />
        <Stat label="Monthly recurring revenue" value={`NT$ ${mrr.toLocaleString()}`} sub="Active tenants at list price" icon={<Wallet size={16} />} />
        <Stat label="Users" value={tenants.reduce((s, t) => s + t.users, 0)} icon={<Users size={16} />} />
        <Stat label="Connected assets" value={tenants.reduce((s, t) => s + t.assets, 0)} sub={`${tenants.reduce((s, t) => s + t.plants, 0)} plants`} icon={<Factory size={16} />} />
      </div>
      <Card title="Newest tenants" actions={<Link href="/platform/tenants" className="text-sm text-brand">Manage tenants →</Link>}>
        <Table head={["Company", "Plan", "Status", "Users", "Assets", "Created"]} empty={tenants.length === 0}>
          {tenants.slice(0, 8).map((t) => (
            <tr key={t.tenantId}>
              <td className="px-3 py-2 font-medium">{t.name}</td>
              <td className="px-3 py-2">{t.planCode}</td>
              <td className="px-3 py-2"><Badge tone={statusTone[t.status]}>{t.status}</Badge></td>
              <td className="px-3 py-2 tabular-nums">{t.users}</td>
              <td className="px-3 py-2 tabular-nums">{t.assets}</td>
              <td className="px-3 py-2 text-muted">{new Date(t.createdAt + "Z").toLocaleDateString()}</td>
            </tr>
          ))}
        </Table>
      </Card>
    </>
  );
}
