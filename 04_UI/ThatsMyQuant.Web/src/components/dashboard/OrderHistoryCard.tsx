import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { auditApi, type AuditEvent } from "@/api/audit";
import { cn } from "@/lib/utils";

/**
 * Order / position event history surfaced from the OperatorAlertEvents
 * cross-process log. Hybrid executors emit alerts via IRealtimeAlerter
 * (Telegram + DB log) on entry, exit, partial-exit, circuit-breaker, etc.
 * Until the executors write to the Trade table directly, this audit log is
 * our best historical record.
 *
 * Filters to event sources that look like trading actions:
 *   • PairsTrading / FundingHybrid / RiskSupervisor / CircuitBreaker
 *
 * Each row: time · source · severity dot · title · body excerpt.
 */
export function OrderHistoryCard({ limit = 30 }: { limit?: number }) {
  const auditQ = useQuery({
    queryKey: ["audit", "trading-events", limit],
    queryFn:  () => auditApi.events({ limit: limit * 3 }),
    refetchInterval: 15_000,
  });

  const rows = useMemo(() => {
    return (auditQ.data ?? [])
      .filter(isTradingEvent)
      .slice(0, limit);
  }, [auditQ.data, limit]);

  const counts = useMemo(() => {
    const all = auditQ.data ?? [];
    return {
      total:   all.length,
      trading: all.filter(isTradingEvent).length,
      errors:  all.filter((e) => e.severity === "error").length,
      successes: all.filter((e) => e.severity === "success").length,
    };
  }, [auditQ.data]);

  return (
    <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
      <div className="flex items-baseline justify-between mb-3">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
            📜 Order History · live
          </p>
          <p className="text-[10px] font-mono text-muted-foreground/55 mt-0.5">
            From OperatorAlertEvents log · trading-related sources
          </p>
        </div>
        <div className="flex items-center gap-1.5 text-[9px] font-mono">
          <Pill tone="emerald">{counts.successes} ✓</Pill>
          <Pill tone="rose">{counts.errors} ✗</Pill>
          <Pill tone="muted">{counts.trading} / {counts.total} events</Pill>
        </div>
      </div>

      {rows.length === 0 ? (
        <p className="text-[11px] italic font-serif text-muted-foreground/50 text-center py-4">
          — no trading events in the audit log yet —
        </p>
      ) : (
        <div className="space-y-1 max-h-[420px] overflow-y-auto">
          {rows.map((e) => <Row key={e.id} e={e} />)}
        </div>
      )}
    </div>
  );
}

function isTradingEvent(e: AuditEvent): boolean {
  const src = (e.source || "").toLowerCase();
  const title = (e.title || "").toLowerCase();
  return (
    src.includes("pair")     ||
    src.includes("fund")     ||
    src.includes("hybrid")   ||
    src.includes("risk")     ||
    src.includes("circuit")  ||
    src.includes("trade")    ||
    src.includes("position") ||
    title.includes("entry")  ||
    title.includes("exit")   ||
    title.includes("close")  ||
    title.includes("position") ||
    title.includes("trade")
  );
}

function Row({ e }: { e: AuditEvent }) {
  const sevDot =
    e.severity === "error"   ? "bg-rose-400" :
    e.severity === "warning" ? "bg-amber-400" :
    e.severity === "success" ? "bg-emerald-400" :
                               "bg-cyan-400";
  const sevLabel =
    e.severity === "error"   ? "text-rose-300" :
    e.severity === "warning" ? "text-amber-300" :
    e.severity === "success" ? "text-emerald-300" :
                               "text-cyan-300";

  return (
    <div className="grid grid-cols-[60px_64px_18px_1fr] gap-2 px-2 py-1.5 rounded text-[10px] font-mono hover:bg-white/[0.03] border border-transparent hover:border-white/[0.04]">
      <span className="text-muted-foreground/55">{fmtTime(e.utcTime)}</span>
      <span className="text-muted-foreground/65 uppercase tracking-widest text-[8px] truncate" title={e.source}>
        {e.source}
      </span>
      <span className={cn("w-1.5 h-1.5 rounded-full self-center justify-self-center", sevDot)} />
      <div className="min-w-0">
        <p className={cn("font-bold truncate", sevLabel)}>{e.title}</p>
        {e.body && (
          <p className="text-muted-foreground/55 text-[9px] truncate" title={e.body}>{e.body}</p>
        )}
      </div>
    </div>
  );
}

function Pill({ tone, children }: { tone: "emerald" | "rose" | "amber" | "muted"; children: React.ReactNode }) {
  const cls =
    tone === "emerald" ? "border-emerald-400/30 bg-emerald-500/10 text-emerald-300" :
    tone === "rose"    ? "border-rose-400/30    bg-rose-500/10    text-rose-300"    :
    tone === "amber"   ? "border-amber-400/30   bg-amber-500/10   text-amber-300"   :
                         "border-white/10       bg-white/[0.03]   text-muted-foreground/55";
  return (
    <span className={cn("px-1.5 py-0.5 rounded-full border uppercase tracking-widest font-black", cls)}>
      {children}
    </span>
  );
}

function fmtTime(iso: string): string {
  try {
    const d = new Date(iso);
    return d.toLocaleString([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
  } catch { return ""; }
}
