import { useEffect, useMemo, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { marketsApi, TIMEFRAMES, type Timeframe, type MarketSymbol } from "@/api/markets";
import { dashboardApi } from "@/api/dashboard";
import { CandleChart, type IndicatorBand, type IndicatorLine } from "@/components/charts/CandleChart";
import { Skeleton } from "@/components/ui/Skeleton";
import { cn } from "@/lib/utils";
import { sma, ema, bollingerBands } from "@/lib/indicators";
import { RsiPane, MacdPane } from "@/components/charts/IndicatorPanes";
import {
  PositionContextStrip,
  positionOverlaysFor,
} from "@/components/markets/PositionContextStrip";
import { useChartDrawings } from "@/hooks/useChartDrawings";
import { drawingMode as toolModeStore, type ToolMode } from "@/lib/chartDrawings";

type IndicatorKey = "ma20" | "ma50" | "ema21" | "bb" | "rsi" | "macd" | "vwap" | "vp";

const INDICATORS: { key: IndicatorKey; label: string; color: string; pane?: "main" | "sub" }[] = [
  { key: "ma20", label: "MA 20", color: "#1E3A8A", pane: "main" },
  { key: "ma50", label: "MA 50", color: "#A05A00", pane: "main" },
  { key: "ema21", label: "EMA 21", color: "#3B8C68", pane: "main" },
  { key: "bb", label: "Bollinger", color: "#6B6A64", pane: "main" },
  { key: "vwap", label: "VWAP", color: "#0ea5e9", pane: "main" },
  { key: "vp", label: "Vol Profile", color: "#f59e0b", pane: "main" },
  { key: "rsi", label: "RSI", color: "#1E3A8A", pane: "sub" },
  { key: "macd", label: "MACD", color: "#1E3A8A", pane: "sub" },
];

interface Props {
  symbol: string;
  /** Optional snapshot from watchlist for the header (last price + 24h chg). */
  snapshot?: MarketSymbol;
}

export function MainSymbolChart({ symbol, snapshot }: Props) {
  const [tf, setTf] = useState<Timeframe>("1h");
  const [activeIndicators, setActiveIndicators] = useState<Set<IndicatorKey>>(
    () => new Set<IndicatorKey>(["ma20"])
  );
  const containerRef = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(900);

  const toggleIndicator = (k: IndicatorKey) =>
    setActiveIndicators((prev) => {
      const next = new Set(prev);
      if (next.has(k)) next.delete(k);
      else next.add(k);
      return next;
    });

  // Resize observer for responsive chart width
  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    const ro = new ResizeObserver(() => {
      setWidth(Math.max(320, el.clientWidth - 4));
    });
    ro.observe(el);
    setWidth(Math.max(320, el.clientWidth - 4));
    return () => ro.disconnect();
  }, []);

  const q = useQuery({
    queryKey: ["markets", "candles", symbol, tf],
    queryFn: () => marketsApi.candles(symbol, tf, 200),
    refetchInterval: 30_000,
    enabled: !!symbol,
  });

  const candles = q.data ?? [];
  const last = candles[candles.length - 1];

  // Open-position overlay (if any)
  const positionsQ = useQuery({
    queryKey: ["positions"],
    queryFn: () => dashboardApi.positions(),
    refetchInterval: 5_000,
  });
  const positionOverlays = useMemo(
    () => positionOverlaysFor(symbol, positionsQ.data),
    [symbol, positionsQ.data]
  );

  // User-drawn levels
  const { drawings, overlays: drawingOverlays } = useChartDrawings(symbol);
  const allOverlays = useMemo(
    () => [...positionOverlays, ...drawingOverlays],
    [positionOverlays, drawingOverlays]
  );

  // Trendlines
  const trendlines = useMemo(
    () =>
      drawings
        .filter((d): d is Extract<(typeof drawings)[number], { kind: "trendline" }> => d.kind === "trendline")
        .map((d) => ({
          fromTime: d.fromTime,
          fromPrice: d.fromPrice,
          toTime: d.toTime,
          toPrice: d.toPrice,
          color: d.color,
        })),
    [drawings]
  );

  // Drawing tool mode (from store)
  const [tool, setTool] = useState<ToolMode>(() => toolModeStore.get());
  const [pendingAnchor, setPendingAnchor] = useState(toolModeStore.pendingAnchor());
  useEffect(
    () =>
      toolModeStore.subscribe((mode, anchor) => {
        setTool(mode);
        setPendingAnchor(anchor);
      }),
    []
  );

  // Cancel drawing on Escape
  useEffect(() => {
    if (tool === "none") return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") toolModeStore.cancel();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [tool]);

  // Compute indicators from candle closes (memoized — only recompute when candles change)
  const closes = useMemo(() => candles.map((c) => c.close), [candles]);
  const indicators = useMemo(() => {
    const lines: IndicatorLine[] = [];
    const bands: IndicatorBand[] = [];

    if (activeIndicators.has("bb")) {
      const bb = bollingerBands(closes, 20, 2);
      bands.push({
        upper: bb.map((p) => p?.upper ?? null),
        lower: bb.map((p) => p?.lower ?? null),
        fill: "rgba(107, 106, 100, 0.5)",
        strokeColor: "#6B6A64",
        label: "BB(20,2)",
      });
    }
    if (activeIndicators.has("ma20")) {
      lines.push({ values: sma(closes, 20), color: "#1E3A8A", width: 1.4, label: "MA 20" });
    }
    if (activeIndicators.has("ma50")) {
      lines.push({ values: sma(closes, 50), color: "#A05A00", width: 1.4, label: "MA 50" });
    }
    if (activeIndicators.has("ema21")) {
      lines.push({ values: ema(closes, 21), color: "#3B8C68", width: 1.4, dash: true, label: "EMA 21" });
    }
    return { lines, bands };
  }, [closes, activeIndicators]);

  // Header: prefer live last from candles, fall back to snapshot
  const lastPrice = last ? last.close : snapshot?.lastPrice ?? 0;
  const change = snapshot?.changePct24h ?? 0;
  const positive = change >= 0;

  // Mini summary stats from current viewport
  const summary = (() => {
    if (candles.length < 2) return null;
    const closes = candles.map((c) => c.close);
    const open = candles[0].open;
    const high = Math.max(...candles.map((c) => c.high));
    const low = Math.min(...candles.map((c) => c.low));
    const periodChange = closes[closes.length - 1] - open;
    const periodChangePct = (periodChange / open) * 100;
    const totalVol = candles.reduce((s, c) => s + c.volume, 0);
    return { open, high, low, periodChangePct, totalVol };
  })();

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      {/* Header */}
      <div className="px-5 py-3.5 border-b border-white/5 flex items-start justify-between gap-4 flex-wrap">
        <div className="min-w-0">
          <div className="flex items-baseline gap-3">
            <h2 className="font-serif text-[26px] tracking-tight text-foreground leading-none">
              {symbol}
            </h2>
            <span className="font-mono text-[11px] uppercase tracking-widest text-muted-foreground/60">
              · Crypto · USD-pair
            </span>
          </div>
          <div className="flex items-baseline gap-3 mt-2">
            <span className="font-mono font-bold text-[22px] tabular-nums">
              {lastPrice >= 1000
                ? lastPrice.toLocaleString(undefined, { maximumFractionDigits: 2 })
                : lastPrice >= 1
                  ? lastPrice.toFixed(2)
                  : lastPrice.toFixed(5)}
            </span>
            {snapshot && (
              <span
                className={cn(
                  "font-mono text-sm font-bold tabular-nums",
                  positive ? "text-emerald-400" : "text-rose-400"
                )}
              >
                {positive ? "+" : ""}
                {change.toFixed(2)}% 24h
              </span>
            )}
          </div>
        </div>

        {/* Timeframe selector */}
        <div className="flex items-center gap-1" role="group" aria-label="Timeframe">
          {TIMEFRAMES.map((t) => (
            <button
              key={t.value}
              onClick={() => setTf(t.value)}
              aria-pressed={tf === t.value}
              className={cn(
                "px-2.5 py-1 rounded text-[10px] font-mono font-bold tracking-wider transition-colors",
                tf === t.value
                  ? "bg-accent/15 text-accent border border-accent/30"
                  : "text-muted-foreground/60 hover:text-foreground hover:bg-white/[0.04] border border-transparent"
              )}
            >
              {t.label}
            </button>
          ))}
        </div>
      </div>

      {/* Period summary (O/H/L/Δ/Vol) */}
      {summary && (
        <div className="px-5 py-2.5 border-b border-white/5 flex flex-wrap items-center gap-x-6 gap-y-1 text-[10px] font-mono">
          <Stat label="O" value={fmtPrice(summary.open)} />
          <Stat label="H" value={fmtPrice(summary.high)} tone="pos" />
          <Stat label="L" value={fmtPrice(summary.low)} tone="neg" />
          <Stat
            label="Δ%"
            value={`${summary.periodChangePct >= 0 ? "+" : ""}${summary.periodChangePct.toFixed(2)}%`}
            tone={summary.periodChangePct >= 0 ? "pos" : "neg"}
          />
          <Stat label="VOL" value={fmtVol(summary.totalVol)} />
          <Stat label="Bars" value={candles.length.toString()} />
        </div>
      )}

      {/* Indicator toggle bar */}
      <div className="px-5 py-2 border-b border-white/5 flex items-center gap-1.5 flex-wrap">
        <span className="text-[8px] font-black uppercase tracking-widest text-muted-foreground/45 mr-1">
          Indicators
        </span>
        {INDICATORS.map((ind) => {
          const on = activeIndicators.has(ind.key);
          return (
            <button
              key={ind.key}
              onClick={() => toggleIndicator(ind.key)}
              aria-pressed={on}
              className={cn(
                "flex items-center gap-1.5 px-2 py-0.5 rounded text-[10px] font-mono transition-colors border",
                on
                  ? "bg-white/[0.04] border-white/10 text-foreground"
                  : "border-transparent text-muted-foreground/50 hover:text-foreground hover:bg-white/[0.03]"
              )}
            >
              <span
                className="w-2 h-2 rounded-full"
                style={{
                  backgroundColor: on ? ind.color : "transparent",
                  border: on ? "none" : `1px dashed ${ind.color}`,
                  opacity: on ? 1 : 0.5,
                }}
              />
              {ind.label}
            </button>
          );
        })}
        {activeIndicators.size > 0 && (
          <button
            onClick={() => setActiveIndicators(new Set())}
            className="ml-auto text-[9px] uppercase tracking-widest text-muted-foreground/40 hover:text-muted-foreground"
          >
            clear
          </button>
        )}
      </div>

      {/* Drawing-mode banner */}
      {tool !== "none" && (
        <div className="px-5 py-1.5 border-b border-accent/20 bg-accent/[0.06] flex items-center gap-2 text-[10px] font-mono">
          <span className="w-1.5 h-1.5 rounded-full bg-accent animate-pulse" />
          <span className="text-accent font-bold uppercase tracking-widest">
            {tool === "horizontal" ? "horizontal line" : "trendline"}
            {tool === "trendline" && pendingAnchor && " · second anchor"}
          </span>
          <span className="text-muted-foreground italic font-serif">
            {tool === "horizontal"
              ? "click chart to place"
              : pendingAnchor
                ? "click again to complete"
                : "click chart for first anchor"}
            {" · Esc cancels"}
          </span>
          <button
            onClick={() => toolModeStore.cancel()}
            className="ml-auto text-[9px] uppercase tracking-widest text-muted-foreground hover:text-rose-400 transition-colors"
          >
            cancel
          </button>
        </div>
      )}

      {/* Position context (only renders if there's an open position on this symbol) */}
      <PositionContextStrip symbol={symbol} />

      {/* Chart body */}
      <div ref={containerRef} className="px-2 py-3">
        {q.isLoading ? (
          <div className="space-y-2 px-3 py-4" style={{ minHeight: 360 }}>
            <Skeleton className="h-4 w-32" />
            <Skeleton className="h-[320px] w-full" />
          </div>
        ) : candles.length === 0 ? (
          <p className="px-4 py-12 text-center text-xs italic font-serif text-muted-foreground">
            — no candles available for {symbol} —
          </p>
        ) : (
          <>
            <CandleChart
              candles={candles}
              width={width}
              height={400}
              showVolume
              showVwap={activeIndicators.has("vwap")}
              showVolumeProfile={activeIndicators.has("vp")}
              lines={indicators.lines}
              bands={indicators.bands}
              overlays={allOverlays}
              trendlines={trendlines}
              drawingMode={tool}
              pendingAnchor={pendingAnchor}
              onChartClick={(time, price) =>
                toolModeStore.handleChartClick(symbol, time, price)
              }
            />
            {/* Sub-panes — toggled via INDICATORS bar */}
            {activeIndicators.has("rsi") && (
              <div className="border-t border-white/5 mt-1 pt-1">
                <RsiPane closes={closes} width={width} />
              </div>
            )}
            {activeIndicators.has("macd") && (
              <div className="border-t border-white/5 mt-1 pt-1">
                <MacdPane closes={closes} width={width} />
              </div>
            )}
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
}: {
  label: string;
  value: string;
  tone?: "pos" | "neg" | "neutral";
}) {
  return (
    <span className="flex items-baseline gap-1.5">
      <span className="text-[8px] uppercase tracking-widest text-muted-foreground/50">{label}</span>
      <span
        className={cn(
          "tabular-nums font-bold",
          tone === "pos" && "text-emerald-400",
          tone === "neg" && "text-rose-400"
        )}
      >
        {value}
      </span>
    </span>
  );
}

function fmtPrice(v: number): string {
  if (v >= 1000) return v.toLocaleString(undefined, { maximumFractionDigits: 2 });
  if (v >= 1) return v.toFixed(2);
  return v.toFixed(5);
}

function fmtVol(v: number): string {
  if (v >= 1_000_000) return `${(v / 1_000_000).toFixed(1)}M`;
  if (v >= 1_000) return `${(v / 1_000).toFixed(1)}k`;
  return v.toFixed(0);
}
