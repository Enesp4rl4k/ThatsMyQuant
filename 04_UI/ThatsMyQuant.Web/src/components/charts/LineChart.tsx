import { useId } from "react";

interface LineChartProps {
  values: number[];
  width?: number;
  height?: number;
  /** Line stroke color. Defaults to navy. */
  stroke?: string;
  /** If set, area below line is filled with this color (gradient if `gradient` is true). */
  fill?: string;
  /** Use a soft top-to-bottom gradient on the fill area. */
  gradient?: boolean;
  zeroLine?: boolean;
  axisColor?: string;
  yLabelFormatter?: (value: number) => string;
  yAxisTickCount?: number;
  /** Optional x-axis labels — drawn at start/middle/end if provided. */
  xLabels?: { start?: string; mid?: string; end?: string };
}

/**
 * Claude-style line chart. Light grid, single bold navy stroke, optional soft
 * gradient fill, axis ticks in muted ink. Theme-aware via CSS variables.
 */
export function LineChart({
  values,
  width = 800,
  height = 220,
  stroke = "var(--chart-line, #1E3A8A)",
  fill,
  gradient = false,
  zeroLine = true,
  axisColor = "var(--chart-grid, rgba(31,30,27,0.06))",
  yLabelFormatter,
  yAxisTickCount = 4,
  xLabels,
}: LineChartProps) {
  const padding = { top: 12, right: 16, bottom: xLabels ? 28 : 16, left: 56 };
  const innerW = width - padding.left - padding.right;
  const innerH = height - padding.top - padding.bottom;
  const gradientId = useId();

  if (values.length < 2) {
    return (
      <div
        className="flex items-center justify-center text-xs italic font-serif text-muted-foreground"
        style={{ width, height }}
      >
        — no data yet —
      </div>
    );
  }

  const min = Math.min(...values);
  const max = Math.max(...values);
  const range = max - min || 1;
  const stepX = innerW / (values.length - 1);
  const yOf = (v: number) => padding.top + innerH - ((v - min) / range) * innerH;

  const points = values
    .map((v, i) => `${(padding.left + i * stepX).toFixed(2)},${yOf(v).toFixed(2)}`)
    .join(" ");

  const ticks = Array.from(
    { length: yAxisTickCount + 1 },
    (_, i) => min + (range * i) / yAxisTickCount
  );
  const formatLabel = yLabelFormatter ?? ((v) => v.toFixed(2));

  let zeroY: number | null = null;
  if (zeroLine && min < 0 && max > 0) {
    zeroY = yOf(0);
  }

  let areaPath: string | null = null;
  if (fill || gradient) {
    areaPath =
      `M ${padding.left},${yOf(values[0])} ` +
      values
        .map((v, i) => `L ${(padding.left + i * stepX).toFixed(2)},${yOf(v).toFixed(2)}`)
        .join(" ") +
      ` L ${(padding.left + innerW).toFixed(2)},${(padding.top + innerH).toFixed(
        2
      )} L ${padding.left},${(padding.top + innerH).toFixed(2)} Z`;
  }

  const fillRef = gradient ? `url(#${gradientId})` : fill;

  return (
    <svg
      width={width}
      height={height}
      role="img"
      style={{ overflow: "visible" }}
      className="font-mono"
    >
      <defs>
        <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor={stroke} stopOpacity={0.18} />
          <stop offset="100%" stopColor={stroke} stopOpacity={0} />
        </linearGradient>
      </defs>

      {/* Y-axis grid + labels */}
      {ticks.map((t, i) => {
        const y = yOf(t);
        return (
          <g key={`g-${i}`}>
            <line
              x1={padding.left}
              y1={y}
              x2={padding.left + innerW}
              y2={y}
              stroke={axisColor}
              strokeWidth={1}
              strokeDasharray={i === 0 || i === ticks.length - 1 ? "" : "2 4"}
            />
            <text
              x={padding.left - 8}
              y={y + 3}
              textAnchor="end"
              fontSize={9}
              fill="rgba(31,30,27,0.45)"
              className="dark:fill-[rgba(255,250,235,0.5)]"
            >
              {formatLabel(t)}
            </text>
          </g>
        );
      })}

      {/* Zero line — emphatic */}
      {zeroY != null && (
        <line
          x1={padding.left}
          y1={zeroY}
          x2={padding.left + innerW}
          y2={zeroY}
          stroke="rgba(31,30,27,0.25)"
          strokeWidth={0.8}
          className="dark:stroke-[rgba(255,250,235,0.3)]"
        />
      )}

      {/* Fill area */}
      {areaPath && <path d={areaPath} fill={fillRef} />}

      {/* Line */}
      <polyline
        points={points}
        stroke={stroke}
        strokeWidth={1.75}
        fill="none"
        strokeLinejoin="round"
        strokeLinecap="round"
      />

      {/* X-axis labels */}
      {xLabels && (
        <g>
          {xLabels.start && (
            <text
              x={padding.left}
              y={height - 6}
              fontSize={9}
              fill="rgba(31,30,27,0.5)"
              className="dark:fill-[rgba(255,250,235,0.5)]"
            >
              {xLabels.start}
            </text>
          )}
          {xLabels.mid && (
            <text
              x={padding.left + innerW / 2}
              y={height - 6}
              fontSize={9}
              textAnchor="middle"
              fill="rgba(31,30,27,0.45)"
              className="dark:fill-[rgba(255,250,235,0.45)]"
            >
              {xLabels.mid}
            </text>
          )}
          {xLabels.end && (
            <text
              x={padding.left + innerW}
              y={height - 6}
              fontSize={9}
              textAnchor="end"
              fill="rgba(31,30,27,0.5)"
              className="dark:fill-[rgba(255,250,235,0.5)]"
            >
              {xLabels.end}
            </text>
          )}
        </g>
      )}
    </svg>
  );
}
