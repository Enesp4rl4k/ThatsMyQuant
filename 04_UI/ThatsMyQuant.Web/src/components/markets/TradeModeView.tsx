import { useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { marketsApi, TIMEFRAMES, type Timeframe } from "@/api/markets";
import { dashboardApi } from "@/api/dashboard";
import { proModeApi } from "@/api/proMode";
import { CandleChart, type ChartAnnotation, type TrendlineSegment } from "@/components/charts/CandleChart";
import { OrderBookPanel } from "@/components/markets/OrderBookPanel";
import { BotStatusStrip } from "@/components/markets/BotStatusStrip";
import { AlertTicker } from "@/components/markets/AlertTicker";
import { RiskMeterPanel } from "@/components/markets/RiskMeterPanel";
import { TimeSalesTape } from "@/components/markets/TimeSalesTape";
import { AICommentaryPanel } from "@/components/markets/AICommentaryPanel";
import { PositionChipStrip } from "@/components/markets/PositionChipStrip";
import { BotDiscoveriesPanel } from "@/components/markets/BotDiscoveriesPanel";
import { TradePlanCard } from "@/components/markets/TradePlanCard";
import { SessionStatsBadge } from "@/components/markets/SessionStatsBadge";
import { WatchlistQuickSwitcher } from "@/components/markets/WatchlistQuickSwitcher";
import { IndicatorOverlayPicker, useIndicatorOverlays, type IndicatorKey } from "@/components/markets/IndicatorOverlayPicker";
import { PairPartnerPane } from "@/components/markets/PairPartnerPane";
import { InlineBacktestButton } from "@/components/markets/InlineBacktestButton";
import { useLiquidityBands } from "@/hooks/useLiquidityBands";
import { priceAlertStore } from "@/lib/priceAlerts";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";
import { pushNotification } from "@/lib/notifications";

/**
 * Sierra-Chart-style immersive Trade Mode. A full-viewport overlay rendered
 * over the entire app shell: pure black background, big candle chart in the
 * centre with bot trade annotations (entry arrows, exit dots, pending
 * triangles, Entry/SL/TP horizontal lines), order book + volume profile on
 * the right, and a compact bottom strip with open positions.
 *
 * Toggled by Markets page (or future global hotkey). Pulls annotation data
 * from existing /api/positions, /api/trades, /api/signals/recent — filtered
 * client-side by symbol.
 *
 * Esc and the ✕ button dismiss the overlay. While open, body scroll is
 * locked and the layout escapes the AppShell via React Portal-style fixed
 * inset-0 positioning at z-[80].
 */

interface Props {
  symbol: string;
  initialTimeframe?: Timeframe;
  onClose: () => void;
}

export function TradeModeView({ symbol: initialSymbol, initialTimeframe = "1h", onClose }: Props) {
  const [symbol, setSymbol]       = useState(initialSymbol);
  const [timeframe, setTimeframe] = useState<Timeframe>(initialTimeframe);
  const [showAnnotations, setShowAnnotations] = useState(true);
  const [showLevels, setShowLevels] = useState(true);
  // Trade Mode default: chart is the main focus. All right-stack side panels start hidden so
  // the candle chart claims the full width. User opens what they want via toolbar toggles.
  const [showOrderBook, setShowOrderBook] = useState(false);
  const [showVolumeProfile, setShowVolumeProfile] = useState(true);
  const [showLivePrice, setShowLivePrice] = useState(true);
  const [showRegimeTint, setShowRegimeTint] = useState(true);
  const [showLiquidity, setShowLiquidity] = useState(false);
  const [showAi, setShowAi] = useState(false);
  const [showTape, setShowTape] = useState(false);
  const [showGrid, setShowGrid] = useState(true);
  const [alertMode, setAlertMode] = useState(false);
  const [compareSymbol, setCompareSymbol] = useState<string | null>(null);
  const [soundOn, setSoundOn] = useState(true);

  // ── Chart zoom + pan ──────────────────────────────────────────────────────
  // zoomFactor: 1.0 = show all candles, 0.05 = show only ~5% (deep zoom in)
  // panOffset:  0   = newest tail visible, +N = window shifted N candles older
  const [zoomFactor, setZoomFactor] = useState(1.0);
  const [panOffset,  setPanOffset]  = useState(0);
  const isPanningRef = useRef<{ x: number; pan: number } | null>(null);

  // ── Drawing tools (horizontal levels + trendlines) ────────────────────────
  type DrawMode = "none" | "horizontal" | "trendline";
  const [drawMode, setDrawMode] = useState<DrawMode>("none");
  const [drawnHorizontals, setDrawnHorizontals] = useState<{ price: number; color: string; label: string; dash?: boolean }[]>([]);
  const [drawnTrendlines,  setDrawnTrendlines]  = useState<TrendlineSegment[]>([]);
  const [pendingAnchor,    setPendingAnchor]    = useState<{ time: string; price: number } | null>(null);

  // ── Indicator overlays + pair pane + plan card ───────────────────────────
  const [activeIndicators, setActiveIndicators] = useState<Set<IndicatorKey>>(new Set());
  const [showPairPane, setShowPairPane] = useState(false);
  const [showTradePlan, setShowTradePlan] = useState(false);

  const liquidityBands = useLiquidityBands(showLiquidity ? symbol : undefined);

  // Sound feedback on position close (SignalR push). Uses Web Audio API for a
  // tiny "blip" (no asset to ship). Silenced when soundOn=false.
  useEffect(() => {
    if (!soundOn) return;
    let cancelled = false;
    let audioCtx: AudioContext | null = null;
    const play = (freq: number, dur = 0.12) => {
      try {
        if (cancelled) return;
        if (!audioCtx) audioCtx = new (window.AudioContext || (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext)();
        const osc = audioCtx.createOscillator();
        const gain = audioCtx.createGain();
        osc.frequency.value = freq;
        osc.type = "sine";
        gain.gain.setValueAtTime(0.0001, audioCtx.currentTime);
        gain.gain.exponentialRampToValueAtTime(0.18, audioCtx.currentTime + 0.01);
        gain.gain.exponentialRampToValueAtTime(0.0001, audioCtx.currentTime + dur);
        osc.connect(gain).connect(audioCtx.destination);
        osc.start();
        osc.stop(audioCtx.currentTime + dur + 0.05);
      } catch { /* silent */ }
    };

    import("@/lib/signalr").then(({ getHubConnection }) => {
      const conn = getHubConnection();
      const onClose = (e: { netPnl?: number; symbol?: string }) => {
        // 880 Hz cheerful for win, 220 Hz solemn for loss
        play(e.netPnl != null && e.netPnl >= 0 ? 880 : 220);
      };
      const onSignal = () => play(660, 0.08);
      conn.on("PositionClosed", onClose);
      conn.on("OperatorAlert",  onSignal);
      return () => {
        conn.off("PositionClosed", onClose);
        conn.off("OperatorAlert",  onSignal);
      };
    });
    return () => { cancelled = true; };
  }, [soundOn]);

  const qc = useQueryClient();
  const flatAllM = useMutation({
    mutationFn: (reason: string) => dashboardApi.flattenAll(reason),
    onSuccess: (res) => {
      pushNotification({
        type: "warning",
        title: "🛑 FLAT ALL queued",
        body: `Request #${res.id}. Worker flattens all open positions within ~3s.`,
      });
      setTimeout(() => {
        qc.invalidateQueries({ queryKey: ["positions"] });
        qc.invalidateQueries({ queryKey: ["portfolio"] });
      }, 4_000);
    },
    onError: (err: unknown) => {
      pushNotification({
        type: "error",
        title: "FLAT ALL failed",
        body: err instanceof Error ? err.message : String(err),
      });
    },
  });

  // Lock body scroll + add `trade-mode` class for any global CSS hooks
  useEffect(() => {
    const prev = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    document.documentElement.classList.add("trade-mode");
    const onKey = (e: KeyboardEvent) => {
      const tag = (e.target as HTMLElement | null)?.tagName?.toLowerCase();
      const inField = tag === "input" || tag === "textarea" || tag === "select" || (e.target as HTMLElement | null)?.isContentEditable;
      if (e.key === "Escape") {
        e.preventDefault();
        onClose();
        return;
      }
      // [ and ] cycle between open positions when not in an input field
      if (!inField && (e.key === "[" || e.key === "]")) {
        e.preventDefault();
        document.dispatchEvent(new CustomEvent("tmq:cycle-position", { detail: { dir: e.key === "]" ? 1 : -1 } }));
      }
    };
    document.addEventListener("keydown", onKey);
    return () => {
      document.body.style.overflow = prev;
      document.documentElement.classList.remove("trade-mode");
      document.removeEventListener("keydown", onKey);
    };
  }, [onClose]);

  // ── Data sources ──────────────────────────────────────────────────────────
  // Trade Mode default to a deep history (1500 bars) so dragging the chart left
  // reveals real older context — not just a 250-bar window. Backend caches per
  // (symbol, timeframe) so the wider window is cheap on subsequent fetches.
  const [historyDepth, setHistoryDepth] = useState(1500);
  const candlesQ = useQuery({
    queryKey: ["trade-mode", "candles", symbol, timeframe, historyDepth],
    queryFn:  () => marketsApi.candles(symbol, timeframe, historyDepth),
    refetchInterval: 15_000,
  });

  const symbolsQ = useQuery({
    queryKey: ["trade-mode", "symbols"],
    queryFn:  () => marketsApi.symbols(),
    staleTime: 60_000,
  });

  const positionsQ = useQuery({
    queryKey: ["positions"],
    queryFn:  () => dashboardApi.positions(),
    refetchInterval: 5_000,
  });

  // Funding-strategy state-file positions (no DB row), so chart overlays + R:R
  // zones still show even though hybrid executors don't write to ITradeRepository.
  // Derives SL/TP per side using the worker's default 1.5% / 2% bracket. When
  // hybrid executors start writing to DB this query becomes redundant — until then
  // it's the bridge that keeps the chart's open-position overlays accurate.
  const paperStateQ = useQuery({
    queryKey: ["paper-state-positions"],
    queryFn:  dashboardApi.paperStatePositions,
    refetchInterval: 5_000,
  });

  // [ / ] keyboard navigation between open positions — listens for the custom
  // "tmq:cycle-position" event dispatched from the global key handler.
  useEffect(() => {
    const onCycle = (e: Event) => {
      const dir = (e as CustomEvent<{ dir: number }>).detail?.dir ?? 1;
      const all = positionsQ.data ?? [];
      const symbols = Array.from(new Set(all.map((p) => p.symbol.toUpperCase()))).sort();
      if (symbols.length === 0) return;
      const cur = symbol.toUpperCase();
      const idx = symbols.indexOf(cur);
      const next = idx >= 0
        ? symbols[(idx + dir + symbols.length) % symbols.length]
        : symbols[0];
      setSymbol(next);
    };
    document.addEventListener("tmq:cycle-position", onCycle);
    return () => document.removeEventListener("tmq:cycle-position", onCycle);
  }, [positionsQ.data, symbol]);

  const tradesQ = useQuery({
    queryKey: ["trades", 200],
    queryFn:  () => dashboardApi.recentTrades(200),
    refetchInterval: 30_000,
  });

  const signalsQ = useQuery({
    queryKey: ["signals", "recent", 50],
    queryFn:  () => dashboardApi.recentSignals(50),
    refetchInterval: 10_000,
  });

  const proQ = useQuery({
    queryKey: ["pro-snapshot"],
    queryFn:  proModeApi.snapshot,
    refetchInterval: 10_000,
  });

  const candlesAll = candlesQ.data ?? [];
  // Zoom + pan window: slice candlesAll to the visible range driven by
  // zoomFactor (how many bars visible) + panOffset (how far back from tail).
  const candles = useMemo(() => {
    const total = candlesAll.length;
    if (total === 0) return candlesAll;
    const minVisible = 20;
    const visible = Math.max(minVisible, Math.floor(total * zoomFactor));
    const end = Math.max(visible, total - panOffset);
    const start = Math.max(0, end - visible);
    return candlesAll.slice(start, Math.min(total, end));
  }, [candlesAll, zoomFactor, panOffset]);

  // Reset zoom/pan when symbol or timeframe changes
  useEffect(() => { setZoomFactor(1.0); setPanOffset(0); }, [symbol, timeframe]);

  // Indicator overlays computed from the visible candles slice
  const indicatorOverlays = useIndicatorOverlays(candles, activeIndicators);

  const SYM     = symbol.toUpperCase();
  const livePrice = candles.length > 0 ? candles[candles.length - 1].close : 0;

  // Compare symbol — second candles series normalized as a ghost line
  const compareQ = useQuery({
    queryKey: ["trade-mode", "candles", compareSymbol, timeframe],
    queryFn:  () => marketsApi.candles(compareSymbol!, timeframe, 250),
    enabled:  !!compareSymbol,
    refetchInterval: 30_000,
  });
  const compareLine = useMemo(() => {
    if (!compareSymbol || !compareQ.data || candles.length === 0 || compareQ.data.length === 0) return null;
    const cmp = compareQ.data;
    // Align lengths to current (replay-truncated) primary
    const len = Math.min(candles.length, cmp.length);
    if (len < 2) return null;
    const primaryStart = candles[0].close;
    const compareStart = cmp[0].close;
    if (primaryStart <= 0 || compareStart <= 0) return null;
    // Each compare close → ratio × primaryStart so it overlays in the same price band
    const values: (number | null)[] = candles.map((_, i) => {
      const cmpC = cmp[i]?.close;
      if (!cmpC || cmpC <= 0) return null;
      return (cmpC / compareStart) * primaryStart;
    });
    return {
      values,
      color: "#A78BFA",
      width: 1.4,
      dash: true,
      label: `${compareSymbol} ghost`,
    };
  }, [compareSymbol, compareQ.data, candles]);

  // Map regime label → ChartTint
  const regimeTint = useMemo(() => {
    const r = (proQ.data?.currentRegime ?? "").toLowerCase();
    if (r.includes("trending") && r.includes("up"))   return "trending-up"   as const;
    if (r.includes("trending") && r.includes("down")) return "trending-down" as const;
    if (r.includes("volatile"))                       return "volatile"      as const;
    if (r.includes("ranging"))                        return "ranging"       as const;
    return null;
  }, [proQ.data?.currentRegime]);

  // Filter datasets to the current symbol
  const symbolPositions = useMemo(() => {
    const dbRows = (positionsQ.data ?? []).filter((p) => p.symbol.toUpperCase() === SYM);
    if (dbRows.length > 0) return dbRows;
    // Fallback: build pseudo-Position rows from worker state files so overlays show.
    // SL/TP defaults match the funding-strategy bracket (1.5% loss / 2% profit).
    const stateRows = (paperStateQ.data?.positions ?? []).filter(
      (p) => p.symbol.toUpperCase() === SYM
    );
    return stateRows.map((p) => {
      const isLong = p.side === "LONG";
      const sl = isLong ? p.entryPrice * (1 - 0.015) : p.entryPrice * (1 + 0.015);
      const tp = isLong ? p.entryPrice * (1 + 0.02)  : p.entryPrice * (1 - 0.02);
      return {
        id: p.clientOrderId ?? `state-${p.symbol}-${p.entryTimeUtc}`,
        symbol: p.symbol,
        strategyTag: p.tier,
        side: isLong ? "Buy" : "Sell",
        entryPrice: p.entryPrice,
        currentPrice: 0,
        quantity: p.quantity,
        stopLoss: sl,
        takeProfit: tp,
        unrealizedPnL: 0,
        realizedPnL: 0,
        entryTime: p.entryTimeUtc,
        executionMode: p.executionMode,
        promotionStage: "Paper",
        regimeTag: "FundingMeanReversion",
        confidence: 75,
        sizeQuoteUsd: p.entryPrice * p.quantity,
      } as unknown as (typeof dbRows)[number];
    });
  }, [positionsQ.data, paperStateQ.data, SYM]);
  const symbolTrades = useMemo(
    () => (tradesQ.data ?? []).filter((t) => t.symbol.toUpperCase() === SYM),
    [tradesQ.data, SYM]
  );
  const symbolSignals = useMemo(
    () =>
      (signalsQ.data ?? []).filter(
        (s) =>
          s.symbol.toUpperCase() === SYM &&
          (s.status === "Pending" || s.status === "Queued")
      ),
    [signalsQ.data, SYM]
  );

  // ── Annotations: closed-trade entries + exits, open-position entries, pending entries ──
  const annotations: ChartAnnotation[] = useMemo(() => {
    if (!candles.length) return [];
    const out: ChartAnnotation[] = [];
    const firstTime = candles[0].time;

    // Closed trades — entry arrow + exit dot
    for (const t of symbolTrades) {
      if (t.entryTime && t.entryTime >= firstTime) {
        out.push({
          time:  t.entryTime,
          price: t.entryPrice,
          side:  t.side === "LONG" ? "long" : "short",
          kind:  "entry",
          label: `${t.side} entry $${t.entryPrice.toFixed(2)} · ${t.strategyTag}`,
        });
      }
      if (t.exitTime && t.exitTime >= firstTime && t.exitPrice > 0) {
        out.push({
          time:   t.exitTime,
          price:  t.exitPrice,
          side:   t.side === "LONG" ? "long" : "short",
          kind:   "exit",
          status: t.realizedPnL > 0 ? "win" : t.realizedPnL < 0 ? "loss" : "open",
          label:  `Exit $${t.exitPrice.toFixed(2)} · ${formatUsd(t.realizedPnL)} · ${t.exitReason || "—"}`,
        });
      }
    }

    // Open positions — entry arrow with "open" status (still bleeding/winning)
    const FRESH_THRESHOLD_MS = 30 * 60 * 1000; // pulse arrows opened in the last 30 min
    for (const p of symbolPositions) {
      if (p.entryTime && p.entryTime >= firstTime) {
        const ageMs = Date.now() - new Date(p.entryTime).getTime();
        out.push({
          time:  p.entryTime,
          price: p.entryPrice,
          side:  p.side === "LONG" ? "long" : "short",
          kind:  "entry",
          label: `OPEN ${p.side} @ $${p.entryPrice.toFixed(2)} · uPnL ${formatUsd(p.unrealizedPnL)}`,
          fresh: ageMs < FRESH_THRESHOLD_MS,
        });

        // ─── MFE / MAE ─── scan candles between entry and now to find the
        // running max-favorable-excursion and max-adverse-excursion price levels.
        const entryIdx = candles.findIndex((c) => c.time >= p.entryTime!);
        if (entryIdx >= 0) {
          const isLong = p.side === "LONG";
          let mfePrice  = p.entryPrice;
          let mfeTime   = p.entryTime!;
          let maePrice  = p.entryPrice;
          let maeTime   = p.entryTime!;
          for (let j = entryIdx; j < candles.length; j++) {
            const cj = candles[j];
            if (isLong) {
              if (cj.high > mfePrice) { mfePrice = cj.high; mfeTime = cj.time; }
              if (cj.low  < maePrice) { maePrice = cj.low;  maeTime = cj.time; }
            } else {
              if (cj.low  < mfePrice || mfePrice === p.entryPrice) {
                mfePrice = Math.min(mfePrice, cj.low);
                if (cj.low === mfePrice) mfeTime = cj.time;
              }
              if (cj.high > maePrice || maePrice === p.entryPrice) {
                maePrice = Math.max(maePrice, cj.high);
                if (cj.high === maePrice) maeTime = cj.time;
              }
            }
          }
          // Only render when actually moved
          if (mfePrice !== p.entryPrice) {
            const pct = ((mfePrice - p.entryPrice) / p.entryPrice) * 100 * (isLong ? 1 : -1);
            out.push({
              time: mfeTime,
              price: mfePrice,
              side: isLong ? "long" : "short",
              kind: "mfe",
              label: `MFE ${formatUsd(mfePrice)} (+${pct.toFixed(2)}%) · max favorable`,
            });
          }
          if (maePrice !== p.entryPrice) {
            const pct = ((maePrice - p.entryPrice) / p.entryPrice) * 100 * (isLong ? 1 : -1);
            out.push({
              time: maeTime,
              price: maePrice,
              side: isLong ? "long" : "short",
              kind: "mae",
              label: `MAE ${formatUsd(maePrice)} (${pct.toFixed(2)}%) · max adverse`,
            });
          }
        }
      }
    }

    // Pending signals — hollow triangles (planned)
    for (const s of symbolSignals) {
      if (s.timestamp && s.timestamp >= firstTime) {
        out.push({
          time:  s.timestamp,
          price: s.price,
          side:  s.side?.toUpperCase() === "BUY" || s.side?.toUpperCase() === "LONG" ? "long" : "short",
          kind:  "pending",
          label: `PENDING ${s.side} @ $${s.price.toFixed(2)} · ${s.strategyTag}`,
        });
      }
    }

    return out;
  }, [candles, symbolTrades, symbolPositions, symbolSignals]);

  // ── Levels: open-position Entry/SL/TP, plus pending-signal levels ──
  const levels = useMemo(() => {
    const result: { price: number; color: string; label: string; dash?: boolean }[] = [];

    // Open positions get the strongest line set (cyan/red/green)
    for (const p of symbolPositions) {
      if (p.entryPrice > 0)
        result.push({ price: p.entryPrice, color: "#0EA5E9", label: `Entry ${formatUsd(p.entryPrice)}`, dash: true });
      if (p.stopLoss > 0)
        result.push({ price: p.stopLoss,   color: "#F05A5A", label: `SL ${formatUsd(p.stopLoss)}`,    dash: true });
      if (p.takeProfit > 0)
        result.push({ price: p.takeProfit, color: "#22C55E", label: `TP ${formatUsd(p.takeProfit)}`,  dash: true });
    }
    // Pending signals get faded entry-only line
    for (const s of symbolSignals) {
      if (s.price > 0) {
        result.push({
          price: s.price,
          color: "rgba(232,121,249,0.7)",
          label: `Pending ${formatUsd(s.price)}`,
          dash: true,
        });
      }
    }
    return result;
  }, [symbolPositions, symbolSignals]);

  // Risk/reward zones for open positions on this symbol — translucent rectangles
  // between Entry↔SL (loss zone, rose) and Entry↔TP (profit zone, emerald). Lets the
  // operator eyeball asymmetric payoff geometry at a glance.
  const riskRewardZones = useMemo(() => {
    const zones: { priceA: number; priceB: number; fill: string; stroke?: string; label?: string }[] = [];
    for (const p of symbolPositions) {
      if (p.entryPrice <= 0) continue;
      const risk   = Math.abs(p.entryPrice - (p.stopLoss   || 0));
      const reward = Math.abs((p.takeProfit || 0) - p.entryPrice);
      const rr     = risk > 0 ? reward / risk : 0;
      if (p.stopLoss > 0) {
        zones.push({
          priceA: p.entryPrice,
          priceB: p.stopLoss,
          fill:   "rgba(240,90,90,0.10)",
          stroke: "rgba(240,90,90,0.55)",
          label:  `Risk · ${formatUsd(risk)}`,
        });
      }
      if (p.takeProfit > 0) {
        zones.push({
          priceA: p.entryPrice,
          priceB: p.takeProfit,
          fill:   "rgba(34,197,94,0.10)",
          stroke: "rgba(34,197,94,0.55)",
          label:  rr > 0 ? `Reward · ${formatUsd(reward)} · R:R ${rr.toFixed(2)}` : `Reward · ${formatUsd(reward)}`,
        });
      }
    }
    return zones;
  }, [symbolPositions]);

  // ── Layout sizing — chart area fills viewport minus header + bottom strip ──
  const chartContainerRef = useRef<HTMLDivElement>(null);

  // ── Snapshot helper — serialise SVG and download as PNG ──
  const takeSnapshot = () => {
    try {
      const root = chartContainerRef.current;
      if (!root) return;
      const svg = root.querySelector("svg");
      if (!svg) {
        pushNotification({ type: "warning", title: "Snapshot failed", body: "No chart found." });
        return;
      }
      const xml = new XMLSerializer().serializeToString(svg);
      const svgBlob = new Blob([
        '<?xml version="1.0" encoding="UTF-8"?>',
        xml,
      ], { type: "image/svg+xml;charset=utf-8" });
      const url = URL.createObjectURL(svgBlob);
      const img = new Image();
      img.onload = () => {
        const canvas = document.createElement("canvas");
        const w = svg.clientWidth || (svg as unknown as { width: { baseVal: { value: number } } }).width.baseVal.value || 1200;
        const h = svg.clientHeight || (svg as unknown as { height: { baseVal: { value: number } } }).height.baseVal.value || 700;
        canvas.width  = w;
        canvas.height = h;
        const ctx = canvas.getContext("2d");
        if (!ctx) return;
        ctx.fillStyle = "#000";
        ctx.fillRect(0, 0, w, h);
        ctx.drawImage(img, 0, 0, w, h);
        canvas.toBlob((blob) => {
          if (!blob) return;
          const a = document.createElement("a");
          a.href = URL.createObjectURL(blob);
          a.download = `${symbol}_${timeframe}_${new Date().toISOString().slice(0, 16).replace(/[:T]/g, "-")}.png`;
          document.body.appendChild(a);
          a.click();
          a.remove();
        }, "image/png");
        URL.revokeObjectURL(url);
      };
      img.src = url;
    } catch (err) {
      pushNotification({
        type: "error",
        title: "Snapshot failed",
        body: err instanceof Error ? err.message : String(err),
      });
    }
  };
  const [size, setSize] = useState({ w: 1200, h: 700 });
  useEffect(() => {
    const update = () => {
      const el = chartContainerRef.current;
      if (!el) return;
      setSize({ w: el.clientWidth, h: el.clientHeight });
    };
    update();
    const ro = new ResizeObserver(update);
    if (chartContainerRef.current) ro.observe(chartContainerRef.current);
    window.addEventListener("resize", update);
    return () => {
      ro.disconnect();
      window.removeEventListener("resize", update);
    };
  }, [showOrderBook]);

  // Render via Portal directly to <body> so we ESCAPE the AppShell stacking
  // context (Topbar's sticky z-20 was leaking through z-80 because of an
  // ancestor with `position: relative`).  Mounting on body guarantees the
  // overlay covers the whole viewport regardless of layout context.
  return createPortal(
    <div
      role="dialog"
      aria-modal="true"
      aria-label="Trade Mode"
      className="fixed inset-0 z-[200] flex flex-col bg-black text-foreground"
    >
      {/* Top bar — flex-wrap so the toolbar doesn't horizontally overflow on mid-width screens.
          Using <div> (not <header>) so global `html.trade-mode header` rules don't accidentally
          hide our own toolbar. */}
      <div className="shrink-0 flex items-center gap-2 flex-wrap px-3 py-2 border-b border-white/[0.06] bg-black/95 backdrop-blur-xl">
        <span className="text-[9px] font-black uppercase tracking-[0.4em] text-amber-400/80">
          ⛶ Trade Mode
        </span>

        {/* Symbol selector */}
        <select
          value={symbol}
          onChange={(e) => setSymbol(e.target.value)}
          className="bg-white/[0.04] border border-white/10 rounded px-2 py-1 text-sm font-mono"
          aria-label="Symbol"
        >
          {(symbolsQ.data ?? []).map((s) => (
            <option key={s.symbol} value={s.symbol}>{s.symbol}</option>
          ))}
        </select>

        {/* Timeframe pills */}
        <div className="flex items-center gap-0.5 rounded-md p-0.5 border border-white/10 bg-white/[0.02]">
          {TIMEFRAMES.map((tf) => (
            <button
              key={tf.value}
              onClick={() => setTimeframe(tf.value)}
              className={cn(
                "px-2 py-0.5 rounded text-[10px] font-mono uppercase",
                timeframe === tf.value
                  ? "bg-foreground/85 text-background font-black"
                  : "text-muted-foreground/60 hover:text-foreground"
              )}
            >
              {tf.label}
            </button>
          ))}
        </div>

        {/* Toggle row */}
        <div className="flex items-center gap-1 ml-2 text-[10px] font-mono flex-wrap">
          <ToggleChip label="Markers"  on={showAnnotations}    onClick={() => setShowAnnotations((v) => !v)} />
          <ToggleChip label="Levels"   on={showLevels}         onClick={() => setShowLevels((v) => !v)} />
          <ToggleChip label="Live Px"  on={showLivePrice}      onClick={() => setShowLivePrice((v) => !v)} />
          <ToggleChip label="Regime"   on={showRegimeTint}     onClick={() => setShowRegimeTint((v) => !v)} />
          <ToggleChip label="Liquidity" on={showLiquidity}     onClick={() => setShowLiquidity((v) => !v)} />
          <ToggleChip label="Vol Prof" on={showVolumeProfile}  onClick={() => setShowVolumeProfile((v) => !v)} />
          <ToggleChip label="AI"       on={showAi}             onClick={() => setShowAi((v) => !v)} />
          <ToggleChip label="T&S"      on={showTape}           onClick={() => setShowTape((v) => !v)} />
          <ToggleChip label="Book"     on={showOrderBook}      onClick={() => setShowOrderBook((v) => !v)} />
          <ToggleChip
            label={alertMode ? "🔔 Click chart…" : "🔔 Set Alert"}
            on={alertMode}
            onClick={() => setAlertMode((v) => !v)}
          />
          <ToggleChip label={soundOn ? "🔊 Sound" : "🔇 Mute"} on={soundOn} onClick={() => setSoundOn((v) => !v)} />
          {/* Compare symbol picker — null = off */}
          <select
            value={compareSymbol ?? ""}
            onChange={(e) => setCompareSymbol(e.target.value || null)}
            className="bg-white/[0.04] border border-white/10 rounded px-1.5 py-0.5 text-[10px] font-mono"
            title="Compare overlay (ghost line)"
          >
            <option value="">vs …</option>
            {(symbolsQ.data ?? []).filter((s) => s.symbol !== symbol).map((s) => (
              <option key={s.symbol} value={s.symbol}>vs {s.symbol}</option>
            ))}
          </select>
          <button
            onClick={takeSnapshot}
            className="px-2 py-0.5 rounded text-[10px] font-mono uppercase tracking-widest border border-white/10 text-muted-foreground/60 hover:text-foreground hover:bg-white/[0.04]"
            title="Save chart snapshot as PNG"
          >
            📸 PNG
          </button>
        </div>

        {/* Bot status row */}
        <BotStatusStrip />

        {/* Today's session at-a-glance */}
        <SessionStatsBadge />

        {/* Open position summary inline */}
        {symbolPositions.length > 0 && (
          <div className="ml-auto flex items-center gap-3 text-[11px] font-mono">
            {symbolPositions.map((p) => (
              <span key={p.id} className="flex items-center gap-1">
                <span className={cn(
                  "px-1.5 py-0.5 rounded text-[9px] font-black",
                  p.side === "LONG" ? "bg-emerald-500/15 text-emerald-300" : "bg-rose-500/15 text-rose-300"
                )}>
                  {p.side === "LONG" ? "↑" : "↓"} {p.side}
                </span>
                <span className="text-muted-foreground/60">qty</span>
                <span className="tabular-nums">{p.quantity.toFixed(4)}</span>
                <span className="text-muted-foreground/60">·</span>
                <span className={cn("tabular-nums font-bold",
                  p.unrealizedPnL >= 0 ? "text-emerald-300" : "text-rose-300")}>
                  {formatUsd(p.unrealizedPnL)}
                </span>
              </span>
            ))}
          </div>
        )}

        <button
          onClick={onClose}
          className={cn(
            "ml-2 text-muted-foreground/60 hover:text-foreground hover:bg-white/[0.05]",
            "px-2 py-1 rounded text-[11px] font-mono uppercase tracking-widest"
          )}
          title="Exit Trade Mode (Esc)"
        >
          ✕ Exit
        </button>
      </div>

      {/* Position chip strip — click to switch chart, [ / ] to cycle */}
      <PositionChipStrip
        positions={positionsQ.data ?? []}
        activeSymbol={symbol}
        onSelect={setSymbol}
      />

      {/* Watchlist pinned-symbols strip */}
      <WatchlistQuickSwitcher activeSymbol={symbol} onSelect={setSymbol} />

      {/* Main grid */}
      <div className="flex-1 grid grid-cols-[1fr_auto] gap-2 p-2 min-h-0 overflow-hidden">
        {/* Chart column — main candle chart + optional pair partner pane */}
        <div className="flex flex-col gap-2 min-h-0 h-full">
        <div
          ref={chartContainerRef}
          className={cn(
            "relative overflow-hidden rounded-lg border border-white/[0.05] bg-black flex-1 min-h-0",
            // Cursor hint by mode
            drawMode === "horizontal" && "cursor-crosshair",
            drawMode === "trendline"  && "cursor-crosshair",
            alertMode                 && "cursor-crosshair",
            drawMode === "none" && !alertMode && (isPanningRef.current ? "cursor-grabbing" : "cursor-grab"),
          )}
          // Mouse-wheel zoom: anchored on cursor X-position so zooming feels natural
          onWheel={(e) => {
            e.preventDefault();
            // Wheel up (deltaY < 0) = zoom IN, wheel down = zoom OUT
            const factor = e.deltaY > 0 ? 1.12 : 1 / 1.12;
            setZoomFactor((z) => Math.max(0.05, Math.min(1.0, z * factor)));
          }}
          // Drag-pan (left button only when no drawing tool active)
          onMouseDown={(e) => {
            if (drawMode !== "none" || alertMode) return;
            if (e.button !== 0) return;
            isPanningRef.current = { x: e.clientX, pan: panOffset };
          }}
          onMouseMove={(e) => {
            if (!isPanningRef.current) return;
            const dx = e.clientX - isPanningRef.current.x;
            // Each ~10px of horizontal drag pans by 1 candle
            const total = candlesAll.length;
            const visible = candles.length;
            const candlesPerPx = visible / Math.max(1, size.w);
            const newPan = isPanningRef.current.pan + Math.round(dx * candlesPerPx);
            const clampedPan = Math.max(0, Math.min(total - visible, newPan));
            setPanOffset(clampedPan);
            // Auto-extend history when the operator drags to the leftmost edge.
            // We only bump depth once per drag-batch and cap at 5000 bars to avoid
            // runaway requests on slow networks.
            if (clampedPan >= total - visible - 5 && historyDepth < 5000 && !candlesQ.isFetching) {
              setHistoryDepth((d) => Math.min(5000, d + 1000));
            }
          }}
          onMouseUp={()    => { isPanningRef.current = null; }}
          onMouseLeave={() => { isPanningRef.current = null; }}
        >
          {candlesQ.isLoading || candles.length === 0 ? (
            <div className="absolute inset-0 flex items-center justify-center text-xs italic font-serif text-muted-foreground/60">
              {candlesQ.isLoading ? "Loading candles…" : "No candle data."}
            </div>
          ) : (
            <CandleChart
              candles={candles}
              width={size.w}
              height={size.h}
              showVolume
              showVwap
              showVolumeProfile={showVolumeProfile}
              showGrid={showGrid}
              annotations={showAnnotations ? annotations : []}
              overlays={[
                ...(showLevels ? levels : []),
                ...drawnHorizontals,
              ]}
              trendlines={drawnTrendlines}
              pendingAnchor={pendingAnchor}
              livePrice={showLivePrice ? livePrice : undefined}
              regimeTint={showRegimeTint ? regimeTint : null}
              liquidityBands={showLiquidity ? liquidityBands : []}
              riskRewardZones={riskRewardZones}
              lines={[
                ...(compareLine ? [compareLine] : []),
                ...indicatorOverlays.lines,
              ]}
              bands={indicatorOverlays.bands}
              drawingMode={
                alertMode               ? "alert" :
                drawMode === "horizontal" ? "horizontal" :
                drawMode === "trendline"  ? "trendline" : "none"
              }
              onChartClick={(time, price) => {
                if (price <= 0) return;
                // 1) Alert mode → drop a price alert
                if (alertMode) {
                  const direction: "above" | "below" = price > livePrice ? "above" : "below";
                  priceAlertStore.add(symbol, direction, price);
                  pushNotification({
                    type: "success",
                    title: `🔔 Alert created · ${symbol}`,
                    body: `Will fire when price goes ${direction} ${formatUsd(price)}`,
                  });
                  setAlertMode(false);
                  return;
                }
                // 2) Drawing mode horizontal → add level
                if (drawMode === "horizontal") {
                  const newId = `${Date.now()}`;
                  setDrawnHorizontals((prev) => [...prev, {
                    price,
                    color: "#0EA5E9",
                    label: `H ${formatUsd(price)} #${newId.slice(-3)}`,
                    dash: true,
                  }]);
                  pushNotification({ type: "info", title: "📏 Level drawn", body: `Horizontal at ${formatUsd(price)} added.` });
                  return;
                }
                // 3) Drawing mode trendline → 2-click flow
                if (drawMode === "trendline") {
                  if (!pendingAnchor) {
                    setPendingAnchor({ time, price });
                    pushNotification({ type: "info", title: "✏ Trendline anchored", body: "Click again to set the second point." });
                  } else {
                    setDrawnTrendlines((prev) => [...prev, {
                      fromTime:  pendingAnchor.time,
                      fromPrice: pendingAnchor.price,
                      toTime:    time,
                      toPrice:   price,
                      color: "#A78BFA",
                      label: "TL",
                    }]);
                    setPendingAnchor(null);
                    setDrawMode("none");
                    pushNotification({ type: "success", title: "✏ Trendline drawn", body: "Click 'Trendline' again to draw another." });
                  }
                  return;
                }
              }}
            />
          )}
          {/* Legend */}
          <div className="absolute top-2 left-3 flex items-center gap-3 text-[9px] font-mono uppercase tracking-widest text-muted-foreground/55 pointer-events-none">
            <span><span className="text-emerald-400">▲</span> Long entry</span>
            <span><span className="text-rose-400">▼</span> Short entry</span>
            <span><span className="text-emerald-400 opacity-70">△</span> Pending long</span>
            <span><span className="text-rose-400 opacity-70">▽</span> Pending short</span>
            <span><span className="text-emerald-400">●</span> Win</span>
            <span><span className="text-rose-400">●</span> Loss</span>
          </div>
        </div>

        {/* Pair partner pane — only when toggled AND symbol is in an active pair */}
        {showPairPane && (
          <PairPartnerPane primarySymbol={symbol} timeframe={timeframe} />
        )}
        </div>

        {/* Right side stack — risk meters + AI + T&S + order book */}
        {(showOrderBook || showAi || showTape) && (
          <div className="w-[340px] flex flex-col gap-2 min-h-0 overflow-y-auto">
            {showTradePlan && <TradePlanCard symbol={symbol} />}
            <RiskMeterPanel positions={symbolPositions} livePrice={livePrice} />
            <BotDiscoveriesPanel onSelectSymbol={setSymbol} />
            {showAi   && <AICommentaryPanel symbol={symbol} timeframe={timeframe} />}
            {showTape && (
              <div className="h-[260px] shrink-0">
                <TimeSalesTape symbol={symbol} limit={50} />
              </div>
            )}
            {showOrderBook && (
              <div className="flex-1 min-h-[260px]">
                <OrderBookPanel symbol={symbol} />
              </div>
            )}
          </div>
        )}
      </div>

      {/* Chart toolbar — zoom + drawing tools */}
      <div className="shrink-0 px-4 py-1.5 border-t border-white/[0.06] bg-black/85 flex items-center gap-2 text-[10px] font-mono text-muted-foreground/70 flex-wrap">
        {/* Drawing tools */}
        <span className="uppercase tracking-widest text-[9px] text-muted-foreground/50">✏ Draw:</span>
        <ToolBtn
          active={drawMode === "horizontal"}
          onClick={() => {
            setDrawMode((m) => m === "horizontal" ? "none" : "horizontal");
            setPendingAnchor(null);
          }}
          title="Click chart to drop a horizontal price level"
        >
          ─ Horizontal
        </ToolBtn>
        <ToolBtn
          active={drawMode === "trendline"}
          onClick={() => {
            setDrawMode((m) => m === "trendline" ? "none" : "trendline");
            setPendingAnchor(null);
          }}
          title="Click 2 points to draw a trendline"
        >
          ╱ Trendline {pendingAnchor ? "(click endpoint)" : ""}
        </ToolBtn>
        <button
          disabled={drawnHorizontals.length === 0 && drawnTrendlines.length === 0}
          onClick={() => {
            setDrawnHorizontals([]);
            setDrawnTrendlines([]);
            setPendingAnchor(null);
            setDrawMode("none");
          }}
          className="px-2 py-0.5 rounded border border-white/10 text-[9px] font-mono uppercase hover:bg-white/[0.05] disabled:opacity-40 disabled:cursor-not-allowed"
          title="Remove all drawn levels and trendlines"
        >
          ✕ Clear ({drawnHorizontals.length + drawnTrendlines.length})
        </button>

        <span className="w-px h-4 bg-white/10 mx-1" />

        {/* Zoom controls */}
        <span className="uppercase tracking-widest text-[9px] text-muted-foreground/50">🔍 Zoom:</span>
        <button
          onClick={() => setZoomFactor((z) => Math.max(0.05, z / 1.25))}
          className="px-1.5 py-0 rounded border border-white/10 text-[10px] font-mono hover:bg-white/[0.05]"
          title="Zoom in (mouse wheel up also)"
        >
          ＋
        </button>
        <button
          onClick={() => setZoomFactor((z) => Math.min(1.0, z * 1.25))}
          className="px-1.5 py-0 rounded border border-white/10 text-[10px] font-mono hover:bg-white/[0.05]"
          title="Zoom out (mouse wheel down also)"
        >
          －
        </button>
        <span className="text-muted-foreground/55 tabular-nums">
          {candles.length}/{candlesAll.length} bars · {(zoomFactor * 100).toFixed(0)}%
        </span>
        {(zoomFactor < 1 || panOffset > 0) && (
          <button
            onClick={() => { setZoomFactor(1); setPanOffset(0); }}
            className="px-2 py-0 rounded border border-cyan-400/30 bg-cyan-500/10 text-cyan-300 text-[9px] font-mono uppercase hover:bg-cyan-500/20"
            title="Reset zoom and pan to live tail"
          >
            ⤺ Reset
          </button>
        )}

        <span className="w-px h-4 bg-white/10 mx-1" />

        {/* Indicator overlay picker */}
        <IndicatorOverlayPicker
          active={activeIndicators}
          onToggle={(k) => {
            setActiveIndicators((prev) => {
              const next = new Set(prev);
              if (next.has(k)) next.delete(k); else next.add(k);
              return next;
            });
          }}
        />

        <span className="w-px h-4 bg-white/10 mx-1" />

        {/* Chart grid + side-pane toggles + inline backtest */}
        <ToolBtn active={showGrid} onClick={() => setShowGrid((v) => !v)} title="Toggle price/time gridlines on the chart">
          ⊞ Grid
        </ToolBtn>
        <ToolBtn active={showPairPane} onClick={() => setShowPairPane((v) => !v)} title="Show / hide pair partner pane below chart">
          ↪ Pair pane
        </ToolBtn>
        <ToolBtn active={showTradePlan} onClick={() => setShowTradePlan((v) => !v)} title="Show / hide trade plan card in right stack">
          📋 Plan
        </ToolBtn>
        <InlineBacktestButton symbol={symbol} timeframe={timeframe} />

        <span className="ml-auto flex items-center gap-2">
          <span className="text-muted-foreground/40 text-[9px]">wheel = zoom · drag = pan</span>
          {candles.length > 0 && (
            <span className="text-muted-foreground/55 tabular-nums">
              {fmtTime(candles[candles.length - 1]?.time ?? "")}
            </span>
          )}
        </span>
      </div>

      {/* Bottom strip — alert ticker + flat-all + counters */}
      <footer className="shrink-0 px-4 py-2 border-t border-white/[0.06] bg-black/95 flex items-center gap-4 text-[10px] font-mono text-muted-foreground/70">
        <span className="shrink-0">
          <span className="text-foreground/80 font-bold">{symbolTrades.length}</span> closed ·{" "}
          <span className="text-foreground/80 font-bold">{symbolPositions.length}</span> open ·{" "}
          <span className="text-foreground/80 font-bold">{symbolSignals.length}</span> pending
        </span>
        <span className="w-px h-3 bg-white/10 shrink-0" />
        <AlertTicker />
        <span className="w-px h-3 bg-white/10 shrink-0" />
        <button
          onClick={() => {
            const totalOpen = (positionsQ.data ?? []).length;
            if (totalOpen === 0) {
              pushNotification({ type: "info", title: "Nothing to flatten", body: "No open positions across the portfolio." });
              return;
            }
            if (!confirm(`🛑 Flatten ALL ${totalOpen} open positions across the entire portfolio?`)) return;
            flatAllM.mutate("TradeMode.OperatorFlatAll");
          }}
          disabled={flatAllM.isPending}
          className={cn(
            "shrink-0 px-3 py-1 rounded text-[10px] font-black uppercase tracking-widest border transition-colors",
            "border-rose-400/40 text-rose-200 bg-rose-500/15 hover:bg-rose-500/25",
            flatAllM.isPending && "opacity-50 cursor-wait"
          )}
          title="Emergency flatten all positions across all symbols"
        >
          🛑 {flatAllM.isPending ? "Queueing…" : "FLAT ALL"}
        </button>
        <span className="text-muted-foreground/40 shrink-0">Esc · ✕ exit</span>
      </footer>
    </div>,
    document.body
  );
}

function fmtTime(iso: string): string {
  if (!iso) return "—";
  try {
    const d = new Date(iso);
    return d.toLocaleString([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
  } catch { return ""; }
}

function ToolBtn({
  active, onClick, title, children,
}: {
  active: boolean;
  onClick: () => void;
  title?: string;
  children: React.ReactNode;
}) {
  return (
    <button
      onClick={onClick}
      title={title}
      aria-pressed={active}
      className={cn(
        "px-2 py-0.5 rounded border text-[10px] font-mono transition-colors",
        active
          ? "border-accent/50 bg-accent/15 text-accent"
          : "border-white/10 text-muted-foreground/65 hover:text-foreground hover:bg-white/[0.04]"
      )}
    >
      {children}
    </button>
  );
}

function ToggleChip({
  label, on, onClick,
}: {
  label: string;
  on: boolean;
  onClick: () => void;
}) {
  return (
    <button
      onClick={onClick}
      aria-pressed={on}
      className={cn(
        "px-2 py-0.5 rounded border transition-colors",
        on
          ? "border-accent/40 text-accent bg-accent/10"
          : "border-white/10 text-muted-foreground/55 hover:text-foreground"
      )}
    >
      {label}
    </button>
  );
}
