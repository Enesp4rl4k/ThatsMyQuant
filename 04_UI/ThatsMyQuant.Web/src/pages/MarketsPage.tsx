import { useEffect, useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { marketsApi } from "@/api/markets";
import { SymbolWatchlist } from "@/components/markets/SymbolWatchlist";
import { MultiChartArea } from "@/components/markets/MultiChartArea";
import { MiniChartPopout } from "@/components/markets/MiniChartPopout";
import { OrderBookPanel } from "@/components/markets/OrderBookPanel";
import { AnalysisPanel } from "@/components/markets/AnalysisPanel";
import { TradeBotPanel } from "@/components/markets/TradeBotPanel";
import { RecentExecutionsPanel } from "@/components/markets/RecentExecutionsPanel";
import { QuickSwitcher } from "@/components/markets/QuickSwitcher";
import { AnalystPanel } from "@/components/markets/AnalystPanel";
import { LiveTape } from "@/components/markets/LiveTape";
import { PriceAlertsCard } from "@/components/markets/PriceAlertsCard";
import { SymbolDetailCard } from "@/components/markets/SymbolDetailCard";
import { DrawingsPanel } from "@/components/markets/DrawingsPanel";
import { WorkspaceMenu } from "@/components/markets/WorkspaceMenu";
import { SymbolPnlStatsCard } from "@/components/markets/SymbolPnlStatsCard";
import { TimeOfDayHeatmap } from "@/components/markets/TimeOfDayHeatmap";
import { OrderEntryPanel } from "@/components/markets/OrderEntryPanel";
import { OrderBookHeatmap } from "@/components/markets/OrderBookHeatmap";
import { SignalConfidenceRadar } from "@/components/markets/SignalConfidenceRadar";
import { ThreeTfStrip } from "@/components/markets/ThreeTfStrip";
import { TradeModeView } from "@/components/markets/TradeModeView";
import { ResizableRow } from "@/components/markets/ResizableRow";
import { useIsMobile } from "@/hooks/useIsMobile";
import { CustomizeBar } from "@/components/ui/CustomizeBar";
import { usePageCustomization } from "@/hooks/usePageCustomization";
import type { WidgetSpec } from "@/lib/pageCustomization";
import type { LayoutMode, ChartPane as ChartPaneCfg, MarketsWorkspace } from "@/lib/workspaces";

const MARKETS_WIDGETS: WidgetSpec[] = [
  { id: "watchlist", label: "Watchlist", description: "Symbol list with sparkline + 24h change · core navigation", category: "Core", defaultVisible: true, required: true },
  { id: "main-chart", label: "Main Chart", description: "Candle chart with indicator overlays + multi-chart layout", category: "Core", defaultVisible: true, required: true },
  { id: "trade-bot", label: "Trade Bot Status", description: "Read-only bot snapshot · controls live on Pro Mode", category: "Core", defaultVisible: true },
  { id: "order-book", label: "Order Book", description: "Bid / ask ladder · synthesised until WS L2 lands", category: "Analysis", defaultVisible: true },
  { id: "analysis", label: "System Analysis", description: "RSI gauge · MA / ATR / regime · indicator readout", category: "Analysis", defaultVisible: true },
  { id: "recent-activity", label: "Recent Activity", description: "Symbol-filtered signals + closed trades", category: "Analysis", defaultVisible: true },
  { id: "ai-analyst", label: "AI Analyst", description: "Indicator-driven template paragraph · bias-tinted card", category: "Intelligence", defaultVisible: true },
  { id: "live-tape", label: "Live Tape", description: "Streaming print ticker · synthesised until real WS tape", category: "Intelligence", defaultVisible: true },
  { id: "alerts", label: "Price Alerts", description: "Above / below price triggers · sound + desktop notify", category: "Intelligence", defaultVisible: true },
  { id: "symbol-detail", label: "Symbol Detail", description: "24h volume + range, 7d sparkline + hi/lo", category: "Detail", defaultVisible: true },
  { id: "drawings", label: "Drawings", description: "Horizontal price levels + click-to-draw trendlines", category: "Detail", defaultVisible: true },
  { id: "pnl-stats", label: "Per-symbol P&L", description: "Historical WR / PF / Sharpe / streaks for this symbol", category: "Performance", defaultVisible: true },
  { id: "tod-heatmap", label: "Time-of-Day Heatmap", description: "7×24 grid · which hours work for this symbol", category: "Performance", defaultVisible: true },
  { id: "order-entry", label: "Order Entry", description: "Paper-mode position-sizing calculator", category: "Trading", defaultVisible: true },
  { id: "depth-heatmap", label: "Depth Heatmap", description: "Order-book depth visualised as a centered bid/ask heatmap", category: "Analysis", defaultVisible: false },
  { id: "confidence-radar", label: "Signal Confidence", description: "Six-axis radar (momentum/trend/volume/vol/sentiment/alphaTrend)", category: "Intelligence", defaultVisible: false },
  { id: "mtf-strip", label: "Multi-TF Context", description: "1D/4H/1H thumbnails with bias badge + EMA crossover", category: "Analysis", defaultVisible: false },
];

const STORAGE_KEY = "tmq.markets.state";

interface Persisted {
  selected: string;
  pinned: string[];
  layoutMode: LayoutMode;
  panes: ChartPaneCfg[];
}

const DEFAULT_PANES: ChartPaneCfg[] = [
  { symbol: "ETHUSDT", timeframe: "1h" },
  { symbol: "SOLUSDT", timeframe: "1h" },
  { symbol: "AVAXUSDT", timeframe: "1h" },
];

function readPersisted(): Persisted {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) {
      return {
        selected: "BTCUSDT",
        pinned: [],
        layoutMode: "single",
        panes: DEFAULT_PANES,
      };
    }
    const parsed = JSON.parse(raw) as Partial<Persisted>;
    return {
      selected: parsed.selected || "BTCUSDT",
      pinned: Array.isArray(parsed.pinned) ? parsed.pinned : [],
      layoutMode: (parsed.layoutMode as LayoutMode) || "single",
      panes: Array.isArray(parsed.panes) && parsed.panes.length > 0 ? parsed.panes : DEFAULT_PANES,
    };
  } catch {
    return {
      selected: "BTCUSDT",
      pinned: [],
      layoutMode: "single",
      panes: DEFAULT_PANES,
    };
  }
}

export function MarketsPage() {
  const initial = readPersisted();
  const isMobile = useIsMobile();
  const cust = usePageCustomization("markets", MARKETS_WIDGETS);
  const [selected, setSelected] = useState(initial.selected);
  const [pinned, setPinned] = useState<string[]>(initial.pinned);
  const [layoutMode, setLayoutMode] = useState<LayoutMode>(initial.layoutMode);
  const [panes, setPanes] = useState<ChartPaneCfg[]>(initial.panes);
  const [zMap, setZMap] = useState<Record<string, number>>({});
  const [topZ, setTopZ] = useState(50);
  const [switcherOpen, setSwitcherOpen] = useState(false);
  const [tradeMode, setTradeMode] = useState(false);

  // Persist UI state
  useEffect(() => {
    try {
      localStorage.setItem(
        STORAGE_KEY,
        JSON.stringify({ selected, pinned, layoutMode, panes } as Persisted)
      );
    } catch {
      /* ignore */
    }
  }, [selected, pinned, layoutMode, panes]);

  // Cmd/Ctrl+K opens the quick switcher; "T" toggles Trade Mode
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") {
        e.preventDefault();
        setSwitcherOpen((v) => !v);
      }
      // "T" alone (no modifiers, not in input) → toggle Trade Mode
      const tag = (e.target as HTMLElement | null)?.tagName?.toLowerCase();
      const inField = tag === "input" || tag === "textarea" || tag === "select" || (e.target as HTMLElement | null)?.isContentEditable;
      if (!inField && !e.ctrlKey && !e.metaKey && !e.altKey && e.key.toLowerCase() === "t") {
        e.preventDefault();
        setTradeMode((v) => !v);
      }
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const symbolsQ = useQuery({
    queryKey: ["markets", "symbols"],
    queryFn: () => marketsApi.symbols(),
    refetchInterval: 15_000,
  });

  const snapshot = symbolsQ.data?.find((s) => s.symbol === selected);
  const symbolPicks = useMemo(
    () => (symbolsQ.data ?? []).map((s) => s.symbol),
    [symbolsQ.data]
  );
  const currentPrice = snapshot?.lastPrice;

  const togglePin = (sym: string) => {
    setPinned((prev) =>
      prev.includes(sym) ? prev.filter((s) => s !== sym) : [...prev, sym]
    );
    if (!pinned.includes(sym)) {
      setTopZ((z) => z + 1);
      setZMap((m) => ({ ...m, [sym]: topZ + 1 }));
    }
  };

  const focusPopout = (sym: string) => {
    setTopZ((z) => z + 1);
    setZMap((m) => ({ ...m, [sym]: topZ + 1 }));
  };

  const onPaneChange = (index: number, next: Partial<ChartPaneCfg>) => {
    if (index < 0) return; // primary pane uses primary symbol — ignore
    setPanes((prev) => {
      const arr = [...prev];
      arr[index] = { ...(arr[index] ?? { symbol: "ETHUSDT", timeframe: "1h" }), ...next };
      return arr;
    });
  };

  const onClosePane = (index: number) => {
    if (layoutMode === "split-2") {
      setLayoutMode("single");
    } else if (layoutMode === "quad") {
      setLayoutMode("split-2");
      setPanes((prev) => prev.filter((_, i) => i !== index));
    }
  };

  const loadWorkspace = (ws: MarketsWorkspace) => {
    setSelected(ws.selected);
    setPinned(ws.pinned);
    setLayoutMode(ws.layoutMode);
    setPanes(ws.panes.length > 0 ? ws.panes : DEFAULT_PANES);
  };

  const pinnedSet = new Set(pinned);

  return (
    <div className="space-y-3 -mx-2 sm:-mx-6">
      {/* Title strip */}
      <div className="flex items-baseline justify-between gap-3 px-2 sm:px-6 pb-1 border-b border-white/5 flex-wrap">
        <div className="flex items-baseline gap-3">
          <h2 className="font-serif text-[18px] tracking-tight">Markets</h2>
          <span className="text-[10px] uppercase tracking-[0.25em] text-accent/70 font-medium">
            Trade Terminal
          </span>
          {isMobile && (
            <span className="text-[9px] uppercase tracking-widest text-amber-400/70 font-mono">
              · single-chart mode (mobile)
            </span>
          )}
        </div>
        <div className="flex items-center gap-2">
          <WorkspaceMenu
            current={{
              selected,
              pinned,
              layoutMode,
              panes,
              indicators: [],
            }}
            onLoad={loadWorkspace}
          />
          <button
            onClick={() => setTradeMode(true)}
            className="flex items-center gap-1.5 px-2.5 py-1 rounded-md text-[10px] font-mono font-black uppercase tracking-widest border border-amber-400/30 bg-amber-400/10 text-amber-300 hover:bg-amber-400/20 transition-colors"
            title="Open immersive Trade Mode (T)"
          >
            ⛶ Trade Mode
            <kbd className="hidden sm:inline-block px-1 py-0.5 rounded bg-black/40 text-[8px]">T</kbd>
          </button>
          <button
            onClick={() => setSwitcherOpen(true)}
            className="hidden md:flex items-center gap-2 px-2.5 py-1 rounded-md text-[10px] font-mono text-muted-foreground/70 border border-white/10 bg-white/[0.02] hover:bg-white/[0.05] hover:text-foreground transition-colors"
            title="Quick switch symbol (Cmd/Ctrl+K)"
          >
            <svg width="11" height="11" viewBox="0 0 16 16" fill="none">
              <circle cx="7" cy="7" r="5" stroke="currentColor" strokeWidth="1.4" />
              <path d="M11 11l3.5 3.5" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
            </svg>
            Switch symbol
            <kbd className="px-1 py-0.5 rounded bg-white/5 border border-white/10 text-[9px]">
              ⌘K
            </kbd>
          </button>
        </div>
      </div>

      {/* ROW 1 — watchlist | layout-aware chart area | trade bot (resizable) */}
      <div className="px-2 sm:px-6">
        <ResizableRow
          storageKey="tmq.markets.row1.widths"
          initial={{ left: 270, right: 310 }}
          min={{ left: 220, right: 260 }}
          max={{ left: 420, right: 460 }}
          left={
            <div className="lg:max-h-[680px] min-h-[400px]">
              <SymbolWatchlist
                selected={selected}
                onSelect={setSelected}
                pinned={pinnedSet}
                onTogglePin={togglePin}
              />
            </div>
          }
          center={
            <div className="min-w-0">
              <MultiChartArea
                layoutMode={isMobile ? "single" : layoutMode}
                primarySymbol={selected}
                primarySnapshot={snapshot}
                panes={panes}
                onLayoutChange={setLayoutMode}
                onPaneChange={onPaneChange}
                onClosePane={onClosePane}
                symbolPicks={symbolPicks}
                onSymbolPicked={setSelected}
              />
            </div>
          }
          right={
            cust.isVisible("trade-bot") ? (
              <div className="lg:max-h-[680px]">
                <TradeBotPanel />
              </div>
            ) : null
          }
        />
      </div>

      {/* ROW 2 — order book | analysis | recent activity */}
      {(cust.isVisible("order-book") || cust.isVisible("analysis") || cust.isVisible("recent-activity")) && (
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-3 px-2 sm:px-6">
          {cust.isVisible("order-book") && (
            <div className="min-h-[420px]"><OrderBookPanel symbol={selected} /></div>
          )}
          {cust.isVisible("analysis") && (
            <div className="min-h-[420px]"><AnalysisPanel symbol={selected} /></div>
          )}
          {cust.isVisible("recent-activity") && (
            <div className="min-h-[420px]"><RecentExecutionsPanel symbol={selected} /></div>
          )}
        </div>
      )}

      {/* ROW 2.5 — depth heatmap | confidence radar (advanced visualisations) */}
      {(cust.isVisible("depth-heatmap") || cust.isVisible("confidence-radar")) && (
        <div className="grid grid-cols-1 md:grid-cols-2 gap-3 px-2 sm:px-6">
          {cust.isVisible("depth-heatmap") && (
            <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl">
              <OrderBookHeatmap symbol={selected} width={600} height={420} />
            </div>
          )}
          {cust.isVisible("confidence-radar") && (
            <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl">
              <SignalConfidenceRadar symbol={selected} timeframe="1h" size={360} />
            </div>
          )}
        </div>
      )}

      {/* MTF Context Strip */}
      {cust.isVisible("mtf-strip") && (
        <div className="px-2 sm:px-6">
          <ThreeTfStrip symbol={selected} />
        </div>
      )}

      {/* ROW 3 — AI analyst (wide) | live tape | price alerts */}
      {(cust.isVisible("ai-analyst") || cust.isVisible("live-tape") || cust.isVisible("alerts")) && (
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-[minmax(0,1.4fr)_minmax(0,1fr)_minmax(0,1fr)] gap-3 px-2 sm:px-6">
          {cust.isVisible("ai-analyst") && (
            <div className="min-h-[360px]"><AnalystPanel symbol={selected} /></div>
          )}
          {cust.isVisible("live-tape") && (
            <div className="min-h-[360px]"><LiveTape symbol={selected} /></div>
          )}
          {cust.isVisible("alerts") && (
            <div className="min-h-[360px]"><PriceAlertsCard symbol={selected} /></div>
          )}
        </div>
      )}

      {/* ROW 4 — symbol detail | drawings */}
      {(cust.isVisible("symbol-detail") || cust.isVisible("drawings")) && (
        <div className="grid grid-cols-1 md:grid-cols-2 gap-3 px-2 sm:px-6">
          {cust.isVisible("symbol-detail") && <SymbolDetailCard symbol={selected} />}
          {cust.isVisible("drawings") && (
            <DrawingsPanel symbol={selected} currentPrice={currentPrice} />
          )}
        </div>
      )}

      {/* ROW 5 — per-symbol P&L stats | time-of-day heatmap */}
      {(cust.isVisible("pnl-stats") || cust.isVisible("tod-heatmap")) && (
        <div className="grid grid-cols-1 md:grid-cols-2 gap-3 px-2 sm:px-6">
          {cust.isVisible("pnl-stats") && <SymbolPnlStatsCard symbol={selected} />}
          {cust.isVisible("tod-heatmap") && <TimeOfDayHeatmap symbol={selected} />}
        </div>
      )}

      {/* ROW 6 — order entry calculator (alert channels moved to Settings) */}
      {cust.isVisible("order-entry") && (
        <div className="px-2 sm:px-6">
          <OrderEntryPanel symbol={selected} />
        </div>
      )}

      {/* Customize bar */}
      <div className="px-2 sm:px-6">
        <CustomizeBar customization={cust} pageLabel="Markets · Trade Terminal" />
      </div>

      {/* Floating mini charts (pinned symbols) */}
      {/* Floating mini charts hidden on mobile — they overflow viewport */}
      {!isMobile && pinned.map((sym, i) => (
        <MiniChartPopout
          key={sym}
          symbol={sym}
          initialX={20 + (i % 3) * 380}
          initialY={120 + Math.floor(i / 3) * 280}
          zIndex={zMap[sym] ?? 50 + i}
          onClose={() => togglePin(sym)}
          onFocus={() => focusPopout(sym)}
        />
      ))}

      {/* Cmd+K switcher */}
      <QuickSwitcher
        open={switcherOpen}
        onClose={() => setSwitcherOpen(false)}
        onSelect={setSelected}
      />

      {/* ⛶ Immersive Trade Mode — full-viewport overlay */}
      {tradeMode && (
        <TradeModeView
          symbol={selected}
          initialTimeframe="1h"
          onClose={() => setTradeMode(false)}
        />
      )}
    </div>
  );
}
