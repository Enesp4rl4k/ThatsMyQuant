import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { marketsApi } from "@/api/markets";
import { Sparkline } from "@/components/charts/Sparkline";
import { cn } from "@/lib/utils";

interface Props {
  symbol: string;
}

/**
 * Compact metadata card — derives from the candle endpoint:
 *   - 24h: volume, range, current vs hi/lo
 *   - 7d:  range, current vs hi/lo, mini sparkline
 *   - momentum: % distance from week mid
 */
export function SymbolDetailCard({ symbol }: Props) {
  const dayQ = useQuery({
    queryKey: ["markets", "candles", symbol, "1h", 24],
    queryFn: () => marketsApi.candles(symbol, "1h", 24),
    refetchInterval: 60_000,
    enabled: !!symbol,
  });

  const weekQ = useQuery({
    queryKey: ["markets", "candles", symbol, "1d", 7],
    queryFn: () => marketsApi.candles(symbol, "1d", 7),
    refetchInterval: 5 * 60_000,
    enabled: !!symbol,
  });

  const dayStats = useMemo(() => {
    const c = dayQ.data;
    if (!c || c.length < 2) return null;
    const last = c[c.length - 1].close;
    const open = c[0].open;
    const high = Math.max(...c.map((x) => x.high));
    const low = Math.min(...c.map((x) => x.low));
    const vol = c.reduce((s, x) => s + x.volume, 0);
    const rangePct = high > 0 ? ((high - low) / high) * 100 : 0;
    const fromHi = high > 0 ? ((last - high) / high) * 100 : 0;
    const fromLo = low > 0 ? ((last - low) / low) * 100 : 0;
    const chgPct = open > 0 ? ((last - open) / open) * 100 : 0;
    return { last, open, high, low, vol, rangePct, fromHi, fromLo, chgPct };
  }, [dayQ.data]);

  const weekStats = useMemo(() => {
    const c = weekQ.data;
    if (!c || c.length < 2) return null;
    const last = c[c.length - 1].close;
    const open = c[0].open;
    const high = Math.max(...c.map((x) => x.high));
    const low = Math.min(...c.map((x) => x.low));
    const mid = (high + low) / 2;
    const fromMid = mid > 0 ? ((last - mid) / mid) * 100 : 0;
    const chgPct = open > 0 ? ((last - open) / open) * 100 : 0;
    return {
      last,
      open,
      high,
      low,
      mid,
      fromMid,
      chgPct,
      closes: c.map((x) => x.close),
    };
  }, [weekQ.data]);

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      <div className="px-4 py-2.5 border-b border-white/5">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
          {symbol}
        </p>
        <p className="text-[11px] font-serif italic text-muted-foreground/60">
          range &amp; momentum context
        </p>
      </div>

      <div className="p-4 space-y-4">
        {/* 24h block */}
        <section>
          <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/50 mb-1.5">
            24 hours
          </p>
          {dayStats ? (
            <>
              <div className="grid grid-cols-2 gap-2 text-[11px] font-mono">
                <Cell label="Range" value={`${dayStats.rangePct.toFixed(2)}%`} />
                <Cell label="Volume" value={fmtVol(dayStats.vol)} />
                <Cell label="High" value={fmtPrice(dayStats.high)} tone="pos" />
                <Cell label="Low" value={fmtPrice(dayStats.low)} tone="neg" />
              </div>
              {/* Position-within-range bar */}
              <RangeBar
                low={dayStats.low}
                high={dayStats.high}
                last={dayStats.last}
                className="mt-3"
                fromHi={dayStats.fromHi}
                fromLo={dayStats.fromLo}
              />
            </>
          ) : (
            <p className="text-[10px] italic font-serif text-muted-foreground/50">— loading —</p>
          )}
        </section>

        {/* 7d block */}
        <section className="pt-3 border-t border-white/5">
          <div className="flex items-baseline justify-between mb-1.5">
            <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/50">
              7 days
            </p>
            {weekStats && (
              <span
                className={cn(
                  "font-mono text-[11px] font-bold tabular-nums",
                  weekStats.chgPct >= 0 ? "text-emerald-400" : "text-rose-400"
                )}
              >
                {weekStats.chgPct >= 0 ? "+" : ""}
                {weekStats.chgPct.toFixed(2)}%
              </span>
            )}
          </div>
          {weekStats ? (
            <>
              <div className="grid grid-cols-2 gap-2 text-[11px] font-mono">
                <Cell label="High" value={fmtPrice(weekStats.high)} tone="pos" />
                <Cell label="Low" value={fmtPrice(weekStats.low)} tone="neg" />
                <Cell label="Mid" value={fmtPrice(weekStats.mid)} />
                <Cell
                  label="vs Mid"
                  value={`${weekStats.fromMid >= 0 ? "+" : ""}${weekStats.fromMid.toFixed(2)}%`}
                  tone={weekStats.fromMid >= 0 ? "pos" : "neg"}
                />
              </div>
              <div className="mt-3">
                <Sparkline
                  values={weekStats.closes}
                  width={240}
                  height={36}
                  stroke={
                    weekStats.chgPct >= 0
                      ? "rgba(45, 106, 79, 0.85)"
                      : "rgba(181, 54, 75, 0.85)"
                  }
                  strokeWidth={1.4}
                />
              </div>
            </>
          ) : (
            <p className="text-[10px] italic font-serif text-muted-foreground/50">— loading —</p>
          )}
        </section>
      </div>
    </div>
  );
}

function RangeBar({
  low,
  high,
  last,
  fromHi,
  fromLo,
  className,
}: {
  low: number;
  high: number;
  last: number;
  fromHi: number;
  fromLo: number;
  className?: string;
}) {
  const pct = high > low ? ((last - low) / (high - low)) * 100 : 50;
  return (
    <div className={className}>
      <div className="relative h-2 rounded-full bg-white/5 overflow-hidden">
        <div
          className="absolute inset-y-0 left-0 bg-gradient-to-r from-rose-500/30 via-amber-500/20 to-emerald-500/30"
          style={{ width: "100%" }}
        />
        <div
          className="absolute top-0 bottom-0 w-1 rounded-full bg-foreground transition-all"
          style={{ left: `calc(${Math.max(0, Math.min(100, pct))}% - 2px)` }}
        />
      </div>
      <div className="flex justify-between mt-1 text-[9px] font-mono text-muted-foreground/50">
        <span className="text-rose-400/70">{fromLo >= 0 ? "+" : ""}{fromLo.toFixed(1)}% from low</span>
        <span className="text-emerald-400/70">{fromHi >= 0 ? "+" : ""}{fromHi.toFixed(1)}% from high</span>
      </div>
    </div>
  );
}

function Cell({
  label,
  value,
  tone = "neutral",
}: {
  label: string;
  value: string;
  tone?: "pos" | "neg" | "neutral";
}) {
  return (
    <div>
      <p className="text-[8px] uppercase tracking-widest text-muted-foreground/50 mb-0.5">{label}</p>
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

function fmtPrice(v: number): string {
  if (v >= 1000) return v.toLocaleString(undefined, { maximumFractionDigits: 2 });
  if (v >= 1) return v.toFixed(2);
  return v.toFixed(5);
}

function fmtVol(v: number): string {
  if (v >= 1_000_000_000) return `${(v / 1_000_000_000).toFixed(2)}B`;
  if (v >= 1_000_000) return `${(v / 1_000_000).toFixed(2)}M`;
  if (v >= 1_000) return `${(v / 1_000).toFixed(2)}k`;
  return v.toFixed(0);
}
