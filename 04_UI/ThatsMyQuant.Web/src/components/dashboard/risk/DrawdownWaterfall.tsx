import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi, type EquityPoint } from "@/api/dashboard";
import { cn } from "@/lib/utils";

/**
 * Underwater chart — area between equity peak and current. The deeper you
 * are below the running peak, the redder the area; flat at zero = at peak.
 * Renders a small recovery gauge alongside.
 */
export function DrawdownWaterfall() {
  const q = useQuery({
    queryKey: ["portfolio", "equity-timeline"],
    queryFn: dashboardApi.equityTimeline,
    refetchInterval: 60_000,
  });

  const series = useMemo(() => {
    const data = q.data ?? [];
    if (data.length === 0) return null;

    let peak = data[0].cumulativeEquity;
    let peakIdx = 0;
    let maxDd = 0;
    let maxDdIdx = 0;
    const pts = data.map((p, i) => {
      if (p.cumulativeEquity > peak) {
        peak = p.cumulativeEquity;
        peakIdx = i;
      }
      const dd = peak !== 0 ? (peak - p.cumulativeEquity) / Math.abs(peak) : 0;
      if (dd > maxDd) {
        maxDd = dd;
        maxDdIdx = i;
      }
      return {
        date: p.date,
        equity: p.cumulativeEquity,
        peakSoFar: peak,
        ddPct: -dd * 100,
      };
    });

    return {
      pts,
      maxDdPct: maxDd * 100,
      maxDdDate: data[maxDdIdx]?.date ?? null,
      currentDdPct: pts[pts.length - 1].ddPct,
      lastPeakDate: data[peakIdx]?.date ?? null,
      daysUnderwater: pts.length - 1 - peakIdx,
    };
  }, [q.data]);

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5">
      <div className="flex items-baseline justify-between mb-3">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-rose-400/70 mb-0.5">
            Drawdown
          </p>
          <p className="text-sm font-bold">
            Underwater Curve{" "}
            <span className="text-muted-foreground/40 font-light text-xs">
              — peak-to-current %
            </span>
          </p>
        </div>
        {series && (
          <div className="flex items-center gap-3 text-[10px] font-mono">
            <Stat
              label="Current"
              value={`${series.currentDdPct.toFixed(2)}%`}
              tone={series.currentDdPct > -1 ? "pos" : series.currentDdPct > -3 ? "warn" : "neg"}
            />
            <Stat
              label="Max"
              value={`-${series.maxDdPct.toFixed(2)}%`}
              tone="neg"
            />
            <Stat
              label="Underwater"
              value={`${series.daysUnderwater}d`}
              tone={series.daysUnderwater > 14 ? "warn" : "neutral"}
            />
          </div>
        )}
      </div>

      {q.isLoading ? (
        <p className="text-xs italic font-serif text-muted-foreground/60 text-center py-12">
          — loading underwater curve —
        </p>
      ) : !series ? (
        <p className="text-xs italic font-serif text-muted-foreground/60 text-center py-12">
          — no equity history yet —
        </p>
      ) : (
        <Waterfall series={series.pts} />
      )}
    </div>
  );
}

function Waterfall({ series }: { series: Array<{ date: string; ddPct: number }> }) {
  const w = 800;
  const h = 140;
  const pad = { l: 40, r: 12, t: 8, b: 22 };

  if (series.length < 2) return null;
  const minPct = Math.min(0, ...series.map((p) => p.ddPct));
  const range = -minPct || 1; // positive number representing depth

  const xStep = (w - pad.l - pad.r) / Math.max(1, series.length - 1);
  const yFor = (pct: number) =>
    pad.t + ((Math.abs(pct) / range) * (h - pad.t - pad.b));

  const linePath = series
    .map((p, i) => `${i === 0 ? "M" : "L"} ${(pad.l + i * xStep).toFixed(1)} ${yFor(p.ddPct).toFixed(1)}`)
    .join(" ");

  const areaPath =
    linePath +
    ` L ${(pad.l + (series.length - 1) * xStep).toFixed(1)} ${pad.t} L ${pad.l} ${pad.t} Z`;

  // Y-axis ticks (5)
  const ticks = [0, 0.25, 0.5, 0.75, 1].map((t) => -range * t);

  return (
    <svg viewBox={`0 0 ${w} ${h}`} className="w-full h-auto block font-mono">
      <defs>
        <linearGradient id="ddGrad" x1="0" x2="0" y1="0" y2="1">
          <stop offset="0%" stopColor="rgba(181, 54, 75, 0)" />
          <stop offset="100%" stopColor="rgba(181, 54, 75, 0.4)" />
        </linearGradient>
      </defs>

      {ticks.map((t, i) => (
        <g key={i}>
          <line
            x1={pad.l}
            x2={w - pad.r}
            y1={yFor(t)}
            y2={yFor(t)}
            stroke="rgba(31,30,27,0.06)"
            strokeDasharray={i === 0 ? "" : "2 4"}
            className="dark:stroke-[rgba(255,250,235,0.06)]"
          />
          <text
            x={pad.l - 6}
            y={yFor(t) + 3}
            textAnchor="end"
            className="fill-muted-foreground/50"
            fontSize="9"
          >
            {t === 0 ? "0%" : `${t.toFixed(1)}%`}
          </text>
        </g>
      ))}

      {/* Area + line */}
      <path d={areaPath} fill="url(#ddGrad)" />
      <path d={linePath} fill="none" stroke="#B5364B" strokeWidth="1.6" strokeLinejoin="round" />

      {/* Zero baseline (peak line) */}
      <line
        x1={pad.l}
        x2={w - pad.r}
        y1={pad.t}
        y2={pad.t}
        stroke="rgba(45, 106, 79, 0.4)"
        strokeWidth="1.2"
        strokeDasharray="4 3"
      />

      {/* End-point marker */}
      <circle
        cx={pad.l + (series.length - 1) * xStep}
        cy={yFor(series[series.length - 1].ddPct)}
        r="3.5"
        fill="white"
        stroke="#B5364B"
        strokeWidth="2"
      />

      {/* X-axis date labels */}
      <text x={pad.l} y={h - 6} className="fill-muted-foreground/50" fontSize="9" fontStyle="italic" fontFamily="Newsreader, serif">
        {series[0].date}
      </text>
      <text
        x={w - pad.r}
        y={h - 6}
        textAnchor="end"
        className="fill-muted-foreground/50"
        fontSize="9"
        fontStyle="italic"
        fontFamily="Newsreader, serif"
      >
        {series[series.length - 1].date}
      </text>
    </svg>
  );
}

function Stat({
  label,
  value,
  tone,
}: {
  label: string;
  value: string;
  tone: "pos" | "warn" | "neg" | "neutral";
}) {
  return (
    <div className="flex items-baseline gap-1.5">
      <span className="text-[8px] uppercase tracking-widest text-muted-foreground/50">{label}</span>
      <span
        className={cn(
          "tabular-nums font-bold",
          tone === "pos" && "text-emerald-400",
          tone === "warn" && "text-amber-400",
          tone === "neg" && "text-rose-400"
        )}
      >
        {value}
      </span>
    </div>
  );
}

// Re-export type so consumers can avoid duplicate imports
export type { EquityPoint };
