import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi, type StrategyBreakdown } from "@/api/dashboard";
import { cn } from "@/lib/utils";
import { formatUsd, pnlColor } from "@/lib/format";

interface StrategyCell extends StrategyBreakdown {
  state: "hot" | "warm" | "cooling" | "cold";
  ranking: number;
}

/** Heuristic regime for each strategy based on Sharpe + recent activity. */
function classifyState(s: StrategyBreakdown): StrategyCell["state"] {
  if (s.sharpe >= 1.5 && s.profitFactor >= 1.3) return "hot";
  if (s.sharpe >= 0.5 && s.profitFactor >= 1.05) return "warm";
  if (s.sharpe < 0 || s.profitFactor < 0.8) return "cold";
  return "cooling";
}

const STATE_TONE: Record<StrategyCell["state"], string> = {
  hot: "border-emerald-500/40 bg-emerald-500/[0.05] shadow-[0_0_20px_rgba(45,106,79,0.15)]",
  warm: "border-emerald-500/20 bg-emerald-500/[0.02]",
  cooling: "border-amber-500/30 bg-amber-500/[0.03]",
  cold: "border-rose-500/30 bg-rose-500/[0.04]",
};

const STATE_LABEL: Record<StrategyCell["state"], string> = {
  hot: "● HOT",
  warm: "● WARM",
  cooling: "◐ COOLING",
  cold: "○ COLD",
};

const STATE_DOT_TONE: Record<StrategyCell["state"], string> = {
  hot: "text-emerald-400",
  warm: "text-emerald-400/70",
  cooling: "text-amber-400",
  cold: "text-rose-400",
};

export function StrategyRotationMatrix() {
  const q = useQuery({
    queryKey: ["trades", "by-strategy"],
    queryFn: dashboardApi.strategyBreakdown,
    refetchInterval: 30_000,
  });

  const strategies = useMemo<StrategyCell[]>(() => {
    const list = q.data ?? [];
    return list
      .filter((s) => s.tradeCount >= 3) // only show strategies with meaningful sample
      .map((s, i) => ({ ...s, state: classifyState(s), ranking: i }))
      .sort((a, b) => b.netPnl - a.netPnl);
  }, [q.data]);

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      <div className="px-5 py-2.5 border-b border-white/5 flex items-baseline justify-between">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
            Strategy Rotation
          </p>
          <p className="text-[11px] font-serif italic text-muted-foreground/60">
            which edges are firing · rank by net P&amp;L · min 3 trades
          </p>
        </div>
        <span className="text-[9px] font-mono text-muted-foreground/50">
          {strategies.length} active
        </span>
      </div>

      {q.isLoading ? (
        <p className="p-6 text-xs italic font-serif text-muted-foreground/60 text-center">
          — loading strategies —
        </p>
      ) : strategies.length === 0 ? (
        <p className="p-6 text-xs italic font-serif text-muted-foreground/60 text-center">
          — no strategies have ≥3 closed trades yet —
        </p>
      ) : (
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-3 p-4">
          {strategies.map((s) => (
            <div
              key={s.strategyTag}
              className={cn("rounded-lg border p-3 transition-colors", STATE_TONE[s.state])}
            >
              <div className="flex items-baseline justify-between mb-2">
                <span className="font-bold text-[12px] tracking-tight truncate">{s.strategyTag}</span>
                <span className={cn("text-[9px] font-mono uppercase tracking-widest", STATE_DOT_TONE[s.state])}>
                  {STATE_LABEL[s.state]}
                </span>
              </div>

              <div className="grid grid-cols-3 gap-2 text-[10px] font-mono">
                <Cell label="Sharpe" value={s.sharpe.toFixed(2)} tone={s.sharpe >= 1 ? "pos" : s.sharpe >= 0 ? "neutral" : "neg"} />
                <Cell label="PF" value={s.profitFactor.toFixed(2)} tone={s.profitFactor >= 1.2 ? "pos" : s.profitFactor >= 1 ? "neutral" : "neg"} />
                <Cell label="WR" value={`${(s.winRate * 100).toFixed(0)}%`} tone={s.winRate >= 0.5 ? "pos" : "neg"} />
              </div>

              <div className="flex items-baseline justify-between mt-3 pt-2 border-t border-white/5">
                <span className="text-[9px] uppercase tracking-widest text-muted-foreground/50">
                  {s.tradeCount} trade{s.tradeCount === 1 ? "" : "s"}
                </span>
                <span className={cn("font-mono font-bold tabular-nums text-[12px]", pnlColor(s.netPnl))}>
                  {s.netPnl >= 0 ? "+" : ""}
                  {formatUsd(s.netPnl)}
                </span>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

function Cell({
  label,
  value,
  tone,
}: {
  label: string;
  value: string;
  tone: "pos" | "neutral" | "neg";
}) {
  return (
    <div>
      <p className="text-[8px] uppercase tracking-widest text-muted-foreground/45 mb-0.5">{label}</p>
      <p
        className={cn(
          "tabular-nums font-bold",
          tone === "pos" && "text-emerald-400",
          tone === "neg" && "text-rose-400"
        )}
      >
        {value}
      </p>
    </div>
  );
}
