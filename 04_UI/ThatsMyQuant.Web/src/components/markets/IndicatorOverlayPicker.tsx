import { useMemo } from "react";
import { sma, ema, bollingerBands } from "@/lib/indicators";
import type { IndicatorLine, IndicatorBand } from "@/components/charts/CandleChart";
import { cn } from "@/lib/utils";

export type IndicatorKey = "ema21" | "ema50" | "ema200" | "sma20" | "sma50" | "bb";

interface Closes { close: number }

/**
 * Toggle chip strip for chart indicator overlays. Computes selected
 * indicators from the candle close series and exposes them via
 * `useIndicatorOverlays` hook so the parent can pass `lines` + `bands` to
 * CandleChart.
 *
 * Available:
 *   • EMA 21 / 50 / 200 (cyan/amber/violet)
 *   • SMA 20 / 50       (green/teal)
 *   • Bollinger Bands(20,2σ) — filled band
 *
 * RSI/MACD are NOT here because they need a separate sub-pane (already
 * supported by IndicatorPanes on the regular Markets page).
 */
const SPECS: { key: IndicatorKey; label: string; color: string }[] = [
  { key: "ema21",  label: "EMA21",  color: "#0EA5E9" },
  { key: "ema50",  label: "EMA50",  color: "#F59E0B" },
  { key: "ema200", label: "EMA200", color: "#A78BFA" },
  { key: "sma20",  label: "SMA20",  color: "#3B8C68" },
  { key: "sma50",  label: "SMA50",  color: "#2DD4BF" },
  { key: "bb",     label: "BB(20,2)", color: "#94A3B8" },
];

export function IndicatorOverlayPicker({ active, onToggle }: { active: Set<IndicatorKey>; onToggle: (k: IndicatorKey) => void }) {
  return (
    <div className="flex items-center gap-1 flex-wrap">
      <span className="text-[9px] uppercase tracking-widest text-muted-foreground/50">📈 Indicators:</span>
      {SPECS.map((s) => {
        const on = active.has(s.key);
        return (
          <button
            key={s.key}
            onClick={() => onToggle(s.key)}
            className={cn(
              "px-2 py-0.5 rounded border text-[10px] font-mono transition-colors",
              on ? "text-foreground" : "text-muted-foreground/55 hover:text-foreground hover:bg-white/[0.04] border-white/10"
            )}
            style={on ? { borderColor: `${s.color}66`, backgroundColor: `${s.color}1a`, color: s.color } : undefined}
            aria-pressed={on}
          >
            {s.label}
          </button>
        );
      })}
    </div>
  );
}

/**
 * Hook variant — given candles + selected keys, returns the lines/bands
 * arrays ready to be spread into CandleChart props.
 */
export function useIndicatorOverlays(candles: Closes[], active: Set<IndicatorKey>):
  { lines: IndicatorLine[]; bands: IndicatorBand[] }
{
  return useMemo(() => {
    const closes = candles.map((c) => c.close);
    const lines: IndicatorLine[] = [];
    const bands: IndicatorBand[] = [];
    if (active.has("ema21"))  lines.push({ values: ema(closes, 21),  color: "#0EA5E9", width: 1.4, label: "EMA21" });
    if (active.has("ema50"))  lines.push({ values: ema(closes, 50),  color: "#F59E0B", width: 1.4, label: "EMA50" });
    if (active.has("ema200")) lines.push({ values: ema(closes, 200), color: "#A78BFA", width: 1.4, label: "EMA200" });
    if (active.has("sma20"))  lines.push({ values: sma(closes, 20),  color: "#3B8C68", width: 1.2, dash: true, label: "SMA20" });
    if (active.has("sma50"))  lines.push({ values: sma(closes, 50),  color: "#2DD4BF", width: 1.2, dash: true, label: "SMA50" });
    if (active.has("bb")) {
      const bb = bollingerBands(closes, 20, 2);
      bands.push({
        upper: bb.map((b) => b?.upper ?? null),
        lower: bb.map((b) => b?.lower ?? null),
        fill: "rgba(148,163,184,0.08)",
        strokeColor: "rgba(148,163,184,0.6)",
        label: "BB(20,2σ)",
      });
      lines.push({ values: bb.map((b) => b?.mid ?? null), color: "#94A3B8", width: 1.0, dash: true, label: "BB-mid" });
    }
    return { lines, bands };
  }, [candles, active]);
}
