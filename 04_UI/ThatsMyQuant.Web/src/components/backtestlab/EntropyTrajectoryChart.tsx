import { useMemo } from "react";
import type { ShannonEntropyResult } from "@/api/backtestLab";

interface Props {
  entropy: ShannonEntropyResult;
  width?: number;
  height?: number;
}

const PAD = { top: 16, right: 12, bottom: 28, left: 64 };

/**
 * Single-line chart of rolling Shannon entropy over time.
 *
 * The first <c>WindowBars - 1</c> values are NaN (window not yet full); the
 * polyline skips them by breaking into a fresh M command after each gap.
 *
 * Reference lines:
 *   - Top: max entropy = ln(bins). Theoretical ceiling for a uniform return
 *     distribution within the window.
 *   - Mid: 50% of max — heuristic "normal" zone for healthy markets.
 *
 * Reading the chart:
 *   - Steady line near 70-90% of max → stable regime, good environment for the
 *     strategy.
 *   - Sudden DROP toward 0 → returns concentrating (a regime change to either
 *     extreme directional move or extreme calm). For mean-reversion strategies
 *     this often precedes drawdown — the "easy money" is being squeezed out.
 *   - Sudden SPIKE toward max → fat-tailed regime, all bins firing — typical
 *     of news shocks. Higher noise-fitting risk.
 */
export function EntropyTrajectoryChart({ entropy, width = 760, height = 220 }: Props) {
  const view = useMemo(() => buildView(entropy, width, height), [entropy, width, height]);

  if (view === null) {
    return (
      <div className="text-xs text-muted-foreground p-4">
        Not enough data to render entropy trajectory.
      </div>
    );
  }

  return (
    <svg
      width={width}
      height={height}
      role="img"
      aria-label="Rolling Shannon entropy trajectory"
      className="block"
    >
      {/* Y grid + labels */}
      {view.yTicks.map((t) => (
        <g key={t.value.toFixed(2)}>
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
            {t.label}
          </text>
        </g>
      ))}

      {/* X labels */}
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

      {/* Max-entropy ceiling reference */}
      <line
        x1={PAD.left}
        x2={width - PAD.right}
        y1={view.maxLineY}
        y2={view.maxLineY}
        stroke="rgba(255,255,255,0.18)"
        strokeWidth={1}
        strokeDasharray="4 3"
      />
      <text
        x={width - PAD.right - 4}
        y={view.maxLineY - 4}
        fill="rgba(255,255,255,0.5)"
        fontSize={9}
        textAnchor="end"
      >
        max = ln({entropy.bins})
      </text>

      {/* Entropy line */}
      <path
        d={view.linePath}
        fill="none"
        stroke="rgba(0, 191, 255, 0.85)"
        strokeWidth={1.5}
      />
    </svg>
  );
}

function buildView(entropy: ShannonEntropyResult, width: number, height: number) {
  const n = entropy.dates.length;
  if (n < 2) return null;

  const innerWidth = width - PAD.left - PAD.right;
  const innerH = height - PAD.top - PAD.bottom;

  // Y-range fixed at [0, max] — entropy is bounded so a stable axis lets the
  // operator compare across runs without re-anchoring their visual baseline.
  const yMin = 0;
  const yMax = Math.max(entropy.maxEntropyNats, 0.01);

  const x = (i: number) => PAD.left + (i / (n - 1)) * innerWidth;
  const y = (v: number) =>
    PAD.top + (1 - (v - yMin) / (yMax - yMin)) * innerH;

  // Build path with NaN gap handling: each contiguous run of finite values
  // starts with M, subsequent points are L. NaN forces the next finite point
  // to start a fresh segment.
  let linePath = "";
  let inSegment = false;
  for (let i = 0; i < n; i++) {
    const v = entropy.entropy[i];
    if (!Number.isFinite(v)) {
      inSegment = false;
      continue;
    }
    const cmd = inSegment ? "L" : "M";
    linePath += `${cmd}${x(i).toFixed(2)},${y(v).toFixed(2)} `;
    inSegment = true;
  }

  // 4 y-ticks: 0, 25%, 50%, 75%, max
  const yTicks: { y: number; value: number; label: string }[] = [
    { value: 0,           y: y(0),           label: "0" },
    { value: yMax * 0.25, y: y(yMax * 0.25), label: (yMax * 0.25).toFixed(2) },
    { value: yMax * 0.5,  y: y(yMax * 0.5),  label: (yMax * 0.5).toFixed(2) },
    { value: yMax * 0.75, y: y(yMax * 0.75), label: (yMax * 0.75).toFixed(2) },
  ];

  // 5 x-ticks
  const xTicks: { x: number; label: string }[] = [];
  for (let k = 0; k < 5; k++) {
    const idx = Math.round((k / 4) * (n - 1));
    xTicks.push({ x: x(idx), label: shortDate(entropy.dates[idx]) });
  }

  return {
    linePath,
    yTicks,
    xTicks,
    maxLineY: y(yMax),
  };
}

function shortDate(iso: string): string {
  const d = new Date(iso);
  return `${String(d.getMonth() + 1).padStart(2, "0")}/${String(d.getDate()).padStart(2, "0")}`;
}
