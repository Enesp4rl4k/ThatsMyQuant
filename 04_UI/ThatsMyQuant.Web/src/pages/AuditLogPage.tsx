import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { auditApi, type AuditEvent, type AuditSeverity, type AuditFilters } from "@/api/audit";
import { cn } from "@/lib/utils";
import { formatDateTime } from "@/lib/format";
import { SkeletonTableRows } from "@/components/ui/Skeleton";

const SEVERITY_TONE: Record<AuditSeverity, string> = {
  error: "bg-rose-500/10 text-rose-400 border-rose-500/30",
  warning: "bg-amber-500/10 text-amber-400 border-amber-500/30",
  success: "bg-emerald-500/10 text-emerald-400 border-emerald-500/30",
  info: "bg-accent/10 text-accent border-accent/30",
};

const SEVERITY_DOT: Record<AuditSeverity, string> = {
  error: "bg-rose-400",
  warning: "bg-amber-400",
  success: "bg-emerald-400",
  info: "bg-accent",
};

export function AuditLogPage() {
  const [severity, setSeverity] = useState<AuditSeverity | "">("");
  const [source, setSource] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [limit, setLimit] = useState(200);

  const filters: AuditFilters = useMemo(
    () => ({
      severity: severity || undefined,
      source: source || undefined,
      from: from || undefined,
      to: to || undefined,
      limit,
    }),
    [severity, source, from, to, limit]
  );

  const sourcesQ = useQuery({
    queryKey: ["audit", "sources"],
    queryFn: auditApi.sources,
    staleTime: 60_000,
  });

  const eventsQ = useQuery({
    queryKey: ["audit", "events", filters],
    queryFn: () => auditApi.events(filters),
    refetchInterval: 10_000,
  });

  const events = eventsQ.data ?? [];

  const counts = useMemo(() => {
    const c = { error: 0, warning: 0, success: 0, info: 0 };
    for (const e of events) c[e.severity]++;
    return c;
  }, [events]);

  const reset = () => {
    setSeverity("");
    setSource("");
    setFrom("");
    setTo("");
    setLimit(200);
  };

  return (
    <div className="space-y-8">
      <div className="flex items-start justify-between">
        <div className="relative">
          <div className="absolute -left-4 top-0 bottom-0 w-1 bg-accent shadow-[0_0_15px_rgba(0,191,255,0.5)]" />
          <h2 className="text-[10px] font-black uppercase tracking-[0.4em] text-accent mb-1 ml-2">
            Operator Audit Trail
          </h2>
          <p className="text-2xl font-black tracking-tight ml-2">
            Audit Log{" "}
            <span className="text-muted-foreground/30 font-light">
              — {events.length} events
            </span>
          </p>
        </div>
        <div className="flex gap-2">
          {(["error", "warning", "success", "info"] as AuditSeverity[]).map((sev) => (
            <button
              key={sev}
              onClick={() => setSeverity(severity === sev ? "" : sev)}
              className={cn(
                "px-3 py-1.5 rounded-md border text-[10px] font-black uppercase tracking-widest transition-colors",
                severity === sev ? SEVERITY_TONE[sev] : "border-white/10 text-muted-foreground/60 hover:bg-white/[0.04]"
              )}
            >
              <span className="font-mono mr-1">{counts[sev]}</span>
              {sev}
            </button>
          ))}
        </div>
      </div>

      <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4 flex flex-wrap items-end gap-4">
        <Field label="Source">
          <select
            value={source}
            onChange={(e) => setSource(e.target.value)}
            className="bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono w-44"
          >
            <option value="">All sources</option>
            {(sourcesQ.data ?? []).map((s) => (
              <option key={s} value={s}>{s}</option>
            ))}
          </select>
        </Field>
        <Field label="From">
          <input
            type="datetime-local"
            value={from}
            onChange={(e) => setFrom(e.target.value)}
            className="bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
          />
        </Field>
        <Field label="To">
          <input
            type="datetime-local"
            value={to}
            onChange={(e) => setTo(e.target.value)}
            className="bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
          />
        </Field>
        <Field label="Limit">
          <input
            type="number"
            min={50}
            max={1000}
            step={50}
            value={limit}
            onChange={(e) => setLimit(parseInt(e.target.value, 10) || 200)}
            className="bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono w-20 text-right"
          />
        </Field>
        <button
          onClick={reset}
          className="px-3 py-1.5 rounded-md text-[10px] font-black uppercase tracking-widest text-muted-foreground hover:text-foreground hover:bg-white/[0.05] transition-colors"
        >
          Reset
        </button>
      </div>

      <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
        {eventsQ.isLoading ? (
          <table className="w-full text-sm">
            <tbody><SkeletonTableRows rows={8} cols={4} /></tbody>
          </table>
        ) : events.length === 0 ? (
          <p className="p-12 text-center text-sm text-muted-foreground">No audit events match the current filters.</p>
        ) : (
          <div className="divide-y divide-white/[0.04]">
            {events.map((e) => (
              <EventRow key={e.id} event={e} />
            ))}
          </div>
        )}
      </div>
    </div>
  );
}

function EventRow({ event: e }: { event: AuditEvent }) {
  const [expanded, setExpanded] = useState(false);
  const long = e.body && e.body.length > 120;

  return (
    <div className="px-4 py-3 hover:bg-white/[0.02] transition-colors">
      <div className="flex items-start gap-3">
        <span className={cn("mt-1.5 w-1.5 h-1.5 rounded-full shrink-0", SEVERITY_DOT[e.severity])} />
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2 flex-wrap">
            <span
              className={cn(
                "px-1.5 py-0.5 rounded text-[8px] font-black uppercase tracking-widest border",
                SEVERITY_TONE[e.severity]
              )}
            >
              {e.severity}
            </span>
            <span className="text-[10px] font-mono uppercase tracking-wider text-accent/70 px-1.5 py-0.5 bg-accent/[0.06] rounded border border-accent/10">
              {e.source}
            </span>
            <span className="text-xs font-bold flex-1 min-w-0 truncate">{e.title}</span>
            <span className="text-[9px] font-mono text-muted-foreground/40 shrink-0">
              {formatDateTime(e.utcTime)}
            </span>
          </div>
          {e.body && (
            <p
              className={cn(
                "text-[11px] text-muted-foreground/60 mt-1 leading-snug",
                !expanded && long && "line-clamp-2"
              )}
              onClick={() => long && setExpanded((v) => !v)}
              style={long ? { cursor: "pointer" } : undefined}
            >
              {e.body}
            </p>
          )}
          {(e.processName || e.correlationId) && (
            <div className="flex gap-3 mt-1 text-[9px] font-mono text-muted-foreground/30">
              {e.processName && <span>process:{e.processName}</span>}
              {e.correlationId && <span>cid:{e.correlationId}</span>}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-1">
      <span className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/50">
        {label}
      </span>
      {children}
    </div>
  );
}
