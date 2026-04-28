import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { cn } from "@/lib/utils";

interface Props {
  window?: number;        // rolling window size (default 20)
  longWindow?: number;    // second line (default 50)
  className?: string;
}

/**
 * Edge Decay Monitor — rolling win-rate and profit-factor over the last N trades.
 * This is the single most important chart for live strategy monitoring:
 * if rolling-20 WR falls below inception WR by >15pp, the edge may be decaying.
 *
 * Shows:
 *   • Rolling-N win-rate (short) — orange line
 *   • Rolling-M win-rate (long) — blue line
 *   • Inception (all-time) WR — grey dashed baseline
 *   • Color-coded verdict: STABLE / MONITORING / DECAY RISK
 */
export function EdgeDecayChart({ window: W = 20, longWindow: LW = 50, className }: Props) {
  const q = useQuery({
    queryKey: ["edge-decay", "trades"],
    queryFn: () => dashboardApi.recentTrades(500),
    staleTime: 60_000,
  });

  const data = useMemo(() => {
    const trades = (q.data ?? [])
      .filter((t) => t.status?.toLowerCase() !== "open")
      .sort((a, b) => new Date(a.exitTime ?? a.entryTime).getTime() - new Date(b.exitTime ?? b.entryTime).getTime());

    if (trades.length < W) return null;

    const wins = trades.map((t) => ((t.realizedPnL ?? 0) > 0 ? 1 : 0) as number);
    const inceptionWR = wins.reduce((s: number, v) => s + v, 0) / wins.length;

    const shortRolling: (number | null)[] = trades.map((_, i) => {
      if (i < W - 1) return null;
      const slice = wins.slice(i - W + 1, i + 1);
      return slice.reduce((s: number, v) => s + v, 0) / W;
    });

    const longRolling: (number | null)[] = trades.map((_, i) => {
      if (i < LW - 1) return null;
      const slice = wins.slice(i - LW + 1, i + 1);
      return slice.reduce((s: number, v) => s + v, 0) / LW;
    });

    // Rolling PF (short window)
    const shortPf: (number | null)[] = trades.map((_, i) => {
      if (i < W - 1) return null;
      const slice = trades.slice(i - W + 1, i + 1);
      let pos = 0, neg = 0;
      for (const t of slice) {
        const p = t.realizedPnL ?? 0;
        if (p > 0) pos += p; else neg -= p;
      }
      return neg > 0 ? pos / neg : pos > 0 ? 99 : null;
    });

    const lastShort = shortRolling.at(-1) ?? 0;
    const drift = lastShort - inceptionWR;
    const verdict: "STABLE" | "MONITORING" | "DECAY" =
      drift >= -0.05 ? "STABLE" :
      drift >= -0.15 ? "MONITORING" : "DECAY";

    return { shortRolling, longRolling, shortPf, inceptionWR, tradeCount: trades.length, lastShort, drift, verdict };
  }, [q.data, W, LW]);

  if (!data) {
    return (
      <div className={cn("rounded-2xl border border-white/5 bg-[#0D1117]/60 p-4", className)}>
        <p className="text-[9px] uppercase tracking-[0.25em] text-accent/70 mb-2">Edge Decay Monitor</p>
        <p className="text-xs italic text-muted-foreground/50 text-center py-6">
          — need ≥{W} closed trades —
        </p>
      </div>
    );
  }

  const vColor = data.verdict === "STABLE" ? "#37A95B" : data.verdict === "MONITORING" ? "#F0883E" : "#F05A5A";
  const n = data.shortRolling.length;
  const W_SVG = 600;
  const H_SVG = 140;
  const PAD = { t: 24, r: 16, b: 24, l: 48 };
  const iW = W_SVG - PAD.l - PAD.r;
  const iH = H_SVG - PAD.t - PAD.b;
  const step = iW / Math.max(1, n - 1);

  const scX = (i: number) => PAD.l + i * step;
  const scY = (v: number) => PAD.t + iH * (1 - v); // WR is already [0,1]

  const mkPath = (vals: (number | null)[]) => {
    let d = "";
    for (let i = 0; i < vals.length; i++) {
      const v = vals[i];
      if (v == null) continue;
      d += d ? ` L${scX(i).toFixed(1)},${scY(v).toFixed(1)}` : `M${scX(i).toFixed(1)},${scY(v).toFixed(1)}`;
    }
    return d || null;
  };

  const shortPath = mkPath(data.shortRolling);
  const longPath = mkPath(data.longRolling);
  const baselineY = scY(data.inceptionWR);

  // Grid lines at 25/50/75%
  const gridLevels = [0.25, 0.5, 0.75, 1.0];

  return (
    <div className={cn("rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4", className)}>
      <div className="flex items-baseline justify-between mb-3">
        <div>
          <p className="text-[9px] uppercase tracking-[0.25em] text-accent/70">Edge Decay Monitor</p>
          <p className="text-[11px] text-muted-foreground/50 mt-0.5">
            {data.tradeCount} trades · rolling-{W} vs rolling-{LW} win-rate
          </p>
        </div>
        <div className="flex items-center gap-3">
          <div className="text-right text-[10px]">
            <p className="text-muted-foreground/50">Inception WR</p>
            <p className="font-mono font-bold text-foreground/80">{(data.inceptionWR * 100).toFixed(1)}%</p>
          </div>
          <div className="text-right text-[10px]">
            <p className="text-muted-foreground/50">Recent-{W}</p>
            <p className="font-mono font-bold" style={{ color: vColor }}>{(data.lastShort * 100).toFixed(1)}%</p>
          </div>
          <span
            className="px-2 py-1 rounded text-[9px] font-black uppercase tracking-wider"
            style={{ backgroundColor: `${vColor}22`, color: vColor }}
          >
            {data.verdict === "DECAY" ? "⚠ " : ""}{data.verdict}
          </span>
        </div>
      </div>

      <div className="overflow-x-auto">
        <svg width={W_SVG} height={H_SVG} style={{ display: "block" }}>
          {/* Grid */}
          {gridLevels.map((lvl) => (
            <g key={lvl}>
              <line
                x1={PAD.l} x2={PAD.l + iW}
                y1={scY(lvl)} y2={scY(lvl)}
                stroke="rgba(255,255,255,0.05)" strokeWidth={0.7}
              />
              <text x={PAD.l - 4} y={scY(lvl) + 3} fontSize={8} textAnchor="end" fill="rgba(255,255,255,0.3)" fontFamily="ui-monospace">
                {(lvl * 100).toFixed(0)}
              </text>
            </g>
          ))}

          {/* Inception baseline */}
          <line
            x1={PAD.l} x2={PAD.l + iW}
            y1={baselineY} y2={baselineY}
            stroke="rgba(255,255,255,0.25)" strokeWidth={1} strokeDasharray="4 4"
          />
          <text x={PAD.l + iW + 2} y={baselineY + 3} fontSize={8} fill="rgba(255,255,255,0.4)" fontFamily="ui-monospace">
            inception
          </text>

          {/* Long rolling (blue, behind) */}
          {longPath && (
            <path d={longPath} fill="none" stroke="#1E3A8A" strokeWidth={1.2} strokeOpacity={0.7} strokeLinejoin="round" />
          )}

          {/* Short rolling (orange, front) */}
          {shortPath && (
            <path d={shortPath} fill="none" stroke="#F0883E" strokeWidth={1.6} strokeLinejoin="round" />
          )}

          {/* Last point dot + label */}
          {(() => {
            const lastIdx = data.shortRolling.reduceRight<number>((found, v, i) => found < 0 && v != null ? i : found, -1);
            if (lastIdx < 0) return null;
            const lx = scX(lastIdx);
            const ly = scY(data.lastShort);
            return (
              <g>
                <circle cx={lx} cy={ly} r={3.5} fill="#F0883E" />
                <text x={lx} y={ly - 6} fontSize={8} textAnchor="middle" fill="#F0883E" fontWeight={700} fontFamily="ui-monospace">
                  {(data.lastShort * 100).toFixed(1)}%
                </text>
              </g>
            );
          })()}

          {/* Decay zone fill (below inception − 15pp) */}
          <rect
            x={PAD.l} y={scY(Math.max(0, data.inceptionWR - 0.15))}
            width={iW}
            height={iH - (scY(Math.max(0, data.inceptionWR - 0.15)) - PAD.t)}
            fill="rgba(240,90,90,0.04)"
          />
        </svg>
      </div>

      {/* Legend */}
      <div className="flex items-center gap-4 mt-1 text-[9px] font-mono text-muted-foreground/50">
        <span><span className="inline-block w-6 h-0.5 bg-[#F0883E] mr-1 align-middle" />Rolling-{W}</span>
        <span><span className="inline-block w-6 h-0.5 bg-[#1E3A8A] mr-1 align-middle opacity-70" />Rolling-{LW}</span>
        <span><span className="inline-block w-4 border-t border-white/30 border-dashed mr-1 align-middle" />Inception</span>
        {data.drift < -0.05 && (
          <span className="text-rose-400/70">
            drift {data.drift >= 0 ? "+" : ""}{(data.drift * 100).toFixed(1)}pp
          </span>
        )}
      </div>
    </div>
  );
}
