import { useEffect, useMemo, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { marketsApi, TIMEFRAMES, type Timeframe } from "@/api/markets";
import { CandleChart, type IndicatorBand, type IndicatorLine } from "@/components/charts/CandleChart";
import { Skeleton } from "@/components/ui/Skeleton";
import { sma, bollingerBands } from "@/lib/indicators";
import { useChartSync } from "@/lib/chartSync";
import { cn } from "@/lib/utils";

interface Props {
  symbol: string;
  timeframe: Timeframe;
  onSymbolClick?: (symbol: string) => void;
  onTimeframeChange?: (tf: Timeframe) => void;
  onSymbolChange?: (symbol: string) => void;
  onClose?: () => void;
  /** Primary pane shows extra header decoration. */
  primary?: boolean;
  showVolume?: boolean;
  showIndicators?: boolean;
  showVwap?: boolean;
  showVolumeProfile?: boolean;
  height?: number;
  /** Quick pickable list of recent symbols (e.g. watchlist). */
  symbolPicks?: string[];
}

/**
 * Compact, fully-self-contained chart pane for the multi-chart view.
 * Each pane fetches its own candles and manages its own timeframe.
 */
export function ChartPane({
  symbol,
  timeframe,
  onTimeframeChange,
  onSymbolChange,
  onSymbolClick,
  onClose,
  primary = false,
  showVolume = false,
  showIndicators = true,
  showVwap = false,
  showVolumeProfile = false,
  height = 260,
  symbolPicks,
}: Props) {
  const sync = useChartSync();
  const [editingSymbol, setEditingSymbol] = useState(false);
  const [draftSymbol, setDraftSymbol] = useState(symbol);
  const containerRef = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(400);

  useEffect(() => setDraftSymbol(symbol), [symbol]);

  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    const ro = new ResizeObserver(() => {
      setWidth(Math.max(280, el.clientWidth - 4));
    });
    ro.observe(el);
    setWidth(Math.max(280, el.clientWidth - 4));
    return () => ro.disconnect();
  }, []);

  const q = useQuery({
    queryKey: ["markets", "candles", symbol, timeframe],
    queryFn: () => marketsApi.candles(symbol, timeframe, 120),
    refetchInterval: 30_000,
    enabled: !!symbol,
  });
  const candles = q.data ?? [];
  const last = candles[candles.length - 1];
  const first = candles[0];
  const change = last && first ? ((last.close - first.open) / first.open) * 100 : 0;
  const positive = change >= 0;

  const closes = useMemo(() => candles.map((c) => c.close), [candles]);
  const indicators = useMemo(() => {
    if (!showIndicators) return { lines: [] as IndicatorLine[], bands: [] as IndicatorBand[] };
    const lines: IndicatorLine[] = [
      { values: sma(closes, 20), color: "#1E3A8A", width: 1.2, label: "MA20" },
    ];
    const bb = bollingerBands(closes, 20, 2);
    const bands: IndicatorBand[] = [
      {
        upper: bb.map((p) => p?.upper ?? null),
        lower: bb.map((p) => p?.lower ?? null),
        fill: "rgba(107, 106, 100, 0.5)",
      },
    ];
    return { lines, bands };
  }, [closes, showIndicators]);

  const commitSymbol = () => {
    setEditingSymbol(false);
    const sym = draftSymbol.trim().toUpperCase();
    if (sym && sym !== symbol) onSymbolChange?.(sym);
    else setDraftSymbol(symbol);
  };

  return (
    <div
      ref={containerRef}
      className={cn(
        "rounded-lg border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden flex flex-col",
        primary && "border-accent/30"
      )}
    >
      {/* Pane header */}
      <div className="px-3 py-2 border-b border-white/5 flex items-center justify-between gap-2 min-w-0">
        <div className="flex items-center gap-2 min-w-0">
          {editingSymbol ? (
            <input
              autoFocus
              value={draftSymbol}
              onChange={(e) => setDraftSymbol(e.target.value)}
              onBlur={commitSymbol}
              onKeyDown={(e) => {
                if (e.key === "Enter") commitSymbol();
                if (e.key === "Escape") {
                  setDraftSymbol(symbol);
                  setEditingSymbol(false);
                }
              }}
              className="bg-[#070A0F] border border-accent/30 rounded px-1.5 py-0.5 text-[12px] font-mono font-bold w-28"
            />
          ) : (
            <button
              onClick={() => {
                if (onSymbolClick) onSymbolClick(symbol);
                setEditingSymbol(true);
              }}
              title="Click to change symbol"
              className="font-bold text-[13px] hover:text-accent transition-colors truncate"
            >
              {symbol}
            </button>
          )}
          {last && (
            <>
              <span className="font-mono text-[11px] tabular-nums">
                {fmt(last.close)}
              </span>
              <span
                className={cn(
                  "font-mono text-[10px] tabular-nums font-bold",
                  positive ? "text-emerald-400" : "text-rose-400"
                )}
              >
                {positive ? "+" : ""}
                {change.toFixed(2)}%
              </span>
            </>
          )}
        </div>
        <div className="flex items-center gap-1 shrink-0">
          <select
            value={timeframe}
            onChange={(e) => onTimeframeChange?.(e.target.value as Timeframe)}
            className="bg-white/[0.04] border border-white/10 rounded px-1.5 py-0.5 text-[10px] font-mono cursor-pointer"
            aria-label="Timeframe"
          >
            {TIMEFRAMES.map((t) => (
              <option key={t.value} value={t.value}>
                {t.label}
              </option>
            ))}
          </select>
          {onClose && (
            <button
              onClick={onClose}
              aria-label={`Close ${symbol} pane`}
              className="w-5 h-5 rounded flex items-center justify-center text-muted-foreground/50 hover:text-foreground hover:bg-white/[0.05]"
            >
              <svg width="9" height="9" viewBox="0 0 10 10" fill="none">
                <path d="M2 2l6 6M8 2L2 8" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
              </svg>
            </button>
          )}
        </div>
      </div>

      {/* Quick symbol picks */}
      {editingSymbol && symbolPicks && symbolPicks.length > 0 && (
        <div className="px-3 py-1.5 border-b border-white/5 flex items-center gap-1 flex-wrap">
          {symbolPicks.slice(0, 8).map((s) => (
            <button
              key={s}
              onClick={() => {
                setDraftSymbol(s);
                onSymbolChange?.(s);
                setEditingSymbol(false);
              }}
              className="px-1.5 py-0.5 rounded text-[9px] font-mono border border-white/10 bg-white/[0.02] hover:bg-accent/15 hover:text-accent transition-colors"
            >
              {s}
            </button>
          ))}
        </div>
      )}

      {/* Chart body */}
      <div className="flex-1 px-1 py-1">
        {q.isLoading ? (
          <Skeleton className="w-full" style={{ height } as React.CSSProperties} />
        ) : candles.length === 0 ? (
          <p className="text-[10px] italic font-serif text-muted-foreground/60 text-center py-6">
            — no candles for {symbol} —
          </p>
        ) : (
          <CandleChart
            candles={candles}
            width={width}
            height={height}
            showVolume={showVolume}
            showVwap={showVwap}
            showVolumeProfile={showVolumeProfile}
            lines={indicators.lines}
            bands={indicators.bands}
            syncedBarIndex={sync?.hoveredIndex}
          />
        )}
      </div>
    </div>
  );
}

function fmt(v: number): string {
  if (v >= 1000) return v.toLocaleString(undefined, { maximumFractionDigits: 2 });
  if (v >= 1) return v.toFixed(2);
  return v.toFixed(5);
}
