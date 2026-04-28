import { useEffect, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { marketsApi, TIMEFRAMES, type Timeframe } from "@/api/markets";
import { CandleChart } from "@/components/charts/CandleChart";
import { cn } from "@/lib/utils";

interface Props {
  symbol: string;
  /** Initial position in viewport pixels. */
  initialX?: number;
  initialY?: number;
  zIndex?: number;
  onClose: () => void;
  onFocus?: () => void;
}

const W = 360;
const H = 240;

/**
 * Floating draggable mini chart card. Drag by the title bar; click anywhere
 * to bring to front. Persists nothing — closing forgets state.
 */
export function MiniChartPopout({
  symbol,
  initialX = 80,
  initialY = 80,
  zIndex = 50,
  onClose,
  onFocus,
}: Props) {
  const [pos, setPos] = useState({ x: initialX, y: initialY });
  const [tf, setTf] = useState<Timeframe>("1h");
  const dragRef = useRef<{ ox: number; oy: number; sx: number; sy: number } | null>(null);

  const q = useQuery({
    queryKey: ["markets", "candles", symbol, tf],
    queryFn: () => marketsApi.candles(symbol, tf, 80),
    refetchInterval: 30_000,
  });

  const onDragStart = (e: React.MouseEvent) => {
    onFocus?.();
    dragRef.current = {
      ox: e.clientX,
      oy: e.clientY,
      sx: pos.x,
      sy: pos.y,
    };
    e.preventDefault();
  };

  useEffect(() => {
    function onMove(e: MouseEvent) {
      if (!dragRef.current) return;
      const dx = e.clientX - dragRef.current.ox;
      const dy = e.clientY - dragRef.current.oy;
      // Constrain inside viewport
      const maxX = window.innerWidth - W - 8;
      const maxY = window.innerHeight - H - 8;
      setPos({
        x: Math.max(0, Math.min(maxX, dragRef.current.sx + dx)),
        y: Math.max(0, Math.min(maxY, dragRef.current.sy + dy)),
      });
    }
    function onUp() {
      dragRef.current = null;
    }
    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);
    return () => {
      window.removeEventListener("mousemove", onMove);
      window.removeEventListener("mouseup", onUp);
    };
  }, []);

  const candles = q.data ?? [];
  const last = candles[candles.length - 1];
  const first = candles[0];
  const change = last && first ? ((last.close - first.open) / first.open) * 100 : 0;
  const positive = change >= 0;

  return (
    <div
      style={{
        position: "fixed",
        left: pos.x,
        top: pos.y,
        width: W,
        zIndex,
      }}
      className="rounded-lg border border-white/10 bg-[#0A0E14]/95 backdrop-blur-xl shadow-paper-lg overflow-hidden select-none"
      onMouseDown={onFocus}
      role="dialog"
      aria-label={`Floating chart for ${symbol}`}
    >
      {/* Title bar (drag handle) */}
      <div
        onMouseDown={onDragStart}
        className="px-3 py-2 border-b border-white/5 flex items-center justify-between cursor-move bg-white/[0.02]"
      >
        <div className="flex items-center gap-2 min-w-0">
          <span className="font-bold text-[12px] tracking-tight">{symbol}</span>
          {last && (
            <>
              <span className="font-mono text-[11px] tabular-nums">
                {last.close >= 1
                  ? last.close.toFixed(2)
                  : last.close.toFixed(5)}
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
        <div className="flex items-center gap-1">
          <select
            value={tf}
            onChange={(e) => setTf(e.target.value as Timeframe)}
            onMouseDown={(e) => e.stopPropagation()}
            aria-label="Timeframe"
            className="bg-white/[0.04] border border-white/10 rounded px-1.5 py-0.5 text-[9px] font-mono cursor-pointer"
          >
            {TIMEFRAMES.map((t) => (
              <option key={t.value} value={t.value}>
                {t.label}
              </option>
            ))}
          </select>
          <button
            onClick={(e) => {
              e.stopPropagation();
              onClose();
            }}
            aria-label={`Close ${symbol} chart`}
            className="w-5 h-5 rounded flex items-center justify-center text-muted-foreground/50 hover:text-foreground hover:bg-white/[0.05]"
          >
            <svg width="10" height="10" viewBox="0 0 10 10" fill="none">
              <path d="M2 2l6 6M8 2L2 8" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
            </svg>
          </button>
        </div>
      </div>

      {/* Body */}
      <div className="p-1">
        {q.isLoading ? (
          <div
            className="flex items-center justify-center text-[10px] italic font-serif text-muted-foreground"
            style={{ height: H - 40 }}
          >
            — loading {symbol} —
          </div>
        ) : candles.length === 0 ? (
          <div
            className="flex items-center justify-center text-[10px] italic font-serif text-muted-foreground"
            style={{ height: H - 40 }}
          >
            — no data —
          </div>
        ) : (
          <CandleChart candles={candles} width={W - 8} height={H - 40} compact />
        )}
      </div>
    </div>
  );
}
