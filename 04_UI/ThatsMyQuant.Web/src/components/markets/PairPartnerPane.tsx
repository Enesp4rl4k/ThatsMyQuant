import { useEffect, useMemo, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { marketsApi, type Timeframe } from "@/api/markets";
import { pairsApi } from "@/api/pairs";
import { CandleChart } from "@/components/charts/CandleChart";
import { cn } from "@/lib/utils";

interface Props {
  /** The primary symbol shown in the main chart. We try to match a pair where this is symbolY. */
  primarySymbol: string;
  timeframe: Timeframe;
}

/**
 * "Pair partner" mini-chart for Trade Mode. When the primary symbol is part
 * of an active cointegrated pair (e.g. SOLUSDT in SOLUSDT/AVAXUSDT), this
 * pane appears beneath the main chart and shows:
 *   • The PARTNER symbol's candles (e.g. AVAXUSDT)
 *   • Live z-score gauge + state (LongSpread / ShortSpread / Neutral)
 *   • β, half-life, correlation
 *
 * If the primary symbol isn't in any active pair, we render a small empty
 * note instead. Click "Swap" to pivot the main chart to the partner.
 */
export function PairPartnerPane({ primarySymbol, timeframe }: Props) {
  const pairsQ = useQuery({
    queryKey: ["pair-partner", primarySymbol],
    queryFn:  pairsApi.active,
    refetchInterval: 5_000,
  });

  // Find a pair where primary is either Y or X
  const match = useMemo(() => {
    if (!pairsQ.data) return null;
    return pairsQ.data.pairs.find(
      (p) =>
        p.symbolY.toUpperCase() === primarySymbol.toUpperCase() ||
        p.symbolX.toUpperCase() === primarySymbol.toUpperCase()
    ) ?? null;
  }, [pairsQ.data, primarySymbol]);

  const partnerSymbol = useMemo(() => {
    if (!match) return null;
    return match.symbolY.toUpperCase() === primarySymbol.toUpperCase()
      ? match.symbolX
      : match.symbolY;
  }, [match, primarySymbol]);

  const partnerQ = useQuery({
    queryKey: ["pair-partner", "candles", partnerSymbol, timeframe],
    queryFn:  () => marketsApi.candles(partnerSymbol!, timeframe, 250),
    enabled:  !!partnerSymbol,
    refetchInterval: 30_000,
  });

  const containerRef = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ w: 600, h: 200 });
  useEffect(() => {
    const update = () => {
      if (!containerRef.current) return;
      setSize({ w: containerRef.current.clientWidth, h: containerRef.current.clientHeight });
    };
    update();
    const ro = new ResizeObserver(update);
    if (containerRef.current) ro.observe(containerRef.current);
    return () => ro.disconnect();
  }, []);

  if (!match || !partnerSymbol) {
    return (
      <div className="rounded-lg border border-white/[0.05] bg-black/85 p-3 text-center">
        <p className="text-[9px] font-mono uppercase tracking-widest text-muted-foreground/55">
          Pair Partner
        </p>
        <p className="text-[11px] italic font-serif text-muted-foreground/45 py-2">
          {primarySymbol} is not in any active cointegrated pair right now.
        </p>
      </div>
    );
  }

  const candles = partnerQ.data ?? [];
  const z = match.currentZ;
  const zAbs = Math.abs(z);
  const stateColor =
    match.state === "LongSpread" ? "text-emerald-300 border-emerald-400/30 bg-emerald-500/10" :
    match.state === "ShortSpread" ? "text-rose-300 border-rose-400/30 bg-rose-500/10" :
    match.state === "Stop" ? "text-amber-300 border-amber-400/30 bg-amber-500/10" :
    "text-muted-foreground/65 border-white/10 bg-white/[0.02]";

  return (
    <div className="rounded-lg border border-white/[0.05] bg-black overflow-hidden">
      {/* Header strip */}
      <div className="flex items-center justify-between gap-3 px-3 py-1.5 border-b border-white/[0.05] bg-white/[0.015]">
        <div className="flex items-center gap-3 text-[10px] font-mono">
          <span className="uppercase tracking-widest text-[9px] text-cyan-400/65">↪ Pair Partner</span>
          <span className="font-bold text-foreground/85">{partnerSymbol}</span>
          <span className="text-muted-foreground/45">paired with</span>
          <span className="font-bold text-foreground/65">{primarySymbol}</span>
        </div>
        <div className="flex items-center gap-2 text-[10px] font-mono">
          <Stat label="z" value={`${z >= 0 ? "+" : ""}${z.toFixed(2)}`} tone={zAbs >= 2 ? "emerald" : zAbs >= 1 ? "cyan" : "default"} />
          <Stat label="β" value={match.hedgeRatio.toFixed(2)} tone="default" />
          <Stat label="HL" value={`${match.halfLife.toFixed(1)}h`} tone="default" />
          <Stat label="ρ" value={match.correlation.toFixed(2)} tone="default" />
          <span className={cn("px-1.5 py-0.5 rounded border text-[9px] font-black uppercase tracking-widest", stateColor)}>
            {match.state}
          </span>
        </div>
      </div>

      {/* Partner chart */}
      <div ref={containerRef} className="relative" style={{ height: 200 }}>
        {partnerQ.isLoading ? (
          <p className="absolute inset-0 flex items-center justify-center text-[11px] italic text-muted-foreground/45">
            loading {partnerSymbol}…
          </p>
        ) : candles.length > 0 ? (
          <CandleChart
            candles={candles}
            width={size.w}
            height={size.h}
            compact
          />
        ) : (
          <p className="absolute inset-0 flex items-center justify-center text-[11px] italic text-muted-foreground/45">
            no data
          </p>
        )}
      </div>
    </div>
  );
}

function Stat({ label, value, tone }: { label: string; value: string; tone: "emerald" | "cyan" | "default" }) {
  const cls =
    tone === "emerald" ? "text-emerald-300" :
    tone === "cyan"    ? "text-cyan-300" :
                         "text-foreground/85";
  return (
    <span className="flex items-baseline gap-0.5">
      <span className="uppercase tracking-widest text-[9px] text-muted-foreground/45">{label}</span>
      <span className={cn("font-bold tabular-nums", cls)}>{value}</span>
    </span>
  );
}
