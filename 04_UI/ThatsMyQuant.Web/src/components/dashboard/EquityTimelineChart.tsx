import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";
import { EmptyState } from "@/components/ui/EmptyState";

export function EquityTimelineChart() {
  const q = useQuery({
    queryKey: ["portfolio", "equity-timeline"],
    queryFn: dashboardApi.equityTimeline,
    refetchInterval: 60_000,
  });

  const points = q.data ?? [];

  const stats = useMemo(() => {
    if (points.length === 0) return null;
    const eq = points.map((p) => p.cumulativeEquity);
    const peak = Math.max(...eq);
    const last = eq[eq.length - 1];
    const drawdown = peak > 0 ? ((peak - last) / peak) * 100 : 0;
    return {
      peak,
      last,
      drawdown,
      totalDays: points.length,
      totalTrades: points.reduce((s, p) => s + p.tradeCount, 0),
      bestDay: points.reduce((b, p) => (p.dailyPnL > b.dailyPnL ? p : b), points[0]),
      worstDay: points.reduce((b, p) => (p.dailyPnL < b.dailyPnL ? p : b), points[0]),
    };
  }, [points]);

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5">
      <div className="flex items-baseline justify-between mb-4">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
            Daily Equity Curve
          </p>
          <p className="text-sm font-bold">
            Realized PnL{" "}
            <span className="text-muted-foreground/40 font-light text-xs">
              — cumulative across closed trades by exit day
            </span>
          </p>
        </div>
        {stats && (
          <div className="flex items-center gap-5 text-[10px]">
            <Stat label="Peak" value={formatUsd(stats.peak)} />
            <Stat
              label="Drawdown"
              value={`${stats.drawdown.toFixed(2)}%`}
              tone={stats.drawdown > 1 ? "neg" : "neutral"}
            />
            <Stat label="Days" value={stats.totalDays.toString()} />
            <Stat label="Trades" value={stats.totalTrades.toString()} />
          </div>
        )}
      </div>

      {q.isLoading ? (
        <p className="text-xs text-muted-foreground/60">Loading timeline…</p>
      ) : points.length === 0 ? (
        <EmptyState
          icon="trades"
          title="No closed trades yet"
          description="The equity curve activates after the first paper-trade closes. Arm the bot in Pro Mode to start scanning for setups."
          cta={{ label: "Open Pro Mode", to: "/pro-mode" }}
        />
      ) : (
        <Curve points={points} />
      )}

      {stats && points.length > 0 && (
        <div className="grid grid-cols-2 gap-4 mt-3 pt-3 border-t border-white/5 text-[10px]">
          <div>
            <p className="text-muted-foreground/40 uppercase tracking-widest text-[9px] mb-1">Best Day</p>
            <p className="font-mono">
              {stats.bestDay.date} <span className="text-emerald-400">{formatUsd(stats.bestDay.dailyPnL)}</span>
            </p>
          </div>
          <div className="text-right">
            <p className="text-muted-foreground/40 uppercase tracking-widest text-[9px] mb-1">Worst Day</p>
            <p className="font-mono">
              {stats.worstDay.date} <span className="text-rose-400">{formatUsd(stats.worstDay.dailyPnL)}</span>
            </p>
          </div>
        </div>
      )}
    </div>
  );
}

function Curve({ points }: { points: Array<{ date: string; cumulativeEquity: number; dailyPnL: number }> }) {
  const w = 800;
  const h = 180;
  const pad = { l: 50, r: 12, t: 8, b: 18 };

  const eq = points.map((p) => p.cumulativeEquity);
  const min = Math.min(0, ...eq);
  const max = Math.max(0, ...eq);
  const range = max - min || 1;

  const xStep = points.length > 1 ? (w - pad.l - pad.r) / (points.length - 1) : 0;
  const yFor = (v: number) => pad.t + (h - pad.t - pad.b) * (1 - (v - min) / range);

  const linePath = points
    .map((p, i) => `${i === 0 ? "M" : "L"} ${(pad.l + i * xStep).toFixed(1)} ${yFor(p.cumulativeEquity).toFixed(1)}`)
    .join(" ");

  const areaPath =
    linePath +
    ` L ${(pad.l + (points.length - 1) * xStep).toFixed(1)} ${yFor(0).toFixed(1)} L ${pad.l} ${yFor(0).toFixed(1)} Z`;

  // Y-axis ticks (5)
  const ticks = [0, 0.25, 0.5, 0.75, 1].map((t) => min + range * t);

  return (
    <svg viewBox={`0 0 ${w} ${h}`} className="w-full h-auto block">
      <defs>
        <linearGradient id="equityGrad" x1="0" x2="0" y1="0" y2="1">
          <stop offset="0%" stopColor="rgba(30, 58, 138, 0.18)" />
          <stop offset="100%" stopColor="rgba(30, 58, 138, 0)" />
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
            className="dark:stroke-[rgba(255,250,235,0.06)]"
            strokeDasharray={i === 0 || t === 0 ? "" : "2 4"}
          />
          <text
            x={pad.l - 8}
            y={yFor(t) + 3}
            textAnchor="end"
            className="fill-muted-foreground/50"
            fontSize="9"
            fontFamily="ui-monospace, monospace"
          >
            ${t >= 1000 ? `${(t / 1000).toFixed(1)}k` : t.toFixed(0)}
          </text>
        </g>
      ))}
      {/* Zero line */}
      <line
        x1={pad.l}
        x2={w - pad.r}
        y1={yFor(0)}
        y2={yFor(0)}
        stroke="rgba(31,30,27,0.22)"
        className="dark:stroke-[rgba(255,250,235,0.18)]"
      />
      <path d={areaPath} fill="url(#equityGrad)" />
      <path d={linePath} fill="none" stroke="#1E3A8A" strokeWidth="1.75" strokeLinejoin="round" />
      {/* End-point marker */}
      {points.length > 0 && (
        <circle
          cx={pad.l + (points.length - 1) * xStep}
          cy={yFor(points[points.length - 1].cumulativeEquity)}
          r="4"
          fill="white"
          stroke="#1E3A8A"
          strokeWidth="2"
        />
      )}
      {/* X-axis: first & last date */}
      {points.length > 1 && (
        <>
          <text
            x={pad.l}
            y={h - 4}
            className="fill-muted-foreground/40"
            fontSize="9"
            fontFamily="ui-monospace, monospace"
          >
            {points[0].date}
          </text>
          <text
            x={w - pad.r}
            y={h - 4}
            textAnchor="end"
            className="fill-muted-foreground/40"
            fontSize="9"
            fontFamily="ui-monospace, monospace"
          >
            {points[points.length - 1].date}
          </text>
        </>
      )}
    </svg>
  );
}

function Stat({ label, value, tone = "neutral" }: { label: string; value: string; tone?: "pos" | "neg" | "neutral" }) {
  return (
    <div className="flex flex-col items-end leading-none">
      <span className="text-[9px] uppercase tracking-widest text-muted-foreground/40 mb-0.5">
        {label}
      </span>
      <span
        className={cn(
          "font-mono font-black tabular-nums",
          tone === "pos" && "text-emerald-400",
          tone === "neg" && "text-rose-400"
        )}
      >
        {value}
      </span>
    </div>
  );
}
