import { useState } from "react";
import { MainSymbolChart } from "@/components/markets/MainSymbolChart";
import { ChartPane } from "@/components/markets/ChartPane";
import type { LayoutMode, ChartPane as ChartPaneCfg } from "@/lib/workspaces";
import type { MarketSymbol, Timeframe } from "@/api/markets";
import { ChartSyncContext } from "@/lib/chartSync";
import { cn } from "@/lib/utils";

interface Props {
  layoutMode: LayoutMode;
  primarySymbol: string;
  primarySnapshot?: MarketSymbol;
  panes: ChartPaneCfg[];
  onLayoutChange: (mode: LayoutMode) => void;
  onPaneChange: (index: number, next: Partial<ChartPaneCfg>) => void;
  onClosePane: (index: number) => void;
  symbolPicks?: string[];
  onSymbolPicked?: (symbol: string) => void;
}

const LAYOUT_BUTTONS: { mode: LayoutMode; label: string; cells: number; glyph: string }[] = [
  { mode: "single", label: "1", cells: 1, glyph: "▢" },
  { mode: "split-2", label: "2", cells: 2, glyph: "▢▢" },
  { mode: "quad", label: "4", cells: 4, glyph: "⊞" },
];

/**
 * Wraps the main chart area with a 1/2/4 layout switcher.
 *
 * - "single" → renders the full-featured `<MainSymbolChart>` for the primary symbol
 * - "split-2" → renders 2 `<ChartPane>` tiles side-by-side (1st = primary, 2nd = panes[0])
 * - "quad" → renders 2x2 of `<ChartPane>` tiles (1st = primary, 2nd-4th = panes[0..2])
 *
 * In multi-chart modes, each pane has its own symbol + timeframe selectors so
 * traders can monitor cross-asset correlations or different timeframes of the
 * same asset.
 */
export function MultiChartArea({
  layoutMode,
  primarySymbol,
  primarySnapshot,
  panes,
  onLayoutChange,
  onPaneChange,
  onClosePane,
  symbolPicks,
  onSymbolPicked,
}: Props) {
  const [hoveredIndex, setHoveredIndex] = useState<number | null>(null);
  const required = layoutMode === "single" ? 0 : layoutMode === "split-2" ? 1 : 3;
  // Ensure we have enough pane configs (defaults if missing)
  const safePanes: ChartPaneCfg[] = [...panes];
  while (safePanes.length < required) {
    const fallback = ["ETHUSDT", "SOLUSDT", "AVAXUSDT", "LINKUSDT"][safePanes.length] || "ETHUSDT";
    safePanes.push({ symbol: fallback, timeframe: "1h" });
  }

  return (
    <ChartSyncContext.Provider value={{ hoveredIndex, setHoveredIndex }}>
    <div className="space-y-2">
      {/* Layout selector */}
      <div className="flex items-center justify-end gap-1">
        <span className="text-[9px] uppercase tracking-widest text-muted-foreground/50 mr-1">
          Layout
        </span>
        {LAYOUT_BUTTONS.map((b) => (
          <button
            key={b.mode}
            onClick={() => onLayoutChange(b.mode)}
            aria-pressed={layoutMode === b.mode}
            title={`${b.cells} chart${b.cells > 1 ? "s" : ""}`}
            className={cn(
              "px-2 py-1 rounded text-[10px] font-mono font-bold tracking-wider transition-colors border",
              layoutMode === b.mode
                ? "bg-accent/15 text-accent border-accent/30"
                : "border-white/10 text-muted-foreground/60 hover:text-foreground hover:bg-white/[0.03]"
            )}
          >
            <span className="mr-1.5">{b.glyph}</span>
            {b.label}
          </button>
        ))}
      </div>

      {/* Layout body */}
      {layoutMode === "single" && (
        <MainSymbolChart symbol={primarySymbol} snapshot={primarySnapshot} />
      )}

      {layoutMode === "split-2" && (
        <div className="grid grid-cols-1 md:grid-cols-2 gap-2">
          <ChartPane
            symbol={primarySymbol}
            timeframe={(safePanes[0]?.timeframe as Timeframe) ?? "1h"}
            onTimeframeChange={(tf) => onPaneChange(-1, { timeframe: tf })}
            primary
            showVolume
            height={300}
            symbolPicks={symbolPicks}
            onSymbolClick={onSymbolPicked}
          />
          <ChartPane
            symbol={safePanes[0]?.symbol ?? "ETHUSDT"}
            timeframe={(safePanes[0]?.timeframe as Timeframe) ?? "1h"}
            onSymbolChange={(s) => onPaneChange(0, { symbol: s })}
            onTimeframeChange={(tf) => onPaneChange(0, { timeframe: tf })}
            onClose={() => onClosePane(0)}
            height={300}
            symbolPicks={symbolPicks}
            onSymbolClick={onSymbolPicked}
          />
        </div>
      )}

      {layoutMode === "quad" && (
        <div className="grid grid-cols-1 md:grid-cols-2 gap-2">
          <ChartPane
            symbol={primarySymbol}
            timeframe={(safePanes[0]?.timeframe as Timeframe) ?? "1h"}
            onTimeframeChange={(tf) => onPaneChange(-1, { timeframe: tf })}
            primary
            height={240}
            symbolPicks={symbolPicks}
            onSymbolClick={onSymbolPicked}
          />
          {[0, 1, 2].map((i) => (
            <ChartPane
              key={i}
              symbol={safePanes[i]?.symbol ?? "ETHUSDT"}
              timeframe={(safePanes[i]?.timeframe as Timeframe) ?? "1h"}
              onSymbolChange={(s) => onPaneChange(i, { symbol: s })}
              onTimeframeChange={(tf) => onPaneChange(i, { timeframe: tf })}
              onClose={() => onClosePane(i)}
              height={240}
              symbolPicks={symbolPicks}
              onSymbolClick={onSymbolPicked}
            />
          ))}
        </div>
      )}
    </div>
    </ChartSyncContext.Provider>
  );
}
