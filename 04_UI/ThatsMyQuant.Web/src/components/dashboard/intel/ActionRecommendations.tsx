import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { proModeApi } from "@/api/proMode";
import { dashboardIntelApi } from "@/api/dashboardIntel";
import { computeRiskSummary } from "@/lib/riskMetrics";
import { cn } from "@/lib/utils";

interface Recommendation {
  level: "info" | "warn" | "critical";
  title: string;
  reason: string;
  /** Suggested operator action — short imperative. */
  action: string;
}

/**
 * Rule-based recommendation engine — reads risk/opportunities/preflight
 * data and surfaces 0-N actionable suggestions. No ML; deterministic rules.
 */
export function ActionRecommendations() {
  const equityQ = useQuery({
    queryKey: ["portfolio", "equity-timeline"],
    queryFn: dashboardApi.equityTimeline,
    refetchInterval: 60_000,
  });
  const positionsQ = useQuery({
    queryKey: ["positions"],
    queryFn: () => dashboardApi.positions(),
    refetchInterval: 5_000,
  });
  const portfolioQ = useQuery({
    queryKey: ["portfolio"],
    queryFn: () => dashboardApi.portfolio(),
    refetchInterval: 5_000,
  });
  const proQ = useQuery({
    queryKey: ["pro-mode", "snapshot"],
    queryFn: proModeApi.snapshot,
    refetchInterval: 30_000,
  });
  const preflightQ = useQuery({
    queryKey: ["dashboard", "preflight"],
    queryFn: dashboardIntelApi.preflight,
    refetchInterval: 30_000,
  });

  const recs = useMemo<Recommendation[]>(() => {
    const out: Recommendation[] = [];
    const risk = computeRiskSummary(equityQ.data ?? [], positionsQ.data ?? []);
    const pro = proQ.data;
    const preflight = preflightQ.data;

    // Pre-flight failures take priority
    if (preflight) {
      for (const c of preflight.checks) {
        if (c.status === "fail") {
          out.push({
            level: "critical",
            title: c.title,
            reason: c.detail,
            action: "Resolve before re-arming bot",
          });
        }
      }
    }

    // Risk-based rules
    if (risk) {
      if (risk.currentDrawdownPct > 4) {
        out.push({
          level: "critical",
          title: "Drawdown approaching circuit-breaker",
          reason: `Currently -${risk.currentDrawdownPct.toFixed(2)}% from peak (${risk.daysUnderwater}d underwater).`,
          action: "Reduce leverage by 1 step or pause new entries for 24h",
        });
      } else if (risk.currentDrawdownPct > 2) {
        out.push({
          level: "warn",
          title: "Edge confirmation pending",
          reason: `${risk.currentDrawdownPct.toFixed(2)}% drawdown — strategies may be off-regime.`,
          action: "Review per-strategy Sharpe before adding fresh exposure",
        });
      }

      if (risk.topConcentrationPct > 60 && risk.topConcentrationSymbol) {
        out.push({
          level: "warn",
          title: `${risk.topConcentrationSymbol} concentration high`,
          reason: `${risk.topConcentrationPct.toFixed(0)}% of book in single symbol — hard cap is 60%.`,
          action: "Cap further entries on this symbol; let it bleed off naturally",
        });
      }

      if (risk.sharpeAnnualised < 0 && (equityQ.data?.length ?? 0) > 14) {
        out.push({
          level: "warn",
          title: "Negative live Sharpe",
          reason: `Annualised Sharpe ${risk.sharpeAnnualised.toFixed(2)} over ${equityQ.data?.length ?? 0} days.`,
          action: "Edge may be deteriorating — pull weekly review of strategy mix",
        });
      }
    }

    // Bot armed/disarmed sanity
    if (pro) {
      if (!pro.isArmed && (positionsQ.data?.length ?? 0) === 0) {
        out.push({
          level: "info",
          title: "Bot is disarmed and book is flat",
          reason: "Nothing is trading or scanning right now.",
          action: "ARM the bot if you want fresh signals to fire",
        });
      }
      if (pro.circuitBreaker.state === "Tripped") {
        out.push({
          level: "critical",
          title: "Circuit breaker tripped",
          reason: pro.circuitBreaker.lastTripReason ?? "Risk supervisor halted the bot.",
          action: "Investigate the trip cause before re-arming",
        });
      }
    }

    // Capacity
    const positions = positionsQ.data ?? [];
    if (positions.length >= 6) {
      out.push({
        level: "info",
        title: "At position cap (6/6)",
        reason: "Book is full — no fresh entries until something resolves.",
        action: "Wait for closes; do not manually flatten unless thesis breaks",
      });
    }

    // Default — all good
    if (out.length === 0) {
      out.push({
        level: "info",
        title: "System operating within parameters",
        reason: "Risk, drawdown, concentration, and capacity all within targets.",
        action: "Stay observational — let the bot do its job",
      });
    }

    return out;
  }, [equityQ.data, positionsQ.data, portfolioQ.data, proQ.data, preflightQ.data]);

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      <div className="px-4 py-2.5 border-b border-white/5 flex items-baseline justify-between">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
            Recommendations
          </p>
          <p className="text-[11px] font-serif italic text-muted-foreground/60">
            rule-based action suggestions · {recs.length}
          </p>
        </div>
      </div>

      <ul className="divide-y divide-white/[0.04]">
        {recs.map((r, i) => (
          <li key={i} className="px-4 py-3">
            <div className="flex items-baseline gap-2 mb-1">
              <Pill level={r.level} />
              <span className="font-bold text-[12px] flex-1 truncate">{r.title}</span>
            </div>
            <p className="text-[11px] font-serif italic text-muted-foreground/70 ml-14 leading-snug">
              {r.reason}
            </p>
            <p className="text-[11px] font-serif text-foreground/85 ml-14 mt-1.5 leading-snug border-l-2 border-accent/40 pl-2">
              <span className="text-accent font-bold mr-1">→</span>
              {r.action}
            </p>
          </li>
        ))}
      </ul>
    </div>
  );
}

function Pill({ level }: { level: "info" | "warn" | "critical" }) {
  const map = {
    info: { bg: "bg-accent/15 text-accent border-accent/30", label: "INFO" },
    warn: { bg: "bg-amber-400/15 text-amber-400 border-amber-500/30", label: "WARN" },
    critical: { bg: "bg-rose-400/15 text-rose-400 border-rose-500/30", label: "CRITICAL" },
  } as const;
  const v = map[level];
  return (
    <span
      className={cn(
        "shrink-0 px-2 py-0.5 rounded text-[8px] font-black uppercase tracking-widest border w-12 text-center",
        v.bg
      )}
    >
      {v.label}
    </span>
  );
}
