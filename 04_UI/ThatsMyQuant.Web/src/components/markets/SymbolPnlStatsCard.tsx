import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import type { TradeJournal } from "@/api/types";
import { cn } from "@/lib/utils";
import { formatUsd, pnlColor } from "@/lib/format";

interface Props {
  symbol: string;
}

/**
 * Per-symbol historical performance — filters closed trades to this symbol
 * and computes WR / PF / Sharpe / avg trade / best / worst / streaks.
 * Surfaces "is this symbol kind to me?" at a glance.
 */
export function SymbolPnlStatsCard({ symbol }: Props) {
  const q = useQuery({
    queryKey: ["trades", "filtered", { limit: 500 }],
    queryFn: () => dashboardApi.filteredTrades({ limit: 500 }),
    refetchInterval: 30_000,
  });

  const symTrades = useMemo<TradeJournal[]>(
    () => (q.data ?? []).filter((t) => t.symbol === symbol),
    [q.data, symbol]
  );

  const stats = useMemo(() => {
    if (symTrades.length === 0) return null;

    const closed = symTrades.filter((t) => t.exitTime != null);
    if (closed.length === 0) return null;

    const wins = closed.filter((t) => t.realizedPnL > 0);
    const losses = closed.filter((t) => t.realizedPnL < 0);
    const winRate = wins.length / closed.length;

    const grossProfit = wins.reduce((s, t) => s + t.realizedPnL, 0);
    const grossLoss = Math.abs(losses.reduce((s, t) => s + t.realizedPnL, 0));
    const profitFactor = grossLoss === 0 ? (grossProfit > 0 ? 99 : 0) : grossProfit / grossLoss;

    const totalPnl = closed.reduce((s, t) => s + t.realizedPnL, 0);
    const avgPnl = totalPnl / closed.length;
    const avgWin = wins.length > 0 ? grossProfit / wins.length : 0;
    const avgLoss = losses.length > 0 ? -grossLoss / losses.length : 0;

    // Sharpe — annualised proxy from per-trade pnlPercent
    const returns = closed.map((t) => t.pnlPercent);
    const mean = returns.reduce((a, b) => a + b, 0) / returns.length;
    const variance =
      returns.reduce((s, r) => s + (r - mean) ** 2, 0) / Math.max(1, returns.length - 1);
    const stdev = Math.sqrt(variance);
    const sharpe = stdev > 0 ? (mean / stdev) * Math.sqrt(252) : 0;

    // Streak
    const sorted = [...closed].sort(
      (a, b) => new Date(a.exitTime!).getTime() - new Date(b.exitTime!).getTime()
    );
    let curStreak = 0;
    let curStreakDir: "win" | "loss" | null = null;
    let bestStreakWins = 0;
    let bestStreakLosses = 0;
    let curWins = 0;
    let curLosses = 0;
    for (const t of sorted) {
      if (t.realizedPnL > 0) {
        curWins++;
        curLosses = 0;
        bestStreakWins = Math.max(bestStreakWins, curWins);
      } else if (t.realizedPnL < 0) {
        curLosses++;
        curWins = 0;
        bestStreakLosses = Math.max(bestStreakLosses, curLosses);
      }
    }
    if (curWins > 0) {
      curStreak = curWins;
      curStreakDir = "win";
    } else if (curLosses > 0) {
      curStreak = curLosses;
      curStreakDir = "loss";
    }

    const best = closed.reduce(
      (b, t) => (t.realizedPnL > b.realizedPnL ? t : b),
      closed[0]
    );
    const worst = closed.reduce(
      (w, t) => (t.realizedPnL < w.realizedPnL ? t : w),
      closed[0]
    );

    return {
      total: closed.length,
      wins: wins.length,
      losses: losses.length,
      winRate,
      profitFactor,
      sharpe,
      totalPnl,
      avgPnl,
      avgWin,
      avgLoss,
      best,
      worst,
      curStreak,
      curStreakDir,
      bestStreakWins,
      bestStreakLosses,
    };
  }, [symTrades]);

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden h-full flex flex-col">
      <div className="px-4 py-2.5 border-b border-white/5">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
          {symbol} Performance
        </p>
        <p className="text-[11px] font-serif italic text-muted-foreground/60">
          historical edge on this symbol · closed trades only
        </p>
      </div>

      <div className="p-4 flex-1 overflow-y-auto">
        {!stats ? (
          <p className="text-xs italic font-serif text-muted-foreground/60 text-center py-6">
            — no closed trades yet on {symbol} —
          </p>
        ) : (
          <>
            {/* Headline 4-stat row */}
            <div className="grid grid-cols-4 gap-3 pb-3 border-b border-white/5">
              <Stat
                label="Win Rate"
                value={`${(stats.winRate * 100).toFixed(0)}%`}
                tone={stats.winRate >= 0.5 ? "pos" : "neg"}
              />
              <Stat
                label="PF"
                value={stats.profitFactor.toFixed(2)}
                tone={stats.profitFactor >= 1.2 ? "pos" : stats.profitFactor >= 1 ? "neutral" : "neg"}
              />
              <Stat
                label="Sharpe"
                value={stats.sharpe.toFixed(2)}
                tone={stats.sharpe >= 1 ? "pos" : stats.sharpe >= 0 ? "neutral" : "neg"}
              />
              <Stat
                label="Trades"
                value={stats.total.toString()}
              />
            </div>

            {/* P&L block */}
            <div className="grid grid-cols-2 gap-3 py-3 border-b border-white/5">
              <Stat label="Total P&L" value={formatUsd(stats.totalPnl)} toneClass={pnlColor(stats.totalPnl)} />
              <Stat label="Avg / Trade" value={formatUsd(stats.avgPnl)} toneClass={pnlColor(stats.avgPnl)} />
              <Stat label="Avg Win" value={formatUsd(stats.avgWin)} tone="pos" />
              <Stat label="Avg Loss" value={formatUsd(stats.avgLoss)} tone="neg" />
            </div>

            {/* Best/Worst trade */}
            <div className="py-3 border-b border-white/5 space-y-2">
              <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/50">
                Extremes
              </p>
              <ExtremeRow
                label="Best"
                trade={stats.best}
                tone="pos"
              />
              <ExtremeRow
                label="Worst"
                trade={stats.worst}
                tone="neg"
              />
            </div>

            {/* Streaks */}
            <div className="pt-3 grid grid-cols-3 gap-3">
              <Stat
                label="Current"
                value={
                  stats.curStreak === 0
                    ? "—"
                    : `${stats.curStreak}${stats.curStreakDir === "win" ? "W" : "L"}`
                }
                tone={stats.curStreakDir === "win" ? "pos" : stats.curStreakDir === "loss" ? "neg" : "neutral"}
              />
              <Stat label="Best Run" value={`${stats.bestStreakWins}W`} tone="pos" />
              <Stat label="Worst Run" value={`${stats.bestStreakLosses}L`} tone="neg" />
            </div>
          </>
        )}
      </div>
    </div>
  );
}

function Stat({
  label,
  value,
  tone = "neutral",
  toneClass,
}: {
  label: string;
  value: string;
  tone?: "pos" | "neg" | "neutral";
  toneClass?: string;
}) {
  return (
    <div>
      <p className="text-[8px] font-black uppercase tracking-widest text-muted-foreground/45 mb-0.5">
        {label}
      </p>
      <p
        className={cn(
          "font-mono font-bold tabular-nums text-[13px]",
          toneClass,
          !toneClass && tone === "pos" && "text-emerald-400",
          !toneClass && tone === "neg" && "text-rose-400"
        )}
      >
        {value}
      </p>
    </div>
  );
}

function ExtremeRow({
  label,
  trade,
  tone,
}: {
  label: string;
  trade: TradeJournal;
  tone: "pos" | "neg";
}) {
  return (
    <div className="flex items-center justify-between text-[11px]">
      <span className="text-[9px] uppercase tracking-widest text-muted-foreground/60">{label}</span>
      <div className="flex items-center gap-3 font-mono">
        <span
          className={cn(
            "px-1.5 py-0.5 rounded text-[8px] font-black uppercase tracking-widest",
            trade.side === "LONG"
              ? "bg-emerald-400/10 text-emerald-400"
              : "bg-rose-400/10 text-rose-400"
          )}
        >
          {trade.side}
        </span>
        <span
          className={cn(
            "font-bold tabular-nums",
            tone === "pos" ? "text-emerald-400" : "text-rose-400"
          )}
        >
          {formatUsd(trade.realizedPnL)}
        </span>
        <span className={tone === "pos" ? "text-emerald-400/70" : "text-rose-400/70"}>
          {(trade.pnlPercent >= 0 ? "+" : "") + (trade.pnlPercent * 100).toFixed(2)}%
        </span>
      </div>
    </div>
  );
}
