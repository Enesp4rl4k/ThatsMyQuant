import { useEffect, useState } from "react";
import { drawingStore, drawingMode, type Drawing, type ToolMode } from "@/lib/chartDrawings";
import { cn } from "@/lib/utils";

interface Props {
  symbol: string;
  /** Suggested current price for quick-add. */
  currentPrice?: number;
}

/**
 * Panel for managing chart drawings (horizontal price levels). Each drawing
 * is keyed to the symbol; the chart picks them up via `useChartDrawings`.
 *
 * Trendlines are stored in the data model but the click-to-draw interaction
 * is not yet wired — manage trendline anchors by editing localStorage if you
 * really want them today (or wait for the proper drawing-mode SVG handler).
 */
export function DrawingsPanel({ symbol, currentPrice }: Props) {
  const [drawings, setDrawings] = useState<Drawing[]>(() => drawingStore.list(symbol));
  const [draftPrice, setDraftPrice] = useState("");
  const [tool, setTool] = useState<ToolMode>(() => drawingMode.get());

  useEffect(() => {
    setDrawings(drawingStore.list(symbol));
    return drawingStore.subscribe((sym, list) => {
      if (sym === symbol) setDrawings(list);
    });
  }, [symbol]);

  useEffect(() => drawingMode.subscribe((m) => setTool(m)), []);

  const submit = () => {
    const price = parseFloat(draftPrice);
    if (!Number.isFinite(price) || price <= 0) return;
    drawingStore.addHorizontal(symbol, price);
    setDraftPrice("");
  };

  const horizontals = drawings.filter((d): d is Extract<Drawing, { kind: "horizontal" }> => d.kind === "horizontal");
  const trendlines = drawings.filter((d): d is Extract<Drawing, { kind: "trendline" }> => d.kind === "trendline");

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      <div className="px-4 py-2.5 border-b border-white/5 flex items-baseline justify-between">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
            Drawings
          </p>
          <p className="text-[11px] font-serif italic text-muted-foreground/60">
            horizontal levels on {symbol}
          </p>
        </div>
        {horizontals.length > 0 && (
          <button
            onClick={() => drawingStore.clear(symbol)}
            className="text-[9px] uppercase tracking-widest text-muted-foreground/50 hover:text-rose-400 transition-colors"
          >
            clear
          </button>
        )}
      </div>

      <div className="p-3 space-y-3">
        {/* Tool toggle — click-to-draw modes */}
        <div className="grid grid-cols-3 gap-1">
          {(
            [
              { key: "none", label: "Cursor", icon: "↖" },
              { key: "horizontal", label: "Line", icon: "—" },
              { key: "trendline", label: "Trend", icon: "╱" },
            ] as { key: ToolMode; label: string; icon: string }[]
          ).map((t) => (
            <button
              key={t.key}
              onClick={() => drawingMode.set(t.key)}
              aria-pressed={tool === t.key}
              className={cn(
                "py-1 rounded text-[10px] font-mono uppercase tracking-widest border transition-colors flex items-center justify-center gap-1",
                tool === t.key
                  ? "bg-accent/15 text-accent border-accent/30"
                  : "border-white/10 text-muted-foreground/60 hover:text-foreground hover:bg-white/[0.03]"
              )}
            >
              <span className="opacity-70">{t.icon}</span>
              {t.label}
            </button>
          ))}
        </div>
        {tool !== "none" && (
          <p className="text-[9px] font-mono italic text-accent/80 text-center">
            {tool === "horizontal"
              ? "click chart to drop a horizontal line · Esc to cancel"
              : "click chart to anchor first point · then click to anchor second · Esc to cancel"}
          </p>
        )}

        {/* Manual horizontal entry — works regardless of tool mode */}
        <div className="pt-3 border-t border-white/5">
          <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/50 mb-1.5">
            Or enter price
          </p>
          <div className="flex items-center gap-2">
            <input
              type="number"
              step="any"
              value={draftPrice}
              onChange={(e) => setDraftPrice(e.target.value)}
              onKeyDown={(e) => e.key === "Enter" && submit()}
              placeholder={currentPrice ? currentPrice.toString() : "price level"}
              aria-label="Horizontal line price"
              className="flex-1 bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
            />
            <button
              onClick={submit}
              disabled={!draftPrice}
              className="px-2.5 py-1.5 rounded text-[10px] font-black uppercase tracking-widest bg-accent/15 text-accent border border-accent/40 hover:bg-accent/25 transition-colors disabled:opacity-40"
            >
              + Line
            </button>
          </div>
          {currentPrice && (
            <button
              onClick={() => {
                drawingStore.addHorizontal(symbol, currentPrice);
              }}
              className="text-[9px] font-mono text-muted-foreground/60 italic hover:text-accent transition-colors mt-1.5"
            >
              ↳ snap at current {fmt(currentPrice)}
            </button>
          )}
        </div>
      </div>

      {trendlines.length > 0 && (
        <div className="border-t border-white/5 px-4 py-2">
          <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/50 mb-1.5">
            Trendlines · {trendlines.length}
          </p>
          <ul className="space-y-1">
            {trendlines.map((tl) => (
              <li
                key={tl.id}
                className="flex items-center justify-between text-[10px] font-mono"
              >
                <span className="text-muted-foreground/70 truncate">
                  {fmt(tl.fromPrice)} → {fmt(tl.toPrice)} ·{" "}
                  <span className={tl.toPrice > tl.fromPrice ? "text-emerald-400" : "text-rose-400"}>
                    {(((tl.toPrice - tl.fromPrice) / tl.fromPrice) * 100).toFixed(2)}%
                  </span>
                </span>
                <button
                  onClick={() => drawingStore.remove(symbol, tl.id)}
                  aria-label="Remove trendline"
                  className="ml-2 w-5 h-5 rounded flex items-center justify-center text-muted-foreground/40 hover:text-rose-400 hover:bg-rose-500/10 transition-colors"
                >
                  <svg width="9" height="9" viewBox="0 0 10 10" fill="none">
                    <path d="M2 2l6 6M8 2L2 8" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
                  </svg>
                </button>
              </li>
            ))}
          </ul>
        </div>
      )}

      {horizontals.length > 0 && (
        <ul className="border-t border-white/5">
          {horizontals.map((d) => (
            <li
              key={d.id}
              className="px-4 py-1.5 border-b border-white/[0.04] last:border-0 flex items-center justify-between gap-2 text-[11px] font-mono"
            >
              <div className="flex items-center gap-2">
                <span
                  className="w-2 h-2 rounded-full"
                  style={{ backgroundColor: d.color }}
                />
                <span className="tabular-nums">{fmt(d.price)}</span>
                {currentPrice && (
                  <span
                    className={cn(
                      "text-[9px]",
                      d.price > currentPrice ? "text-emerald-400/80" : "text-rose-400/80"
                    )}
                  >
                    {(((d.price - currentPrice) / currentPrice) * 100).toFixed(2)}%
                  </span>
                )}
              </div>
              <button
                onClick={() => drawingStore.remove(symbol, d.id)}
                aria-label={`Remove level at ${d.price}`}
                className="w-5 h-5 rounded flex items-center justify-center text-muted-foreground/40 hover:text-rose-400 hover:bg-rose-500/10 transition-colors"
              >
                <svg width="9" height="9" viewBox="0 0 10 10" fill="none">
                  <path d="M2 2l6 6M8 2L2 8" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
                </svg>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function fmt(v: number): string {
  if (v >= 1000) return v.toLocaleString(undefined, { maximumFractionDigits: 2 });
  if (v >= 1) return v.toFixed(2);
  return v.toFixed(5);
}
