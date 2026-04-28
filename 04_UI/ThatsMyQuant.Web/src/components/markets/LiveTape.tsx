import { useEffect, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { marketsApi } from "@/api/markets";
import { cn } from "@/lib/utils";
import { SyntheticBadge } from "@/components/ui/SyntheticBadge";

interface Props {
  symbol: string;
}

interface Print {
  id: string;
  time: number;
  price: number;
  size: number;
  side: "buy" | "sell";
  isFresh?: boolean;
}

const MAX_PRINTS = 30;

/**
 * Live tape — derives synthetic prints from the latest candle close + a small
 * size distribution. Each poll, the latest close becomes the newest print at
 * the top, fading older ones down. When real exchange trade-tape integration
 * lands (WebSocket), swap the source — UI doesn't change.
 */
export function LiveTape({ symbol }: Props) {
  const [tape, setTape] = useState<Print[]>([]);
  const lastCloseRef = useRef<number | null>(null);

  const q = useQuery({
    queryKey: ["markets", "candles", symbol, "5m"],
    queryFn: () => marketsApi.candles(symbol, "5m", 30),
    refetchInterval: 5_000,
    enabled: !!symbol,
  });

  useEffect(() => {
    setTape([]);
    lastCloseRef.current = null;
  }, [symbol]);

  useEffect(() => {
    const candles = q.data;
    if (!candles || candles.length === 0) return;
    const last = candles[candles.length - 1];
    if (lastCloseRef.current === last.close && tape.length > 0) return;

    const now = Date.now();
    // Synthesize 1-3 prints per refresh, with side inferred from close-vs-open
    const greenBias = last.close >= last.open;
    const newPrints: Print[] = [];
    const count = 1 + Math.floor(Math.random() * 2);
    for (let i = 0; i < count; i++) {
      const wobble = (Math.random() - 0.5) * (last.high - last.low) * 0.3;
      const price = last.close + wobble;
      const size = +(Math.random() * 1.5 + 0.05).toFixed(3);
      const side: "buy" | "sell" = Math.random() < (greenBias ? 0.6 : 0.4) ? "buy" : "sell";
      newPrints.push({
        id: `${now}-${i}-${Math.random().toString(36).slice(2, 7)}`,
        time: now - i * 200,
        price,
        size,
        side,
        isFresh: true,
      });
    }

    setTape((prev) =>
      [
        ...newPrints,
        ...prev.map((p) => ({ ...p, isFresh: false })),
      ].slice(0, MAX_PRINTS)
    );
    lastCloseRef.current = last.close;

    // Clear isFresh flag after the fade-in animation completes
    const t = setTimeout(() => {
      setTape((prev) => prev.map((p) => ({ ...p, isFresh: false })));
    }, 800);
    return () => clearTimeout(t);
  }, [q.data, tape.length]);

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden h-full flex flex-col">
      <div className="px-4 py-2.5 border-b border-white/5 flex items-baseline justify-between">
        <div>
          <div className="flex items-center gap-1.5">
            <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
              Live Tape
            </p>
            <SyntheticBadge source="synthetic" />
          </div>
          <p className="text-[11px] font-serif italic text-muted-foreground/60 mt-0.5">
            recent prints · placeholder until WS tape lands
          </p>
        </div>
        <div className="flex items-center gap-1.5">
          <span className="w-1.5 h-1.5 rounded-full bg-amber-400 animate-pulse" />
          <span className="text-[9px] font-mono uppercase tracking-widest text-amber-400/80">
            demo
          </span>
        </div>
      </div>

      <div className="flex-1 overflow-y-auto">
        {tape.length === 0 ? (
          <p className="p-6 text-center text-xs italic font-serif text-muted-foreground/60">
            — waiting for prints —
          </p>
        ) : (
          <>
            <div className="grid grid-cols-3 px-4 py-1.5 border-b border-white/[0.04] text-[8px] uppercase tracking-widest text-muted-foreground/50">
              <span>Time</span>
              <span className="text-right">Price</span>
              <span className="text-right">Size</span>
            </div>
            <ul>
              {tape.map((p) => (
                <li
                  key={p.id}
                  className={cn(
                    "grid grid-cols-3 px-4 py-1 text-[11px] font-mono tabular-nums border-l-2 transition-colors",
                    p.side === "buy"
                      ? "border-l-emerald-500/50"
                      : "border-l-rose-500/50",
                    p.isFresh && (p.side === "buy"
                      ? "bg-emerald-500/[0.08]"
                      : "bg-rose-500/[0.08]")
                  )}
                >
                  <span className="text-muted-foreground/60">
                    {fmtTime(p.time)}
                  </span>
                  <span
                    className={cn(
                      "text-right",
                      p.side === "buy" ? "text-emerald-400" : "text-rose-400"
                    )}
                  >
                    {fmtPrice(p.price)}
                  </span>
                  <span className="text-right text-foreground/80">
                    {p.size.toFixed(3)}
                  </span>
                </li>
              ))}
            </ul>
          </>
        )}
      </div>
    </div>
  );
}

function fmtTime(ts: number): string {
  const d = new Date(ts);
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

function pad(n: number): string {
  return n.toString().padStart(2, "0");
}

function fmtPrice(v: number): string {
  if (v >= 1000) return v.toLocaleString(undefined, { maximumFractionDigits: 2 });
  if (v >= 1) return v.toFixed(2);
  return v.toFixed(5);
}
