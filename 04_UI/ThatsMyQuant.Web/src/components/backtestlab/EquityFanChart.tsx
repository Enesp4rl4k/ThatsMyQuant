import { useMemo } from "react";
import type { MonteCarloFanResult } from "@/api/backtestLab";

interface Props {
  fan: MonteCarloFanResult;
  width?: number;
  height?: number;
}

const PAD = { top: 16, right: 12, bottom: 28, left: 64 };

/**
 * Per-bar percentile-envelope (fan) chart of bootstrap equity paths, with the
 * realised path overlaid. Server-side computation (see C# MonteCarloFanService);
 * this component is purely presentation.
 *
 * Visual encoding:
 *   - outer band (5–95):   pale fill, "plausible alternative-realisation envelope"
 *   - inner band (25–75):  brighter fill, "most likely path range"
 *   - median (p50):        thin dashed line
 *   - realised:            bold accent line
 *
 * The operator uses this to answer: was the realised path lucky? If it lives
 * inside the 25–75 band most of the time, the strategy looks plausible. If it
 * hugs the 95th percentile, you got a flukey draw.
 */
export function EquityFanChart({ fan, width = 760, height = 320 }: Props) {
  const view = useMemo(() => buildView(fan, width, height), [fan, width, height]);

  if (view === null) {
    return (
      <div className="text-xs text-muted-foreground p-4">
        Not enough data to render fan chart.
      </div>
    );
  }

  return (
    <svg
      width={width}
      height={height}
      role="img"
      aria-label="Monte Carlo equity fan with realised overlay"
      className="block"
    >
      {/* Y-axis grid + labels */}
      {view.yTicks.map((t) => (
        <g key={t.value}>
          <line
            x1={PAD.left}
            x2={width - PAD.right}
            y1={t.y}
            y2={t.y}
            stroke="rgba(255,255,255,0.04)"
            strokeWidth={1}
          />
          <text
            x={PAD.left - 8}
            y={t.y + 3}
            fill="rgba(255,255,255,0.4)"
            fontSize={10}
            textAnchor="end"
            fontFamily="ui-monospace, SFMono-Regular, monospace"
          >
            {formatUsd(t.value)}
          </text>
        </g>
      ))}

      {/* X-axis labels */}
      {view.xTicks.map((t, i) => (
        <text
          key={i}
          x={t.x}
          y={height - PAD.bottom + 16}
          fill="rgba(255,255,255,0.4)"
          fontSize={10}
          textAnchor="middle"
        >
          {t.label}
        </text>
      ))}

      {/* 5–95 band */}
      <path d={view.band05_95} fill="rgba(0, 191, 255, 0.08)" />
      {/* 25–75 band */}
      <path d={view.band25_75} fill="rgba(0, 191, 255, 0.18)" />
      {/* Median dashed */}
      <path
        d={view.median}
        fill="none"
        stroke="rgba(0, 191, 255, 0.6)"
        strokeWidth={1.25}
        strokeDasharray="4 3"
      />
      {/* Realised — accent emphasis */}
      <path
        d={view.realized}
        fill="none"
        stroke="rgba(110, 231, 183, 0.95)"
        strokeWidth={1.75}
      />

      {/* Legend */}
      <g transform={`translate(${PAD.left}, ${PAD.top - 4})`}>
        <LegendItem x={0}   color="rgba(0, 191, 255, 0.18)" label="25–75%" filled />
        <LegendItem x={80}  color="rgba(0, 191, 255, 0.08)" label="5–95%" filled />
        <LegendItem x={160} color="rgba(0, 191, 255, 0.6)" label="Median" dashed />
        <LegendItem x={230} color="rgba(110, 231, 183, 0.95)" label="Realised" />
      </g>
    </svg>
  );
}

interface ViewModel {
  band05_95: string;
  band25_75: string;
  median: string;
  realized: string;
  xTicks: { x: number; label: string }[];
  yTicks: { y: number; value: number }[];
}

function buildView(fan: MonteCarloFanResult, width: number, height: number): ViewModel | null {
  const n = fan.dates.length;
  if (n < 2) return null;

  const innerWidth = width - PAD.left - PAD.right;
  const innerHeight = height - PAD.top - PAD.bottom;

  // Y-range covers the widest envelope (5/95) plus the realised curve so nothing clips.
  let yMin = Infinity;
  let yMax = -Infinity;
  for (let i = 0; i < n; i++) {
    if (fan.percentile05[i] < yMin) yMin = fan.percentile05[i];
    if (fan.percentile95[i] > yMax) yMax = fan.percentile95[i];
    if (fan.realizedEquity[i] < yMin) yMin = fan.realizedEquity[i];
    if (fan.realizedEquity[i] > yMax) yMax = fan.realizedEquity[i];
  }
  // 5% padding above/below so the chart breathes.
  const yRange = yMax - yMin || Math.max(1, yMax * 0.05);
  yMin -= yRange * 0.05;
  yMax += yRange * 0.05;

  const x = (i: number) => PAD.left + (i / (n - 1)) * innerWidth;
  const y = (v: number) =>
    PAD.top + (1 - (v - yMin) / (yMax - yMin)) * innerHeight;

  // Closed-area paths for the bands: forward across the upper bound, back across the lower.
  const band = (lower: number[], upper: number[]) => {
    const fwd = upper.map((v, i) => `${i === 0 ? "M" : "L"}${x(i).toFixed(2)},${y(v).toFixed(2)}`).join(" ");
    const bwd = lower
      .map((v, i) => `L${x(i).toFixed(2)},${y(v).toFixed(2)}`)
      .reverse()
      .join(" ");
    return `${fwd} ${bwd} Z`;
  };

  const polyline = (vals: number[]) =>
    vals.map((v, i) => `${i === 0 ? "M" : "L"}${x(i).toFixed(2)},${y(v).toFixed(2)}`).join(" ");

  // X-axis: 5 evenly-spaced labels showing date.
  const xTicks: { x: number; label: string }[] = [];
  for (let k = 0; k < 5; k++) {
    const idx = Math.round((k / 4) * (n - 1));
    xTicks.push({ x: x(idx), label: shortDate(fan.dates[idx]) });
  }

  // Y-axis: 5 ticks evenly spaced through the value range.
  const yTicks: { y: number; value: number }[] = [];
  for (let k = 0; k < 5; k++) {
    const v = yMax - (k / 4) * (yMax - yMin);
    yTicks.push({ y: y(v), value: v });
  }

  return {
    band05_95: band(fan.percentile05, fan.percentile95),
    band25_75: band(fan.percentile25, fan.percentile75),
    median:    polyline(fan.percentile50),
    realized:  polyline(fan.realizedEquity),
    xTicks,
    yTicks,
  };
}

function LegendItem({
  x, color, label, filled = false, dashed = false,
}: { x: number; color: string; label: string; filled?: boolean; dashed?: boolean }) {
  return (
    <g transform={`translate(${x}, 0)`}>
      {filled ? (
        <rect width={14} height={6} y={2} fill={color} rx={1} />
      ) : (
        <line
          x1={0}
          x2={14}
          y1={5}
          y2={5}
          stroke={color}
          strokeWidth={1.75}
          strokeDasharray={dashed ? "3 2" : undefined}
        />
      )}
      <text x={18} y={9} fontSize={10} fill="rgba(255,255,255,0.6)">
        {label}
      </text>
    </g>
  );
}

function formatUsd(v: number): string {
  if (Math.abs(v) >= 1_000_000) return `$${(v / 1_000_000).toFixed(2)}M`;
  if (Math.abs(v) >= 1_000) return `$${(v / 1_000).toFixed(1)}k`;
  return `$${v.toFixed(0)}`;
}

function shortDate(iso: string): string {
  const d = new Date(iso);
  return `${String(d.getMonth() + 1).padStart(2, "0")}/${String(d.getDate()).padStart(2, "0")}`;
}
