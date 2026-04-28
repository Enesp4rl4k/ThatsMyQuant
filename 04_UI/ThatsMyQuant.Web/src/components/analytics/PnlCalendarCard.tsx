import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi, type EquityPoint } from "@/api/dashboard";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";

/**
 * GitHub-contribution-style daily P&L heatmap. Each cell = 1 day.
 * Color intensity scales with |dailyPnL| relative to the period max.
 * Green = positive, rose = negative, neutral grey = zero / no trades.
 */
export function PnlCalendarCard() {
  const [windowDays, setWindowDays] = useState(180);

  const q = useQuery({
    queryKey: ["portfolio", "equity-timeline"],
    queryFn: dashboardApi.equityTimeline,
    refetchInterval: 60_000,
  });

  const data = q.data ?? [];

  const stats = useMemo(() => {
    if (data.length === 0) {
      return { totalPnL: 0, posDays: 0, negDays: 0, bestDay: 0, worstDay: 0, hitRate: 0, max: 1 };
    }
    const sliced = data.slice(-windowDays);
    let total = 0, pos = 0, neg = 0, best = 0, worst = 0, max = 0;
    for (const p of sliced) {
      total += p.dailyPnL;
      if (p.dailyPnL > 0) pos++;
      else if (p.dailyPnL < 0) neg++;
      if (p.dailyPnL > best) best = p.dailyPnL;
      if (p.dailyPnL < worst) worst = p.dailyPnL;
      const abs = Math.abs(p.dailyPnL);
      if (abs > max) max = abs;
    }
    const tradingDays = pos + neg;
    return {
      totalPnL: total,
      posDays: pos,
      negDays: neg,
      bestDay: best,
      worstDay: worst,
      hitRate: tradingDays === 0 ? 0 : pos / tradingDays,
      max: max || 1,
    };
  }, [data, windowDays]);

  const grid = useMemo(() => buildCalendarGrid(data.slice(-windowDays)), [data, windowDays]);

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5">
      <div className="flex items-baseline justify-between mb-4">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
            Daily Returns
          </p>
          <p className="text-sm font-bold">
            P&amp;L Calendar{" "}
            <span className="text-muted-foreground/40 font-light text-xs">
              — last {windowDays}d · {stats.posDays}W · {stats.negDays}L · hit{" "}
              {(stats.hitRate * 100).toFixed(0)}%
            </span>
          </p>
        </div>
        <div className="flex gap-1">
          {[60, 120, 180, 365].map((d) => (
            <button
              key={d}
              onClick={() => setWindowDays(d)}
              className={cn(
                "px-2 py-1 rounded text-[9px] font-mono font-bold transition-colors",
                windowDays === d
                  ? "bg-accent/20 text-accent border border-accent/30"
                  : "text-muted-foreground/60 hover:bg-white/[0.05] border border-transparent"
              )}
            >
              {d}d
            </button>
          ))}
        </div>
      </div>

      {/* summary tiles */}
      <div className="grid grid-cols-2 md:grid-cols-4 gap-3 mb-5">
        <Tile label="Total P&L" value={formatUsd(stats.totalPnL)} tone={stats.totalPnL >= 0 ? "pos" : "neg"} />
        <Tile label="Best Day" value={formatUsd(stats.bestDay)} tone="pos" />
        <Tile label="Worst Day" value={formatUsd(stats.worstDay)} tone="neg" />
        <Tile label="Win Days" value={`${stats.posDays}/${stats.posDays + stats.negDays}`} tone="neutral" />
      </div>

      {/* heatmap */}
      {q.isLoading ? (
        <p className="text-xs text-muted-foreground/60 py-12 text-center">Loading calendar…</p>
      ) : data.length === 0 ? (
        <p className="text-xs text-muted-foreground/40 py-12 text-center">
          No closed-trade history yet — calendar will populate after first paper close.
        </p>
      ) : (
        <CalendarGrid grid={grid} maxAbs={stats.max} />
      )}

      <Legend className="mt-4" />
    </div>
  );
}

interface CalendarCell {
  date: string;
  pnL: number | null; // null = no data for this date
}

interface CalendarGridShape {
  weeks: CalendarCell[][]; // each week = 7 cells (Sun..Sat)
  monthLabels: { col: number; label: string }[];
}

function buildCalendarGrid(points: EquityPoint[]): CalendarGridShape {
  if (points.length === 0) return { weeks: [], monthLabels: [] };

  const map = new Map<string, number>();
  for (const p of points) {
    map.set(p.date.substring(0, 10), p.dailyPnL);
  }

  // Find date range
  const firstDate = new Date(points[0].date);
  const lastDate = new Date(points[points.length - 1].date);

  // Normalize to start of week (Sunday)
  const start = new Date(firstDate);
  start.setUTCHours(0, 0, 0, 0);
  start.setUTCDate(start.getUTCDate() - start.getUTCDay()); // back to previous Sunday

  const end = new Date(lastDate);
  end.setUTCHours(0, 0, 0, 0);

  const weeks: CalendarCell[][] = [];
  const monthLabels: { col: number; label: string }[] = [];
  const monthNames = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
  let lastMonth = -1;

  let cursor = new Date(start);
  let col = 0;
  while (cursor <= end) {
    const week: CalendarCell[] = [];
    for (let d = 0; d < 7; d++) {
      const iso = cursor.toISOString().substring(0, 10);
      const pnL = map.get(iso);
      // Only show data within the source range; before first data point => null
      const inRange = cursor >= firstDate && cursor <= lastDate;
      week.push({
        date: iso,
        pnL: inRange ? (pnL ?? 0) : null,
      });

      if (inRange && cursor.getUTCDate() <= 7) {
        const m = cursor.getUTCMonth();
        if (m !== lastMonth) {
          monthLabels.push({ col, label: monthNames[m] });
          lastMonth = m;
        }
      }

      cursor.setUTCDate(cursor.getUTCDate() + 1);
    }
    weeks.push(week);
    col++;
  }

  return { weeks, monthLabels };
}

function CalendarGrid({ grid, maxAbs }: { grid: CalendarGridShape; maxAbs: number }) {
  const cellSize = 11;
  const gap = 3;
  const dayLabelWidth = 22;
  const monthLabelHeight = 14;

  const width = grid.weeks.length * (cellSize + gap) + dayLabelWidth;
  const height = 7 * (cellSize + gap) + monthLabelHeight;

  return (
    <div className="overflow-x-auto">
      <svg width={width} height={height} className="block">
        {/* month labels */}
        {grid.monthLabels.map((m) => (
          <text
            key={`m-${m.col}-${m.label}`}
            x={dayLabelWidth + m.col * (cellSize + gap)}
            y={10}
            className="fill-muted-foreground/50"
            style={{ fontSize: 9, fontFamily: "ui-monospace, monospace" }}
          >
            {m.label}
          </text>
        ))}

        {/* day-of-week labels (Mon, Wed, Fri) */}
        {[1, 3, 5].map((d) => (
          <text
            key={`d-${d}`}
            x={0}
            y={monthLabelHeight + d * (cellSize + gap) + cellSize - 1}
            className="fill-muted-foreground/40"
            style={{ fontSize: 8, fontFamily: "ui-monospace, monospace" }}
          >
            {d === 1 ? "Mon" : d === 3 ? "Wed" : "Fri"}
          </text>
        ))}

        {/* cells */}
        {grid.weeks.map((week, wi) =>
          week.map((cell, di) => {
            const x = dayLabelWidth + wi * (cellSize + gap);
            const y = monthLabelHeight + di * (cellSize + gap);
            const fill = cellColor(cell.pnL, maxAbs);
            const tooltip = cell.pnL === null ? cell.date : `${cell.date}: ${formatUsd(cell.pnL)}`;
            return (
              <g key={`${wi}-${di}`}>
                <rect
                  x={x}
                  y={y}
                  width={cellSize}
                  height={cellSize}
                  rx={2}
                  fill={fill}
                  stroke={cell.pnL !== null && cell.pnL !== 0 ? "rgba(255,255,255,0.05)" : "transparent"}
                  strokeWidth={0.5}
                >
                  <title>{tooltip}</title>
                </rect>
              </g>
            );
          })
        )}
      </svg>
    </div>
  );
}

function cellColor(pnL: number | null, maxAbs: number): string {
  if (pnL === null) return "rgba(255,255,255,0.02)";
  if (pnL === 0) return "rgba(255,255,255,0.05)";
  const intensity = Math.min(1, Math.abs(pnL) / maxAbs);
  // 4 bins for clean visual stepping
  const step = intensity < 0.25 ? 0.25 : intensity < 0.5 ? 0.5 : intensity < 0.8 ? 0.75 : 1;
  if (pnL > 0) {
    // emerald — rgb(16, 185, 129)
    return `rgba(16, 185, 129, ${0.2 + step * 0.7})`;
  }
  // rose — rgb(244, 63, 94)
  return `rgba(244, 63, 94, ${0.2 + step * 0.7})`;
}

function Legend({ className }: { className?: string }) {
  return (
    <div className={cn("flex items-center gap-3 text-[9px] text-muted-foreground/50", className)}>
      <span className="font-mono uppercase tracking-widest">Less</span>
      <div className="flex gap-1">
        {[0.25, 0.5, 0.75, 1].map((s) => (
          <span
            key={`l-${s}`}
            className="w-2.5 h-2.5 rounded-sm"
            style={{ background: `rgba(244, 63, 94, ${0.2 + s * 0.7})` }}
          />
        ))}
        <span className="w-2.5 h-2.5 rounded-sm bg-white/[0.05]" />
        {[0.25, 0.5, 0.75, 1].map((s) => (
          <span
            key={`g-${s}`}
            className="w-2.5 h-2.5 rounded-sm"
            style={{ background: `rgba(16, 185, 129, ${0.2 + s * 0.7})` }}
          />
        ))}
      </div>
      <span className="font-mono uppercase tracking-widest">More</span>
    </div>
  );
}

function Tile({
  label,
  value,
  tone,
}: {
  label: string;
  value: string;
  tone: "pos" | "neg" | "neutral";
}) {
  return (
    <div className="rounded-md bg-white/[0.02] border border-white/5 px-3 py-2">
      <p className="text-[8px] font-black uppercase tracking-widest text-muted-foreground/40">{label}</p>
      <p
        className={cn(
          "font-mono font-black tabular-nums text-sm mt-0.5",
          tone === "pos" && "text-emerald-400",
          tone === "neg" && "text-rose-400"
        )}
      >
        {value}
      </p>
    </div>
  );
}
