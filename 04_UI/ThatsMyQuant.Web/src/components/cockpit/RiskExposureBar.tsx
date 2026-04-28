import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";
import type { Position } from "@/api/types";

interface Props {
  positions: Position[];
}

/**
 * Live risk exposure ribbon — shows what the system is actually risking right now.
 *
 * Computes from open positions:
 *   • Gross notional   = Σ |quantity × markPrice|        (absolute exposure both sides)
 *   • Net notional     = Σ side × quantity × markPrice  (directional bias; pairs cancel)
 *   • Margin used      = Σ |notional / leverage|         (capital tied up)
 *   • Margin %         = margin used / equity            (utilisation vs available cash)
 *   • Largest position % of gross — concentration metric
 *
 * Pairs positions are typically delta-neutral so net ≈ 0 even when gross is large —
 * that's a feature, not a bug. The bar visualises both: green if net is contained
 * relative to gross (good neutrality), amber/red if bias is heavy.
 */
export function RiskExposureBar({ positions }: Props) {
  const portfolioQ = useQuery({
    queryKey: ["portfolio"],
    queryFn: () => dashboardApi.portfolio(),
    refetchInterval: 5_000,
  });

  const exposure = useMemo(() => {
    if (positions.length === 0) return null;

    let gross = 0;
    let net = 0;
    let margin = 0;
    let maxPositionNotional = 0;

    for (const p of positions) {
      const px       = p.markPrice > 0 ? p.markPrice : p.entryPrice;
      const notional = Math.abs(p.quantity * px);
      const sign     = p.side === "LONG" ? 1 : -1;
      const lev      = p.leverage > 0 ? p.leverage : 1;

      gross += notional;
      net   += notional * sign;
      margin += notional / lev;
      if (notional > maxPositionNotional) maxPositionNotional = notional;
    }

    const equity   = portfolioQ.data?.totalEquity ?? 0;
    const marginPct = equity > 0 ? (margin / equity) * 100 : 0;
    const concPct   = gross > 0 ? (maxPositionNotional / gross) * 100 : 0;
    const netRatio  = gross > 0 ? Math.abs(net) / gross : 0; // 0 = perfectly neutral, 1 = one-sided

    return { gross, net, margin, marginPct, concPct, netRatio, equity };
  }, [positions, portfolioQ.data]);

  if (!exposure) {
    return (
      <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl px-4 py-3">
        <p className="text-[10px] font-black uppercase tracking-[0.25em] text-accent/80 mb-1">
          ⚖ Risk Exposure
        </p>
        <p className="text-[11px] italic font-serif text-muted-foreground/50">
          — no open positions —
        </p>
      </div>
    );
  }

  // Visual cues
  const marginTone =
    exposure.marginPct < 30 ? "text-emerald-400" :
    exposure.marginPct < 60 ? "text-amber-400"   :
                              "text-rose-400";
  const netTone =
    exposure.netRatio < 0.20 ? "text-emerald-400" :
    exposure.netRatio < 0.50 ? "text-amber-400"   :
                               "text-rose-400";
  const concTone =
    exposure.concPct < 35 ? "text-emerald-400" :
    exposure.concPct < 60 ? "text-amber-400"   :
                            "text-rose-400";

  // Long vs short ratio for the directional bias bar
  const longGross  = positions.filter((p) => p.side === "LONG").reduce((s, p) => s + Math.abs(p.quantity * p.markPrice), 0);
  const shortGross = positions.filter((p) => p.side === "SHORT").reduce((s, p) => s + Math.abs(p.quantity * p.markPrice), 0);
  const total      = longGross + shortGross || 1;
  const longPct    = (longGross / total) * 100;

  return (
    <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
      <div className="flex items-baseline justify-between mb-3">
        <p className="text-[10px] font-black uppercase tracking-[0.25em] text-accent/80">
          ⚖ Risk Exposure
        </p>
        <p className="text-[10px] font-mono text-muted-foreground/50">
          {positions.length} open · equity {formatUsd(exposure.equity)}
        </p>
      </div>

      {/* Long vs short directional bar */}
      <div className="mb-3">
        <div className="flex items-baseline justify-between text-[9px] font-mono mb-1">
          <span className="text-emerald-400">↑ LONG {formatUsd(longGross)}</span>
          <span className={cn("font-bold", netTone)}>
            net {(exposure.netRatio * 100).toFixed(0)}% bias
          </span>
          <span className="text-rose-400">SHORT {formatUsd(shortGross)} ↓</span>
        </div>
        <div className="relative h-2 rounded-full overflow-hidden bg-white/[0.06]">
          <div
            className="absolute inset-y-0 left-0 bg-emerald-500/70"
            style={{ width: `${longPct}%` }}
          />
          <div
            className="absolute inset-y-0 right-0 bg-rose-500/70"
            style={{ width: `${100 - longPct}%` }}
          />
          <div className="absolute left-1/2 top-0 bottom-0 w-px bg-white/30 -translate-x-1/2" />
        </div>
      </div>

      {/* Four metrics */}
      <div className="grid grid-cols-4 gap-3 text-[10px] font-mono">
        <Metric label="Gross"    value={formatUsd(exposure.gross)} tone="text-foreground/80" />
        <Metric label="Margin"   value={`${formatUsd(exposure.margin)} (${exposure.marginPct.toFixed(1)}%)`} tone={marginTone} />
        <Metric label="Net Bias" value={`${(exposure.netRatio * 100).toFixed(0)}%`} tone={netTone} />
        <Metric label="Top Pos"  value={`${exposure.concPct.toFixed(0)}%`} tone={concTone} />
      </div>
    </div>
  );
}

function Metric({ label, value, tone }: { label: string; value: string; tone: string }) {
  return (
    <div>
      <p className="text-[8px] uppercase tracking-widest text-muted-foreground/45 mb-0.5">{label}</p>
      <p className={cn("font-bold tabular-nums", tone)}>{value}</p>
    </div>
  );
}
