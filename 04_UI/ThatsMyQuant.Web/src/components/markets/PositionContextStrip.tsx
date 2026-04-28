import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import type { Position } from "@/api/types";
import { cn } from "@/lib/utils";
import { formatUsd, pnlColor } from "@/lib/format";

interface Props {
  symbol: string;
}

/**
 * Thin strip rendered between the chart header and chart body when there's
 * an open position on the selected symbol. Otherwise renders nothing.
 * Surfaces side, qty, entry, mark, P&L%, leverage, and SL/TP distances.
 */
export function PositionContextStrip({ symbol }: Props) {
  const q = useQuery({
    queryKey: ["positions"],
    queryFn: () => dashboardApi.positions(),
    refetchInterval: 5_000,
  });

  const pos = (q.data ?? []).find(
    (p) => p.symbol.toUpperCase() === symbol.toUpperCase()
  );

  if (!pos) return null;

  const isLong = pos.side === "LONG";
  const slDist = pos.stopLoss > 0 ? ((pos.stopLoss - pos.markPrice) / pos.markPrice) * 100 : null;
  const tpDist = pos.takeProfit > 0 ? ((pos.takeProfit - pos.markPrice) / pos.markPrice) * 100 : null;

  return (
    <div
      className={cn(
        "px-5 py-2.5 border-b border-white/5 flex flex-wrap items-center gap-x-5 gap-y-1.5 text-[11px] font-mono",
        isLong
          ? "bg-emerald-500/[0.04] border-l-2 border-l-emerald-500/40"
          : "bg-rose-500/[0.04] border-l-2 border-l-rose-500/40"
      )}
    >
      <span
        className={cn(
          "px-2 py-0.5 rounded text-[9px] font-black uppercase tracking-widest border",
          isLong
            ? "bg-emerald-400/10 text-emerald-400 border-emerald-500/30"
            : "bg-rose-400/10 text-rose-400 border-rose-500/30"
        )}
      >
        ● Open · {pos.side}
      </span>

      <Cell label="Qty" value={pos.quantity.toFixed(4)} />
      <Cell label="Entry" value={fmt(pos.entryPrice)} />
      <Cell label="Mark" value={fmt(pos.markPrice)} />
      <Cell
        label="P&L"
        value={`${formatUsd(pos.unrealizedPnL)}`}
        toneClass={pnlColor(pos.unrealizedPnL)}
      />
      <Cell
        label="ROE"
        value={`${pos.roe >= 0 ? "+" : ""}${(pos.roe * 100).toFixed(2)}%`}
        toneClass={pnlColor(pos.roe)}
      />
      <Cell label="Lev" value={`${pos.leverage}x`} />

      {slDist !== null && (
        <Cell
          label="SL"
          value={`${fmt(pos.stopLoss)} (${slDist >= 0 ? "+" : ""}${slDist.toFixed(2)}%)`}
          toneClass="text-rose-400"
        />
      )}
      {tpDist !== null && (
        <Cell
          label="TP"
          value={`${fmt(pos.takeProfit)} (${tpDist >= 0 ? "+" : ""}${tpDist.toFixed(2)}%)`}
          toneClass="text-emerald-400"
        />
      )}

      {pos.strategyTag && (
        <span className="ml-auto text-[9px] uppercase tracking-widest text-muted-foreground/50">
          {pos.strategyTag}
        </span>
      )}
    </div>
  );
}

function Cell({ label, value, toneClass }: { label: string; value: string; toneClass?: string }) {
  return (
    <span className="flex items-baseline gap-1.5">
      <span className="text-[8px] uppercase tracking-widest text-muted-foreground/50">{label}</span>
      <span className={cn("font-bold tabular-nums", toneClass)}>{value}</span>
    </span>
  );
}

/** Build chart overlay lines from a position — returns empty if no match. */
export function positionOverlaysFor(symbol: string, positions: Position[] | undefined) {
  if (!positions) return [];
  const pos = positions.find((p) => p.symbol.toUpperCase() === symbol.toUpperCase());
  if (!pos) return [];

  const overlays = [];
  if (pos.entryPrice > 0) {
    overlays.push({
      price: pos.entryPrice,
      color: "#1E3A8A",
      label: "ENTRY",
      dash: false,
    });
  }
  if (pos.stopLoss > 0) {
    overlays.push({
      price: pos.stopLoss,
      color: "#B5364B",
      label: "SL",
      dash: true,
    });
  }
  if (pos.takeProfit > 0) {
    overlays.push({
      price: pos.takeProfit,
      color: "#2D6A4F",
      label: "TP",
      dash: true,
    });
  }
  if (pos.markPrice > 0) {
    overlays.push({
      price: pos.markPrice,
      color: "#6B6A64",
      label: "MARK",
      dash: true,
    });
  }
  return overlays;
}

function fmt(v: number): string {
  if (v >= 1000) return v.toLocaleString(undefined, { maximumFractionDigits: 2 });
  if (v >= 1) return v.toFixed(2);
  return v.toFixed(5);
}
