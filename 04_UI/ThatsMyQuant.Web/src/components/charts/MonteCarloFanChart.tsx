import { useMemo } from "react";

interface Props {
  equityCurve: number[];
  days?: number;
  paths?: number;
  width?: number;
  height?: number;
}

const PAD = { top: 16, right: 12, bottom: 28, left: 60 };

interface Percentiles {
  p5: number;
  p25: number;
  p50: number;
  p75: number;
  p95: number;
}

function buildFan(equity: number[], days: number, paths: number): Percentiles[] {
  if (equity.length < 2) return [];

  const returns: number[] = [];
  for (let i = 1; i < equity.length; i++) {
    if (equity[i - 1] !== 0) {
      returns.push(equity[i] / equity[i - 1] - 1);
    }
  }
  if (returns.length === 0) return [];

  const start = equity[equity.length - 1];
  const all: number[][] = new Array(paths);
  for (let p = 0; p < paths; p++) {
    const path = new Array(days + 1);
    path[0] = start;
    for (let d = 1; d <= days; d++) {
      const r = returns[Math.floor(Math.random() * returns.length)];
      path[d] = path[d - 1] * (1 + r);
    }
    all[p] = path;
  }

  const out: Percentiles[] = [];
  for (let d = 0; d <= days; d++) {
    const vals = new Array(paths);
    for (let p = 0; p < paths; p++) vals[p] = all[p][d];
    vals.sort((a, b) => a - b);
    out.push({
      p5: vals[Math.floor(paths * 0.05)],
      p25: vals[Math.floor(paths * 0.25)],
      p50: vals[Math.floor(paths * 0.5)],
      p75: vals[Math.floor(paths * 0.75)],
      p95: vals[Math.floor(paths * 0.95)],
    });
  }
  return out;
}

export function MonteCarloFanChart({
  equityCurve,
  days = 120,
  paths = 1000,
  width = 1000,
  height = 280,
}: Props) {
  const fan = useMemo(() => buildFan(equityCurve, days, paths), [equityCurve, days, paths]);

  if (fan.length === 0) {
    return (
      <div
        style={{ width, height }}
        className="flex items-center justify-center text-xs text-muted-foreground/50"
      >
        Need at least 2 equity points
      </div>
    );
  }

  const inner = { w: width - PAD.left - PAD.right, h: height - PAD.top - PAD.bottom };

  const minV = Math.min(...fan.map((f) => f.p5));
  const maxV = Math.max(...fan.map((f) => f.p95));
  const range = maxV - minV || 1;

  const x = (i: number) => PAD.left + (i / (fan.length - 1)) * inner.w;
  const y = (v: number) => PAD.top + inner.h * (1 - (v - minV) / range);

  const buildArea = (lower: keyof Percentiles, upper: keyof Percentiles) => {
    const top = fan.map((f, i) => `${x(i)},${y(f[upper])}`).join(" ");
    const bot = fan
      .slice()
      .reverse()
      .map((f, i) => `${x(fan.length - 1 - i)},${y(f[lower])}`)
      .join(" ");
    return `${top} ${bot}`;
  };

  const medianPath = fan.map((f, i) => `${i === 0 ? "M" : "L"}${x(i)},${y(f.p50)}`).join(" ");

  const startVal = fan[0].p50;
  const endP5 = fan[fan.length - 1].p5;
  const endP50 = fan[fan.length - 1].p50;
  const endP95 = fan[fan.length - 1].p95;

  // Y-axis ticks
  const ticks = Array.from({ length: 5 }, (_, i) => {
    const v = minV + (range * i) / 4;
    return { v, y: y(v) };
  });

  return (
    <svg width={width} height={height} style={{ display: "block" }} className="font-mono">
      <defs>
        <linearGradient id="mc-outer" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor="rgba(30, 58, 138, 0.10)" />
          <stop offset="100%" stopColor="rgba(30, 58, 138, 0.03)" />
        </linearGradient>
      </defs>

      {/* Y-axis grid + labels */}
      {ticks.map((t, i) => (
        <g key={i}>
          <line
            x1={PAD.left}
            x2={PAD.left + inner.w}
            y1={t.y}
            y2={t.y}
            stroke="rgba(31,30,27,0.06)"
            strokeWidth={1}
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

      {/* p5–p95 outer band */}
      <polygon points={buildArea("p5", "p95")} fill="url(#mc-outer)" />

      {/* p25–p75 inner band */}
      <polygon points={buildArea("p25", "p75")} fill="rgba(30, 58, 138, 0.16)" />

      {/* Median */}
      <path d={medianPath} fill="none" stroke="#1E3A8A" strokeWidth={1.75} strokeLinejoin="round" />

      {/* Start marker — single ring */}
      <circle cx={x(0)} cy={y(startVal)} r={4} fill="white" stroke="#1E3A8A" strokeWidth={2} />

      {/* End annotations */}
      <g fontSize={10}>
        <text x={x(fan.length - 1) - 6} y={y(endP95) - 4} textAnchor="end" fill="rgba(30, 58, 138, 0.55)">
          p95 ${(endP95 / 1000).toFixed(1)}k
        </text>
        <text x={x(fan.length - 1) - 6} y={y(endP50) - 4} textAnchor="end" fill="#1E3A8A" fontWeight="600">
          p50 ${(endP50 / 1000).toFixed(1)}k
        </text>
        <text x={x(fan.length - 1) - 6} y={y(endP5) + 12} textAnchor="end" fill="rgba(181, 54, 75, 0.75)">
          p5 ${(endP5 / 1000).toFixed(1)}k
        </text>
      </g>

      {/* X-axis baseline */}
      <line
        x1={PAD.left}
        x2={PAD.left + inner.w}
        y1={PAD.top + inner.h}
        y2={PAD.top + inner.h}
        stroke="rgba(31,30,27,0.12)"
        strokeWidth={1}
        className="dark:stroke-[rgba(255,250,235,0.08)]"
      />

      {/* X-axis labels — italic for editorial feel */}
      <text
        x={PAD.left}
        y={height - 8}
        fill="rgba(31,30,27,0.5)"
        fontSize={10}
        fontStyle="italic"
        fontFamily="Newsreader, serif"
        className="dark:fill-[rgba(255,250,235,0.5)]"
      >
        today
      </text>
      <text
        x={PAD.left + inner.w}
        y={height - 8}
        textAnchor="end"
        fill="rgba(31,30,27,0.5)"
        fontSize={10}
        fontStyle="italic"
        fontFamily="Newsreader, serif"
        className="dark:fill-[rgba(255,250,235,0.5)]"
      >
        +{days} days
      </text>
    </svg>
  );
}
