"use client";

import { useCallback, useEffect, useState } from "react";
import { api } from "@/lib/api";
import { AuditEntry, fmtDate } from "@/lib/types";
import { Badge, Button, Card, ErrorNote, PageHeader, Table, errorText } from "./ui";

/** The audit log, newest first, loaded 100 entries at a time. Shared by the platform and factory areas. */
export function AuditView({ sub }: { sub: string }) {
  const [rows, setRows] = useState<AuditEntry[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [more, setMore] = useState(true);

  const load = useCallback(async (before?: number) => {
    try {
      const page = await api<AuditEntry[]>(`/api/v1/audit?take=100${before ? `&before=${before}` : ""}`);
      setRows((r) => (before ? [...r, ...page] : page));
      setMore(page.length === 100);
    } catch (e) {
      setError(errorText(e));
    }
  }, []);
  useEffect(() => {
    load();
  }, [load]);

  const tone = (a: string) => (a.includes("failed") ? "bad" : a.endsWith("delete") ? "warn" : a.endsWith("create") ? "ok" : "muted");

  return (
    <>
      <PageHeader title="Audit log" sub={sub} />
      <ErrorNote error={error} />
      <Card>
        <Table head={["When", "Who", "Action", "Entity", "Details", "IP"]} empty={rows.length === 0}>
          {rows.map((r) => (
            <tr key={r.auditId} className="align-top">
              <td className="whitespace-nowrap px-3 py-2 text-muted">{fmtDate(r.at)}</td>
              <td className="px-3 py-2">{r.userEmail ?? "—"}</td>
              <td className="px-3 py-2"><Badge tone={tone(r.action)}>{r.action}</Badge></td>
              <td className="px-3 py-2">{r.entity}</td>
              <td className="max-w-md truncate px-3 py-2 font-mono text-xs text-muted" title={r.detail ?? ""}>{r.detail ?? ""}</td>
              <td className="px-3 py-2 text-xs text-muted">{r.ipAddress}</td>
            </tr>
          ))}
        </Table>
        {more && rows.length > 0 && (
          <div className="mt-3 text-center">
            <Button variant="outline" onClick={() => load(rows[rows.length - 1].auditId)}>Load older</Button>
          </div>
        )}
      </Card>
    </>
  );
}
