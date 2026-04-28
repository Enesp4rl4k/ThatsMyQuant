import type { Position } from "@/api/types";
import { cn } from "@/lib/utils";
import { formatDecimal, formatPercentLoose, formatUsd, pnlColor } from "@/lib/format";
import { SkeletonTableRows } from "@/components/ui/Skeleton";
import { EmptyState } from "@/components/ui/EmptyState";

interface PositionsTableProps {
  positions: Position[];
  isLoading: boolean;
  onRowClick?: (p: Position) => void;
}

export function PositionsTable({ positions, isLoading, onRowClick }: PositionsTableProps) {
  if (isLoading) {
    return (
      <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
        <table className="w-full text-sm">
          <tbody><SkeletonTableRows rows={3} cols={9} /></tbody>
        </table>
      </div>
    );
  }

  if (positions.length === 0) {
    return (
      <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl">
        <EmptyState
          icon="positions"
          title="No open positions"
          description="The book is flat. Either the bot is disarmed, or strategies are waiting for high-conviction setups. Quality trades come from patience — let the scanner find an edge."
          cta={{ label: "Open Pro Mode", to: "/pro-mode" }}
        />
      </div>
    );
  }

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b border-white/5 text-[9px] uppercase tracking-[0.2em] text-muted-foreground/60">
            <th className="text-left px-5 py-3 font-black">Symbol</th>
            <th className="text-left px-3 py-3 font-black">Side</th>
            <th className="text-right px-3 py-3 font-black">Qty</th>
            <th className="text-right px-3 py-3 font-black">Entry</th>
            <th className="text-right px-3 py-3 font-black">Mark</th>
            <th className="text-right px-3 py-3 font-black">uPnL</th>
            <th className="text-right px-3 py-3 font-black">ROE</th>
            <th className="text-right px-3 py-3 font-black">Lev</th>
            <th className="text-left px-3 py-3 font-black">Strategy</th>
            <th className="text-left px-5 py-3 font-black">Status</th>
          </tr>
        </thead>
        <tbody>
          {positions.map((p) => (
            <tr
              key={p.id}
              onClick={() => onRowClick?.(p)}
              className={cn(
                "border-b border-white/[0.02] transition-colors",
                onRowClick
                  ? "cursor-pointer hover:bg-white/[0.04]"
                  : "hover:bg-white/[0.02]"
              )}
            >
              <td className="px-5 py-3 font-bold tracking-tight">{p.symbol}</td>
              <td className="px-3 py-3">
                <span
                  className={cn(
                    "px-2 py-0.5 rounded text-[10px] font-black tracking-wider",
                    p.side === "LONG"
                      ? "bg-emerald-400/10 text-emerald-400"
                      : "bg-rose-400/10 text-rose-400"
                  )}
                >
                  {p.side}
                </span>
              </td>
              <td className="px-3 py-3 text-right font-mono">{formatDecimal(p.quantity)}</td>
              <td className="px-3 py-3 text-right font-mono">{formatUsd(p.entryPrice)}</td>
              <td className="px-3 py-3 text-right font-mono">{formatUsd(p.markPrice)}</td>
              <td className={cn("px-3 py-3 text-right font-mono font-bold", pnlColor(p.unrealizedPnL))}>
                {formatUsd(p.unrealizedPnL)}
              </td>
              <td className={cn("px-3 py-3 text-right font-mono", pnlColor(p.roe))}>
                {formatPercentLoose(p.roe)}
              </td>
              <td className="px-3 py-3 text-right font-mono text-muted-foreground">
                {formatDecimal(p.leverage)}x
              </td>
              <td className="px-3 py-3 text-[10px] uppercase tracking-wider text-muted-foreground">
                {p.strategyTag}
              </td>
              <td className="px-5 py-3 text-[10px] uppercase tracking-wider text-accent/80">
                {p.lifecycleStatus}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
