import { useMemo } from "react";
import type { LinearRegressionResult } from "@/api/backtestLab";

interface Props {
  regression: LinearRegressionResult;
  realizedEquity: number[];
  dates: string[];
  width?: number;
  height?: number;
}

/**
 * Two-panel decomposition chart:
 *   Top    — realised equity (accent line) + OLS trend line (dashed)
 *   Bottom — residuals (realised − trend) around zero, with ±1σ bands
 *
 * Reading the chart:
 *   - Trend that hugs realised tightly → high R² → steady compounding
 *   - Big residual swings → realised path is volatile around the linear drift
 *   - Residuals systematically positive/negative for long stretches →
 *     non-linear regime; pure linear regression is misleading and you should
 *     question whether the drift is real or just one biased era
 */
const PAD = { top: 16, right: 12, bottom: 28, left: 64 };
const SPLIT_RATIO = 0.65; // top panel takes 65%, bottom 35%

export function RegressionDecompositionChart({
  regression,
  realizedEquity,
  dates,
  width = 760,
  height = 320,
}: Props) {
  const view = useMemo(
    () => buildView(regression, realizedEquity, dates, width, height),
    [regression, realizedEquity, dates, width, height],
  );

  if (view === null) {
    return (
      <div className="text-xs text-muted-foreground p-4">
        Not enough data to render regression decomposition.
      </div>
    );
  }

  return (
    <svg
      width={width}
      height={height}
      role="img"
      aria-label="Linear regression decomposition: realised vs trend, with residuals"
      className="block"
    >
      {/* Top panel grid */}
      {view.topYTicks.map((t) => (
        <g key={`top-${t.value}`}>
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

      {/* Top panel: realised + trend */}
      <path
        d={view.realizedPath}
        fill="none"
        stroke="rgba(110, 231, 183, 0.95)"
        strokeWidth={1.75}
      />
      <path
        d={view.trendPath}
        fill="none"
        stroke="rgba(0, 191, 255, 0.7)"
        strokeWidth={1.25}
        strokeDasharray="4 3"
      />

      {/* Splitter line between panels */}
      <line
        x1={PAD.left}
        x2={width - PAD.right}
        y1={view.splitY}
        y2={view.splitY}
        stroke="rgba(255,255,255,0.08)"
        strokeWidth={1}
      />

      {/* Bottom panel: residuals around zero with ±1σ band */}
      <rect
        x={PAD.left}
        y={view.residOneSigmaUpY}
        width={width - PAD.left - PAD.right}
        height={view.residOneSigmaDownY - view.residOneSigmaUpY}
        fill="rgba(0, 191, 255, 0.06)"
      />
      {/* Zero line */}
      <line
        x1={PAD.left}
        x2={width - PAD.right}
        y1={view.residZeroY}
        y2={view.residZeroY}
        stroke="rgba(255,255,255,0.25)"
        strokeWidth={1}
        strokeDasharray="2 2"
      />
      <path
        d={view.residPath}
        fill="none"
        stroke="rgba(255, 200, 100, 0.85)"
        strokeWidth={1.25}
      />
      {/* Residual y-axis labels (top, zero, bottom) */}
      <text x={PAD.left - 8} y={view.residZeroY + 3} fill="rgba(255,255,255,0.4)" fontSize={10} textAnchor="end" fontFamily="ui-monospace, SFMono-Regular, monospace">0</text>
      <text x={PAD.left - 8} y={view.residOneSigmaUpY + 3} fill="rgba(255,255,255,0.4)" fontSize={10} textAnchor="end" fontFamily="ui-monospace, SFMono-Regular, monospace">+1σ</text>
      <text x={PAD.left - 8} y={view.residOneSigmaDownY + 3} fill="rgba(255,255,255,0.4)" fontSize={10} textAnchor="end" fontFamily="ui-monospace, SFMono-Regular, monospace">−1σ</text>

      {/* X-axis labels (bottom of bottom panel) */}
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

      {/* Legend */}
      <g transform={`translate(${PAD.left}, ${PAD.top - 4})`}>
        <LegendItem x={0}   color="rgba(110, 231, 183, 0.95)" label="Realised" />
        <LegendItem x={80}  color="rgba(0, 191, 255, 0.7)"   label="Trend" dashed />
        <LegendItem x={150} color="rgba(255, 200, 100, 0.85)" label="Residuals" />
        <LegendItem x={235} color="rgba(0, 191, 255, 0.06)"   label="±1σ" filled />
      </g>
    </svg>
  );
}

function buildView(
  regression: LinearRegressionResult,
  realizedEquity: number[],
  dates: string[],
  width: number,
  height: number,
) {
  const n = realizedEquity.length;
  if (n < 2) return null;

  const innerWidth = width - PAD.left - PAD.right;
  const innerH = height - PAD.top - PAD.bottom;
  const topH = innerH * SPLIT_RATIO;
  const botH = innerH * (1 - SPLIT_RATIO) - 8; // 8px gap
  const splitY = PAD.top + topH;

  // Top panel y-range covers realised + trend
  let yMin = Infinity;
  let yMax = -Infinity;
  for (let i = 0; i < n; i++) {
    if (realizedEquity[i] < yMin) yMin = realizedEquity[i];
    if (realizedEquity[i] > yMax) yMax = realizedEquity[i];
    if (regression.trendUsd[i] < yMin) yMin = regression.trendUsd[i];
    if (regression.trendUsd[i] > yMax) yMax = regression.trendUsd[i];
  }
  const yRange = yMax - yMin || Math.max(1, yMax * 0.05);
  yMin -= yRange * 0.05;
  yMax += yRange * 0.05;

  const x = (i: number) => PAD.left + (i / (n - 1)) * innerWidth;
  const yTop = (v: number) =>
    PAD.top + (1 - (v - yMin) / (yMax - yMin)) * topH;

  // Bottom panel: residuals symmetric around zero, scaled to ±max(|residuals|)
  let residAbsMax = 0;
  for (let i = 0; i < n; i++) {
    const a = Math.abs(regression.residualsUsd[i]);
    if (a > residAbsMax) residAbsMax = a;
  }
  if (residAbsMax === 0) residAbsMax = 1;
  // Pad 10% so the line doesn't kiss the edge
  residAbsMax *= 1.1;
  const botCenterY = splitY + 8 + botH / 2;
  const yBot = (v: number) => botCenterY - (v / residAbsMax) * (botH / 2);

  // Polyline builders
  const polyTop = (vals: number[]) =>
    vals.map((v, i) => `${i === 0 ? "M" : "L"}${x(i).toFixed(2)},${yTop(v).toFixed(2)}`).join(" ");
  const polyBot = (vals: number[]) =>
    vals.map((v, i) => `${i === 0 ? "M" : "L"}${x(i).toFixed(2)},${yBot(v).toFixed(2)}`).join(" ");

  // X ticks: 5 evenly-spaced
  const xTicks: { x: number; label: string }[] = [];
  for (let k = 0; k < 5; k++) {
    const idx = Math.round((k / 4) * (n - 1));
    xTicks.push({ x: x(idx), label: shortDate(dates[idx]) });
  }
  // Top-panel y-ticks
  const topYTicks: { y: number; value: number }[] = [];
  for (let k = 0; k < 4; k++) {
    const v = yMax - (k / 3) * (yMax - yMin);
    topYTicks.push({ y: yTop(v), value: v });
  }

  return {
    realizedPath: polyTop(realizedEquity),
    trendPath:    polyTop(regression.trendUsd),
    residPath:    polyBot(regression.residualsUsd),
    splitY,
    residZeroY:        yBot(0),
    residOneSigmaUpY:  yBot(regression.residualStdUsd),
    residOneSigmaDownY: yBot(-regression.residualStdUsd),
    xTicks,
    topYTicks,
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
