import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { cn } from "@/lib/utils";
import { formatUsd, pnlColor } from "@/lib/format";
import { SkeletonTableRows } from "@/components/ui/Skeleton";

export function StrategyBreakdownCard() {
  const q = useQuery({
    queryKey: ["trades", "by-strategy"],
    queryFn: dashboardApi.strategyBreakdown,
    refetchInterval: 30_000,
  });

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      <div className="px-5 py-3 border-b border-white/5 flex items-center justify-between">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
          Per-Strategy Performance
        </p>
        {q.isFetching && (
          <span className="text-[9px] uppercase tracking-widest text-accent/60 animate-pulse">live</span>
        )}
      </div>
      {q.isLoading ? (
        <table className="w-full text-xs">
          <tbody><SkeletonTableRows rows={4} cols={8} /></tbody>
        </table>
      ) : !q.data || q.data.length === 0 ? (
        <p className="p-8 text-center text-xs text-muted-foreground">No closed trades yet.</p>
      ) : (
        <table className="w-full text-xs">
          <thead>
            <tr className="border-b border-white/5 text-[9px] uppercase tracking-[0.2em] text-muted-foreground/60">
              <th className="text-left px-4 py-2 font-black">Strategy</th>
              <th className="text-right px-3 py-2 font-black">Trades</th>
              <th className="text-right px-3 py-2 font-black">Win%</th>
              <th className="text-right px-3 py-2 font-black">PF</th>
              <th className="text-right px-3 py-2 font-black">Sharpe</th>
              <th className="text-right px-3 py-2 font-black">Net PnL</th>
              <th className="text-right px-3 py-2 font-black">Avg</th>
              <th className="text-right px-4 py-2 font-black">Max W / L</th>
            </tr>
          </thead>
          <tbody>
            {q.data.map((s) => (
              <tr key={s.strategyTag} className="border-b border-white/[0.02] hover:bg-white/[0.02]">
                <td className="px-4 py-2 font-bold text-foreground/90">{s.strategyTag}</td>
                <td className="px-3 py-2 text-right font-mono text-muted-foreground">{s.tradeCount}</td>
                <td className={cn("px-3 py-2 text-right font-mono", s.winRate >= 0.5 ? "text-emerald-400" : "text-amber-400")}>
                  {(s.winRate * 100).toFixed(1)}%
                </td>
                <td className={cn("px-3 py-2 text-right font-mono", s.profitFactor >= 1.2 ? "text-emerald-400" : s.profitFactor >= 1 ? "text-amber-400" : "text-rose-400")}>
                  {s.profitFactor === 0 ? "—" : s.profitFactor.toFixed(2)}
                </td>
                <td className={cn("px-3 py-2 text-right font-mono", s.sharpe >= 1 ? "text-emerald-400" : s.sharpe >= 0 ? "text-amber-400" : "text-rose-400")}>
                  {s.sharpe.toFixed(2)}
                </td>
                <td className={cn("px-3 py-2 text-right font-mono font-bold", pnlColor(s.netPnl))}>
                  {formatUsd(s.netPnl)}
                </td>
                <td className={cn("px-3 py-2 text-right font-mono", pnlColor(s.averagePnl))}>
                  {formatUsd(s.averagePnl)}
                </td>
                <td className="px-4 py-2 text-right font-mono text-[10px]">
                  <span className="text-emerald-400">{formatUsd(s.maxWin)}</span>
                  <span className="text-muted-foreground/40 mx-1">/</span>
                  <span className="text-rose-400">{formatUsd(s.maxLoss)}</span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
