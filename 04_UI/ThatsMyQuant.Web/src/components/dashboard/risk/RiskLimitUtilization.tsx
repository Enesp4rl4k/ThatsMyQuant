import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";

/**
 * Capital allocation + risk limit utilization snapshot. Shows:
 *  - per-symbol notional pie / bar
 *  - position count vs cap
 *  - (synth) Kelly fraction utilisation
 *  - DD throttle status
 */

const POSITION_CAP = 6;
const KELLY_BUDGET_PCT = 100; // assume full Kelly allocated when book is full

export function RiskLimitUtilization() {
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

  const buckets = useMemo(() => {
    const positions = positionsQ.data ?? [];
    if (positions.length === 0) return [];
    const total = positions.reduce(
      (s, p) => s + Math.abs(p.quantity * p.markPrice),
      0
    );
    return positions
      .map((p) => ({
        symbol: p.symbol,
        notional: Math.abs(p.quantity * p.markPrice),
        pct: total > 0 ? (Math.abs(p.quantity * p.markPrice) / total) * 100 : 0,
        side: p.side,
        unrealized: p.unrealizedPnL,
      }))
      .sort((a, b) => b.notional - a.notional);
  }, [positionsQ.data]);

  const positions = positionsQ.data ?? [];
  const portfolio = portfolioQ.data;
  const positionUtil = (positions.length / POSITION_CAP) * 100;
  const kellyUtil = positions.length > 0 ? Math.min(100, (positions.length / POSITION_CAP) * KELLY_BUDGET_PCT) : 0;

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5 space-y-4">
      <div>
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-amber-400/70 mb-0.5">
          Capital Allocation
        </p>
        <p className="text-sm font-bold">
          Risk Limits{" "}
          <span className="text-muted-foreground/40 font-light text-xs">
            — utilization vs hard caps
          </span>
        </p>
      </div>

      {/* Limit gauges */}
      <div className="grid grid-cols-3 gap-3">
        <LimitGauge
          label="Positions"
          current={positions.length.toString()}
          limit={POSITION_CAP.toString()}
          pct={positionUtil}
          tone={positionUtil >= 100 ? "neg" : positionUtil >= 70 ? "warn" : "pos"}
        />
        <LimitGauge
          label="Kelly Budget"
          current={`${kellyUtil.toFixed(0)}%`}
          limit="100%"
          pct={kellyUtil}
          tone={kellyUtil >= 90 ? "warn" : "pos"}
        />
        <LimitGauge
          label="Throttle"
          current={portfolio && portfolio.unrealizedPnL < 0 ? "Active" : "Off"}
          limit={portfolio ? formatUsd(Math.abs(portfolio.unrealizedPnL)) : "—"}
          pct={
            portfolio && portfolio.totalEquity > 0
              ? Math.min(100, Math.max(0, (-portfolio.unrealizedPnL / portfolio.totalEquity) * 100 * 20))
              : 0
          }
          tone={portfolio && portfolio.unrealizedPnL < 0 ? "warn" : "pos"}
        />
      </div>

      {/* Per-symbol allocation */}
      <div className="pt-3 border-t border-white/5">
        <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/50 mb-2">
          Per-symbol notional · {buckets.length}/{POSITION_CAP}
        </p>
        {buckets.length === 0 ? (
          <p className="text-[11px] italic font-serif text-muted-foreground/50 py-3">
            — book is flat —
          </p>
        ) : (
          <div className="space-y-1.5">
            {buckets.map((b) => (
              <div key={b.symbol} className="flex items-center gap-2 text-[11px] font-mono">
                <span className="w-20 truncate font-bold">{b.symbol}</span>
                <span
                  className={cn(
                    "w-10 text-[9px] uppercase tracking-widest",
                    b.side === "LONG" ? "text-emerald-400" : "text-rose-400"
                  )}
                >
                  {b.side}
                </span>
                <div className="flex-1 h-2 bg-white/5 rounded-full overflow-hidden">
                  <div
                    className={cn(
                      "h-full transition-all",
                      b.pct > 50
                        ? "bg-rose-400/80"
                        : b.pct > 30
                          ? "bg-amber-400/80"
                          : "bg-accent/70"
                    )}
                    style={{ width: `${Math.min(100, b.pct)}%` }}
                  />
                </div>
                <span className="w-12 text-right tabular-nums">{b.pct.toFixed(0)}%</span>
                <span className="w-20 text-right tabular-nums text-muted-foreground/70">
                  {formatUsd(b.notional)}
                </span>
                <span
                  className={cn(
                    "w-16 text-right tabular-nums",
                    b.unrealized >= 0 ? "text-emerald-400" : "text-rose-400"
                  )}
                >
                  {b.unrealized >= 0 ? "+" : ""}
                  {b.unrealized.toFixed(0)}
                </span>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}

function LimitGauge({
  label,
  current,
  limit,
  pct,
  tone,
}: {
  label: string;
  current: string;
  limit: string;
  pct: number;
  tone: "pos" | "warn" | "neg";
}) {
  const safe = Math.max(0, Math.min(100, pct));
  return (
    <div className="rounded-lg bg-white/[0.02] border border-white/5 px-3 py-2.5">
      <p className="text-[8px] font-black uppercase tracking-widest text-muted-foreground/45">
        {label}
      </p>
      <div className="flex items-baseline justify-between mt-0.5">
        <span
          className={cn(
            "font-mono font-black tabular-nums text-[14px]",
            tone === "pos" && "text-emerald-400",
            tone === "warn" && "text-amber-400",
            tone === "neg" && "text-rose-400"
          )}
        >
          {current}
        </span>
        <span className="text-[10px] font-mono text-muted-foreground/40">/ {limit}</span>
      </div>
      <div className="mt-1.5 h-1 rounded-full bg-white/5 overflow-hidden">
        <div
          className={cn(
            "h-full transition-all",
            tone === "pos" && "bg-emerald-400/70",
            tone === "warn" && "bg-amber-400/70",
            tone === "neg" && "bg-rose-400/70"
          )}
          style={{ width: `${safe}%` }}
        />
      </div>
    </div>
  );
}
