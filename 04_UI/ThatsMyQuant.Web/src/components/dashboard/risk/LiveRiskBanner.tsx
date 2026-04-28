import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { computeRiskSummary } from "@/lib/riskMetrics";
import { cn } from "@/lib/utils";

/**
 * Headline risk strip — one row of gauges that read at a glance:
 *   VaR 95 / VaR 99 / Current DD vs limit / Concentration / Sharpe
 *
 * Pure client-side compute from the equity timeline + open positions feed.
 * No backend endpoint needed; refreshes whenever upstream queries refresh.
 */
const MAX_DD_LIMIT = 5; // % — circuit breaker threshold sense-check

export function LiveRiskBanner() {
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

  const risk = useMemo(
    () => computeRiskSummary(equityQ.data ?? [], positionsQ.data ?? []),
    [equityQ.data, positionsQ.data]
  );

  return (
    <div className="rounded-xl border border-rose-500/20 bg-gradient-to-r from-rose-500/[0.04] via-amber-500/[0.04] to-emerald-500/[0.04] backdrop-blur-xl p-4">
      <div className="flex items-center justify-between mb-3">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-rose-400/80 mb-0.5">
            Risk Command
          </p>
          <p className="text-[12px] font-serif italic text-muted-foreground/70">
            real-time book exposure · drawdown · concentration
          </p>
        </div>
        {risk && (
          <div className="flex items-center gap-1.5">
            <span
              className={cn(
                "w-1.5 h-1.5 rounded-full animate-pulse",
                risk.currentDrawdownPct < 1
                  ? "bg-emerald-400"
                  : risk.currentDrawdownPct < 3
                    ? "bg-amber-400"
                    : "bg-rose-400"
              )}
            />
            <span className="text-[10px] font-mono uppercase tracking-widest">
              {risk.currentDrawdownPct < 1
                ? "Healthy"
                : risk.currentDrawdownPct < 3
                  ? "Watching"
                  : "Stretched"}
            </span>
          </div>
        )}
      </div>

      {risk ? (
        <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-6 gap-3">
          <Gauge
            label="VaR 95% (1d)"
            value={`${risk.var95.toFixed(2)}%`}
            sub="historical · 5th pctile"
            tone={risk.var95 > 3 ? "neg" : risk.var95 > 1.5 ? "warn" : "pos"}
          />
          <Gauge
            label="CVaR 95%"
            value={`${risk.cvar95.toFixed(2)}%`}
            sub="expected shortfall"
            tone={risk.cvar95 > 4 ? "neg" : risk.cvar95 > 2 ? "warn" : "pos"}
          />
          <Gauge
            label="Current DD"
            value={`${risk.currentDrawdownPct.toFixed(2)}%`}
            sub={`max ${risk.maxDrawdownPct.toFixed(2)}% · ${risk.daysUnderwater}d under`}
            tone={
              risk.currentDrawdownPct > MAX_DD_LIMIT * 0.8
                ? "neg"
                : risk.currentDrawdownPct > MAX_DD_LIMIT * 0.5
                  ? "warn"
                  : "pos"
            }
            progress={
              MAX_DD_LIMIT > 0
                ? Math.min(100, (risk.currentDrawdownPct / MAX_DD_LIMIT) * 100)
                : 0
            }
          />
          <Gauge
            label="Concentration"
            value={
              risk.topConcentrationSymbol
                ? `${risk.topConcentrationPct.toFixed(0)}%`
                : "—"
            }
            sub={
              risk.topConcentrationSymbol
                ? `${risk.topConcentrationSymbol} · top position`
                : "no open positions"
            }
            tone={
              risk.topConcentrationPct > 60
                ? "neg"
                : risk.topConcentrationPct > 40
                  ? "warn"
                  : "pos"
            }
            progress={Math.min(100, risk.topConcentrationPct)}
          />
          <Gauge
            label="Daily Vol"
            value={`${risk.dailyVol.toFixed(2)}%`}
            sub={`avg ${risk.avgDailyReturn >= 0 ? "+" : ""}${risk.avgDailyReturn.toFixed(2)}%`}
            tone="neutral"
          />
          <Gauge
            label="Sharpe"
            value={risk.sharpeAnnualised.toFixed(2)}
            sub="annualised · daily"
            tone={
              risk.sharpeAnnualised >= 1.5
                ? "pos"
                : risk.sharpeAnnualised >= 0.5
                  ? "warn"
                  : "neg"
            }
          />
        </div>
      ) : (
        <p className="text-xs italic font-serif text-muted-foreground/60 py-4 text-center">
          — risk metrics activate after equity timeline has at least 5 days of data —
        </p>
      )}
    </div>
  );
}

function Gauge({
  label,
  value,
  sub,
  tone = "neutral",
  progress,
}: {
  label: string;
  value: string;
  sub?: string;
  tone?: "pos" | "neg" | "warn" | "neutral";
  progress?: number;
}) {
  return (
    <div className="rounded-lg bg-white/[0.02] border border-white/5 px-3 py-2">
      <p className="text-[8px] font-black uppercase tracking-widest text-muted-foreground/45 mb-1">
        {label}
      </p>
      <p
        className={cn(
          "font-mono font-black tabular-nums text-[16px] leading-none",
          tone === "pos" && "text-emerald-400",
          tone === "warn" && "text-amber-400",
          tone === "neg" && "text-rose-400"
        )}
      >
        {value}
      </p>
      {progress !== undefined && (
        <div className="mt-1.5 h-1 rounded-full bg-white/5 overflow-hidden">
          <div
            className={cn(
              "h-full transition-all",
              tone === "pos" && "bg-emerald-400/70",
              tone === "warn" && "bg-amber-400/70",
              tone === "neg" && "bg-rose-400/70",
              tone === "neutral" && "bg-accent/70"
            )}
            style={{ width: `${progress}%` }}
          />
        </div>
      )}
      {sub && (
        <p className="text-[9px] font-mono text-muted-foreground/50 italic mt-1 truncate">
          {sub}
        </p>
      )}
    </div>
  );
}
