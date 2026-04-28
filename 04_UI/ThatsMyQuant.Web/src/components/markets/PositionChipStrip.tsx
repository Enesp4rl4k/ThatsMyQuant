import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";
import type { Position } from "@/api/types";

interface Props {
  /** All open positions across the portfolio (any symbol). */
  positions: Position[];
  /** Currently active symbol — chip is highlighted. */
  activeSymbol: string;
  /** Click handler — caller switches the chart symbol. */
  onSelect: (symbol: string) => void;
}

/**
 * Compact horizontal strip of every open position. Each chip shows the
 * symbol, side arrow + colour, and uPnL — clicking jumps the Trade Mode
 * chart to that position. The chip for the active symbol is highlighted.
 *
 * Hidden when there are no open positions.
 */
export function PositionChipStrip({ positions, activeSymbol, onSelect }: Props) {
  if (positions.length === 0) return null;

  // Group: aggregate same-symbol positions (e.g. dual-tier funding) so we
  // show a single chip per symbol with combined uPnL.
  const bySymbol = new Map<string, { side: "LONG" | "SHORT"; pnl: number; count: number }>();
  for (const p of positions) {
    const cur = bySymbol.get(p.symbol);
    if (cur) {
      cur.pnl += p.unrealizedPnL;
      cur.count += 1;
    } else {
      bySymbol.set(p.symbol, { side: p.side, pnl: p.unrealizedPnL, count: 1 });
    }
  }
  const chips = Array.from(bySymbol.entries()).sort((a, b) => Math.abs(b[1].pnl) - Math.abs(a[1].pnl));

  return (
    <div
      className="flex items-center gap-1.5 px-3 py-1.5 border-y border-white/[0.06] bg-black/85 overflow-x-auto"
      role="tablist"
      aria-label="Open positions — click to switch chart"
    >
      <span className="text-[9px] font-mono uppercase tracking-widest text-muted-foreground/45 shrink-0">
        Open ({chips.length})
      </span>
      {chips.map(([symbol, info]) => {
        const active = symbol.toUpperCase() === activeSymbol.toUpperCase();
        const isLong = info.side === "LONG";
        const pnlPositive = info.pnl >= 0;
        return (
          <button
            key={symbol}
            role="tab"
            aria-selected={active}
            onClick={() => onSelect(symbol)}
            title={`${symbol} ${info.side} · uPnL ${formatUsd(info.pnl)}${info.count > 1 ? ` · ${info.count} legs` : ""}`}
            className={cn(
              "shrink-0 flex items-center gap-1.5 px-2 py-0.5 rounded-md border text-[10px] font-mono transition-colors",
              active
                ? "border-accent/50 bg-accent/15 text-foreground shadow-[0_0_8px_rgba(14,165,233,0.25)]"
                : "border-white/[0.07] bg-white/[0.02] text-muted-foreground/80 hover:bg-white/[0.05] hover:text-foreground"
            )}
          >
            <span
              className={cn(
                "inline-block w-1.5 h-1.5 rounded-full",
                isLong ? "bg-emerald-400" : "bg-rose-400"
              )}
            />
            <span className="font-bold">{symbol}</span>
            <span className={cn("text-[9px]", isLong ? "text-emerald-300" : "text-rose-300")}>
              {isLong ? "↑" : "↓"}
            </span>
            <span className={cn("tabular-nums font-bold", pnlPositive ? "text-emerald-300" : "text-rose-300")}>
              {formatUsd(info.pnl)}
            </span>
            {info.count > 1 && (
              <span className="text-[8px] uppercase tracking-widest text-muted-foreground/45">
                ×{info.count}
              </span>
            )}
          </button>
        );
      })}
      <span className="ml-auto text-[8px] font-mono uppercase tracking-widest text-muted-foreground/35 shrink-0 whitespace-nowrap">
        [ ] · prev / next
      </span>
    </div>
  );
}
