import { useMemo } from "react";
import { useQueries } from "@tanstack/react-query";
import { marketsApi } from "@/api/markets";
import { cn } from "@/lib/utils";
import type { Position } from "@/api/types";

interface Props {
  positions: Position[];
  /** Number of recent closes used for correlation. Default 96 (4d on 1h, 16d on 1d). */
  bars?: number;
  /** Click handler — caller can pivot the chart to that symbol. */
  onSelect?: (symbol: string) => void;
}

/**
 * Pearson correlation matrix between every open position's recent returns.
 * The bot's `CorrelationBreakGuard` exits a pair when its rolling correlation
 * drops below the configured threshold — but it can't see CROSS-position
 * correlation spikes (e.g. all funding longs becoming co-directional in a
 * crypto-wide rally). This card surfaces that gap visually.
 *
 * Heatmap cells coloured:
 *   ρ ≥ +0.6 (high positive)  → orange/red — concentration risk
 *   ρ ≥ +0.3 (moderate +)     → amber
 *   ρ ≈ 0                     → grey-blue
 *   ρ ≤ -0.3                  → cyan (good diversifier)
 *
 * The diagonal is always 1.0 and uses a darker shade so the eye reads
 * off-diagonal hotspots immediately.
 */
export function CorrelationMatrix({ positions, bars = 96, onSelect }: Props) {
  const symbols = useMemo(() => {
    const set = new Set(positions.map((p) => p.symbol.toUpperCase()));
    return Array.from(set).sort();
  }, [positions]);

  // Fetch 1h candles for each symbol in parallel
  const candleQueries = useQueries({
    queries: symbols.map((sym) => ({
      queryKey: ["corr-candles", sym, bars],
      queryFn: () => marketsApi.candles(sym, "1h", bars),
      staleTime: 60_000,
      refetchInterval: 60_000,
    })),
  });

  // Compute correlation matrix
  const matrix = useMemo(() => {
    if (symbols.length < 2) return null;
    const allLoaded = candleQueries.every((q) => q.data && q.data.length >= 10);
    if (!allLoaded) return null;

    // For each symbol, derive log returns
    const returns: Record<string, number[]> = {};
    symbols.forEach((sym, i) => {
      const candles = candleQueries[i].data!;
      const r: number[] = [];
      for (let j = 1; j < candles.length; j++) {
        const prev = candles[j - 1].close;
        const cur  = candles[j].close;
        if (prev > 0 && cur > 0) r.push(Math.log(cur / prev));
      }
      returns[sym] = r;
    });

    // Pearson correlation
    function pearson(a: number[], b: number[]): number {
      const n = Math.min(a.length, b.length);
      if (n < 5) return 0;
      let sumA = 0, sumB = 0;
      for (let i = 0; i < n; i++) { sumA += a[i]; sumB += b[i]; }
      const meanA = sumA / n;
      const meanB = sumB / n;
      let num = 0, denA = 0, denB = 0;
      for (let i = 0; i < n; i++) {
        const da = a[i] - meanA;
        const db = b[i] - meanB;
        num += da * db;
        denA += da * da;
        denB += db * db;
      }
      const den = Math.sqrt(denA * denB);
      return den === 0 ? 0 : num / den;
    }

    const m: number[][] = symbols.map((s1) =>
      symbols.map((s2) => s1 === s2 ? 1.0 : pearson(returns[s1], returns[s2]))
    );

    return m;
  }, [symbols, candleQueries]);

  // Find max off-diagonal correlation as a concentration warning
  const maxOffDiag = useMemo(() => {
    if (!matrix) return null;
    let max = -1, ix = -1, iy = -1;
    for (let i = 0; i < matrix.length; i++) {
      for (let j = i + 1; j < matrix[i].length; j++) {
        if (matrix[i][j] > max) {
          max = matrix[i][j];
          ix = i; iy = j;
        }
      }
    }
    return ix >= 0 ? { value: max, a: symbols[ix], b: symbols[iy] } : null;
  }, [matrix, symbols]);

  if (positions.length < 2) {
    return (
      <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-2">
          🌐 Correlation Matrix
        </p>
        <p className="text-[11px] italic font-serif text-muted-foreground/55 text-center py-3">
          — needs ≥2 open positions on different symbols —
        </p>
      </div>
    );
  }

  if (!matrix) {
    return (
      <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-2">
          🌐 Correlation Matrix
        </p>
        <p className="text-[11px] italic font-serif text-muted-foreground/55 text-center py-3">
          — loading {symbols.length} candle series… —
        </p>
      </div>
    );
  }

  const concAlert = maxOffDiag && maxOffDiag.value >= 0.7;

  return (
    <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
      <div className="flex items-baseline justify-between mb-3">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
            🌐 Correlation Matrix
          </p>
          <p className="text-[10px] font-mono text-muted-foreground/45 mt-0.5">
            {symbols.length} positions · 1h returns · last {bars} bars
          </p>
        </div>
        {maxOffDiag && (
          <div className="text-right">
            <p className="text-[9px] uppercase tracking-widest text-muted-foreground/45">Peak</p>
            <p className={cn(
              "font-mono text-sm font-bold tabular-nums",
              concAlert ? "text-rose-300" : maxOffDiag.value >= 0.4 ? "text-amber-300" : "text-emerald-300"
            )}>
              ρ {maxOffDiag.value.toFixed(2)}
            </p>
          </div>
        )}
      </div>

      <div className="overflow-x-auto">
        <table className="text-[9px] font-mono mx-auto">
          <thead>
            <tr>
              <th className="px-1 py-1"></th>
              {symbols.map((s) => (
                <th key={s} className="px-1 py-1 text-muted-foreground/55 uppercase tracking-widest" style={{ writingMode: "vertical-rl", transform: "rotate(180deg)" }}>
                  {s}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {symbols.map((s1, i) => (
              <tr key={s1}>
                <td className="px-1 py-0.5 text-muted-foreground/55 uppercase tracking-widest text-right whitespace-nowrap">{s1}</td>
                {symbols.map((s2, j) => {
                  const v = matrix[i][j];
                  const isDiag = i === j;
                  return (
                    <td key={s2} className="p-0.5">
                      <div
                        onClick={() => onSelect?.(i === j ? s1 : (Math.abs(i-j) > 0 ? s2 : s1))}
                        className={cn(
                          "w-9 h-9 flex items-center justify-center rounded text-[9px] font-bold tabular-nums transition-transform hover:scale-110 cursor-pointer",
                          isDiag ? "opacity-25" : ""
                        )}
                        style={{ backgroundColor: corrColor(v), color: textColorFor(v) }}
                        title={`${s1} ↔ ${s2}: ρ = ${v.toFixed(3)}`}
                      >
                        {v.toFixed(2)}
                      </div>
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {concAlert && (
        <p className="mt-3 px-3 py-2 rounded-md border border-rose-400/30 bg-rose-500/10 text-rose-200 text-[10px] font-mono leading-relaxed">
          ⚠ <strong>{maxOffDiag.a}</strong> ↔ <strong>{maxOffDiag.b}</strong> correlation is{" "}
          <strong>ρ = {maxOffDiag.value.toFixed(2)}</strong>. These positions move nearly together — diversification benefit is gone, drawdown amplifies.
        </p>
      )}

      <div className="flex items-center justify-center gap-2 mt-3 text-[8px] font-mono uppercase tracking-widest text-muted-foreground/45">
        <span>−1</span>
        <span className="inline-block w-32 h-2 rounded-full" style={{
          background: "linear-gradient(to right, #0EA5E9 0%, #2D6A4F 25%, #3F3F46 50%, #F59E0B 75%, #E11D48 100%)",
        }} />
        <span>+1</span>
      </div>
    </div>
  );
}

// Color ramp from blue (-1) → grey (0) → red (+1)
function corrColor(v: number): string {
  const clamped = Math.max(-1, Math.min(1, v));
  if (clamped >= 0.7) return "rgba(225,29,72,0.85)";   // strong + red
  if (clamped >= 0.4) return "rgba(245,158,11,0.7)";   // amber
  if (clamped >= 0.2) return "rgba(245,158,11,0.4)";   // light amber
  if (clamped >= -0.2) return "rgba(63,63,70,0.6)";    // neutral grey
  if (clamped >= -0.4) return "rgba(14,165,233,0.4)";  // light cyan
  if (clamped >= -0.7) return "rgba(14,165,233,0.7)";  // cyan
  return "rgba(34,197,94,0.85)";                       // strong - green (great diversifier)
}

function textColorFor(v: number): string {
  return Math.abs(v) >= 0.4 ? "#FFFFFF" : "rgba(255,255,255,0.65)";
}
