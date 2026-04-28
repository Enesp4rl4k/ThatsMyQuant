import { useEffect, useRef, useState } from "react";
import { cn } from "@/lib/utils";

interface Props {
  equityCurve: number[];
  dates: string[];
  width?: number;
  height?: number;
}

const PAD = { top: 16, right: 12, bottom: 28, left: 60 };

export function ReplayChart({ equityCurve, dates, width = 1000, height = 280 }: Props) {
  const [cursor, setCursor] = useState(equityCurve.length - 1);
  const [playing, setPlaying] = useState(false);
  const [speed, setSpeed] = useState(50); // bars per second
  const rafRef = useRef<number | null>(null);
  const lastTickRef = useRef<number>(0);

  useEffect(() => {
    setCursor(equityCurve.length - 1);
    setPlaying(false);
  }, [equityCurve.length]);

  useEffect(() => {
    if (!playing) {
      if (rafRef.current !== null) cancelAnimationFrame(rafRef.current);
      return;
    }
    const tick = (now: number) => {
      const last = lastTickRef.current;
      if (last === 0) lastTickRef.current = now;
      const elapsed = now - lastTickRef.current;
      const advance = Math.floor((elapsed / 1000) * speed);
      if (advance > 0) {
        lastTickRef.current = now;
        setCursor((c) => {
          const next = c + advance;
          if (next >= equityCurve.length - 1) {
            setPlaying(false);
            return equityCurve.length - 1;
          }
          return next;
        });
      }
      rafRef.current = requestAnimationFrame(tick);
    };
    rafRef.current = requestAnimationFrame(tick);
    return () => {
      if (rafRef.current !== null) cancelAnimationFrame(rafRef.current);
      lastTickRef.current = 0;
    };
  }, [playing, speed, equityCurve.length]);

  if (equityCurve.length < 2) {
    return (
      <div style={{ width, height }} className="flex items-center justify-center text-xs text-muted-foreground/50">
        Insufficient data for replay
      </div>
    );
  }

  const inner = { w: width - PAD.left - PAD.right, h: height - PAD.top - PAD.bottom };

  const minV = Math.min(...equityCurve);
  const maxV = Math.max(...equityCurve);
  const range = maxV - minV || 1;

  const x = (i: number) => PAD.left + (i / (equityCurve.length - 1)) * inner.w;
  const y = (v: number) => PAD.top + inner.h * (1 - (v - minV) / range);

  // Visible portion (0..cursor)
  const visiblePath = equityCurve
    .slice(0, cursor + 1)
    .map((v, i) => `${i === 0 ? "M" : "L"}${x(i)},${y(v)}`)
    .join(" ");

  // Faded full path
  const fullPath = equityCurve
    .map((v, i) => `${i === 0 ? "M" : "L"}${x(i)},${y(v)}`)
    .join(" ");

  const cursorX = x(cursor);
  const cursorY = y(equityCurve[cursor]);
  const cursorDate = dates[cursor] ?? "";
  const cursorEquity = equityCurve[cursor];
  const start = equityCurve[0];
  const pnl = cursorEquity - start;
  const pnlPct = (cursorEquity / start - 1) * 100;

  const ticks = Array.from({ length: 5 }, (_, i) => {
    const v = minV + (range * i) / 4;
    return { v, y: y(v) };
  });

  return (
    <div className="space-y-3">
      <svg width={width} height={height} style={{ display: "block" }} className="font-mono">
        {ticks.map((t, i) => (
          <g key={i}>
            <line
              x1={PAD.left}
              x2={PAD.left + inner.w}
              y1={t.y}
              y2={t.y}
              stroke="rgba(31,30,27,0.06)"
              strokeDasharray={i === 0 || i === ticks.length - 1 ? "" : "2 4"}
              className="dark:stroke-[rgba(255,250,235,0.06)]"
            />
            <text
              x={PAD.left - 8}
              y={t.y + 4}
              textAnchor="end"
              fill="rgba(31,30,27,0.45)"
              fontSize={10}
              className="dark:fill-[rgba(255,250,235,0.5)]"
            >
              ${(t.v / 1000).toFixed(0)}k
            </text>
          </g>
        ))}

        {/* Faded "future" trace */}
        <path d={fullPath} fill="none" stroke="rgba(31,30,27,0.12)" strokeWidth={1} className="dark:stroke-[rgba(255,250,235,0.1)]" />

        {/* Played-up-to-cursor */}
        <path d={visiblePath} fill="none" stroke="#1E3A8A" strokeWidth={1.75} strokeLinejoin="round" />

        {/* Cursor crosshair */}
        <line
          x1={cursorX}
          x2={cursorX}
          y1={PAD.top}
          y2={PAD.top + inner.h}
          stroke="rgba(31,30,27,0.3)"
          strokeWidth={1}
          strokeDasharray="3 3"
          className="dark:stroke-[rgba(255,250,235,0.4)]"
        />
        <circle cx={cursorX} cy={cursorY} r={4} fill="white" stroke="#1E3A8A" strokeWidth={2} />

        <line
          x1={PAD.left}
          x2={PAD.left + inner.w}
          y1={PAD.top + inner.h}
          y2={PAD.top + inner.h}
          stroke="rgba(31,30,27,0.12)"
          className="dark:stroke-[rgba(255,250,235,0.08)]"
        />
      </svg>

      {/* Replay controls */}
      <div className="rounded-lg border border-white/5 bg-black/40 p-3 space-y-2">
        <div className="flex items-center gap-3">
          <button
            onClick={() => {
              if (cursor >= equityCurve.length - 1) setCursor(0);
              setPlaying((p) => !p);
            }}
            className={cn(
              "px-4 py-1.5 rounded text-[11px] font-black uppercase tracking-wider border",
              playing
                ? "bg-rose-500/15 text-rose-400 border-rose-500/40"
                : "bg-accent/15 text-accent border-accent/40"
            )}
          >
            {playing ? "⏸ PAUSE" : "▶ PLAY"}
          </button>
          <button
            onClick={() => {
              setPlaying(false);
              setCursor(0);
            }}
            className="px-3 py-1.5 rounded text-[11px] font-bold uppercase tracking-wider border border-white/10 bg-white/[0.02] hover:bg-white/[0.05]"
          >
            ⏮ Reset
          </button>
          <span className="text-[10px] uppercase tracking-widest text-muted-foreground/50">Speed</span>
          <input
            type="range"
            min={5}
            max={500}
            value={speed}
            onChange={(e) => setSpeed(Number(e.target.value))}
            className="flex-1 max-w-[200px] accent-[#1E3A8A]"
          />
          <span className="font-mono text-xs text-muted-foreground w-16 text-right">{speed} bars/s</span>
        </div>

        <input
          type="range"
          min={0}
          max={equityCurve.length - 1}
          value={cursor}
          onChange={(e) => {
            setPlaying(false);
            setCursor(Number(e.target.value));
          }}
          className="w-full accent-[#1E3A8A]"
        />

        <div className="flex items-center justify-between text-[10px] font-mono">
          <span className="text-muted-foreground/50">{dates[0] ?? "Start"}</span>
          <div className="flex items-center gap-4">
            <span className="text-foreground/70">{cursorDate.slice(0, 10)}</span>
            <span className="font-bold">${cursorEquity.toFixed(0)}</span>
            <span
              className={cn(
                "font-bold",
                pnl >= 0 ? "text-emerald-400" : "text-rose-400"
              )}
            >
              {pnl >= 0 ? "+" : ""}
              {pnl.toFixed(0)} ({pnlPct >= 0 ? "+" : ""}
              {pnlPct.toFixed(2)}%)
            </span>
          </div>
          <span className="text-muted-foreground/50">{dates[dates.length - 1] ?? "End"}</span>
        </div>
      </div>
    </div>
  );
}
