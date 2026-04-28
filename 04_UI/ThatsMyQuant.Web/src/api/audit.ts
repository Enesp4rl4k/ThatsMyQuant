import { api } from "./client";

export type AuditSeverity = "info" | "success" | "warning" | "error";

export interface AuditEvent {
  id: number;
  utcTime: string;
  severity: AuditSeverity;
  source: string;
  title: string;
  body: string;
  processName: string;
  correlationId: string | null;
}

export interface AuditFilters {
  severity?: AuditSeverity;
  source?: string;
  from?: string;
  to?: string;
  limit?: number;
}

function buildQuery(f: AuditFilters): string {
  const params = new URLSearchParams();
  if (f.severity) params.set("severity", f.severity);
  if (f.source) params.set("source", f.source);
  if (f.from) params.set("from", f.from);
  if (f.to) params.set("to", f.to);
  if (f.limit) params.set("limit", String(f.limit));
  const s = params.toString();
  return s ? `?${s}` : "";
}

export const auditApi = {
  events: (filters: AuditFilters = {}) =>
    api.get<AuditEvent[]>(`/api/audit/events${buildQuery(filters)}`),
  sources: () => api.get<string[]>("/api/audit/sources"),
};
