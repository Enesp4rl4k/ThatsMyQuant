import { useMemo } from "react";
import { rsi, macd } from "@/lib/indicators";

interface BasePaneProps {
  closes: number[];
  width: number;
  height?: number;
  className?: string;
}

const PAD = { top: 14, right: 16, bottom: 18, left: 56 };

/** RSI sub-pane (0..100, with 30/50/70 reference lines). */
export function RsiPane({ closes, width, height = 90, className }: BasePaneProps) {
  const series = useMemo(() => rsi(closes, 14), [closes]);

  if (series.length < 15) {
    return (
      <div className="text-[10px] italic font-serif text-muted-foreground/60 text-center py-3">
        — insufficient data for RSI(14) —
      </div>
    );
  }

  const innerW = width - PAD.left - PAD.right;
  const innerH = height - PAD.top - PAD.bottom;
  const stepX = innerW / Math.max(1, series.length - 1);
  const yOf = (v: number) => PAD.top + innerH * (1 - v / 100);

  const points = series
    .map((v, i) => (v == null ? null : `${(PAD.left + i * stepX).toFixed(2)},${yOf(v).toFixed(2)}`))
    .filter((p): p is string => p !== null)
    .join(" ");

  // Determine current value tone
  const last = series[series.length - 1] ?? 50;
  const lastTone =
    last >= 70
      ? "#B5364B"
      : last <= 30
        ? "#2D6A4F"
        : last >= 55
          ? "#2D6A4F"
          : last <= 45
            ? "#A05A00"
            : "#1E3A8A";

  return (
    <svg width={width} height={height} className={className} style={{ display: "block" }}>
      {/* Title */}
      <text x={PAD.left} y={10} fontSize={9} fontWeight="700" fill="currentColor" className="fill-muted-foreground/70">
        RSI 14
      </text>
      <text
        x={PAD.left + 50}
        y={10}
        fontSize={10}
        fontWeight="700"
        fontFamily="ui-monospace, monospace"
        fill={lastTone}
      >
        {last.toFixed(1)}
      </text>

      {/* Overbought / oversold zones */}
      <rect
        x={PAD.left}
        y={yOf(70)}
        width={innerW}
        height={yOf(100) - yOf(70)}
        fill="rgba(181, 54, 75, 0.06)"
      />
      <rect
        x={PAD.left}
        y={yOf(30)}
        width={innerW}
        height={yOf(0) - yOf(30)}
        fill="rgba(45, 106, 79, 0.06)"
      />

      {/* 30/50/70 reference lines */}
      {[30, 50, 70].map((level) => (
        <g key={level}>
          <line
            x1={PAD.left}
            x2={PAD.left + innerW}
            y1={yOf(level)}
            y2={yOf(level)}
            stroke="rgba(31,30,27,0.12)"
            className="dark:stroke-[rgba(255,250,235,0.1)]"
            strokeDasharray={level === 50 ? "2 4" : ""}
            strokeWidth={0.7}
          />
          <text
            x={PAD.left - 4}
            y={yOf(level) + 3}
            fontSize={8}
            textAnchor="end"
            fill="rgba(31,30,27,0.4)"
            className="dark:fill-[rgba(255,250,235,0.4)]"
          >
            {level}
          </text>
        </g>
      ))}

      {/* RSI line */}
      <polyline
        points={points}
        stroke="#1E3A8A"
        strokeWidth={1.4}
        fill="none"
        strokeLinejoin="round"
      />
    </svg>
  );
}

/** MACD sub-pane — line + signal + histogram bars. */
export function MacdPane({ closes, width, height = 100, className }: BasePaneProps) {
  const series = useMemo(() => macd(closes, 12, 26, 9), [closes]);
  const valid = series.filter((s) => s != null);

  if (valid.length === 0) {
    return (
      <div className="text-[10px] italic font-serif text-muted-foreground/60 text-center py-3">
        — insufficient data for MACD —
      </div>
    );
  }

  const all: number[] = [];
  for (const s of series) {
    if (s != null) {
      all.push(s.macd, s.signal, s.histogram);
    }
  }
  const max = Math.max(...all, 0);
  const min = Math.min(...all, 0);
  const range = max - min || 1;

  const innerW = width - PAD.left - PAD.right;
  const innerH = height - PAD.top - PAD.bottom;
  const stepX = innerW / Math.max(1, series.length - 1);
  const yOf = (v: number) => PAD.top + innerH * (1 - (v - min) / range);
  const zeroY = yOf(0);

  const macdLinePts = series
    .map((s, i) => (s == null ? null : `${(PAD.left + i * stepX).toFixed(2)},${yOf(s.macd).toFixed(2)}`))
    .filter((p): p is string => p !== null)
    .join(" ");

  const signalLinePts = series
    .map((s, i) => (s == null ? null : `${(PAD.left + i * stepX).toFixed(2)},${yOf(s.signal).toFixed(2)}`))
    .filter((p): p is string => p !== null)
    .join(" ");

  const lastSet = series.filter((s) => s != null).slice(-1)[0]!;

  return (
    <svg width={width} height={height} className={className} style={{ display: "block" }}>
      {/* Title + last values */}
      <text x={PAD.left} y={10} fontSize={9} fontWeight="700" className="fill-muted-foreground/70">
        MACD 12/26/9
      </text>
      <g fontSize={9} fontFamily="ui-monospace, monospace">
        <text x={PAD.left + 80} y={10} fill="#1E3A8A" fontWeight="600">
          MACD {lastSet.macd.toFixed(2)}
        </text>
        <text x={PAD.left + 160} y={10} fill="#A05A00" fontWeight="600">
          Sig {lastSet.signal.toFixed(2)}
        </text>
        <text
          x={PAD.left + 230}
          y={10}
          fill={lastSet.histogram >= 0 ? "#2D6A4F" : "#B5364B"}
          fontWeight="600"
        >
          Hist {lastSet.histogram >= 0 ? "+" : ""}
          {lastSet.histogram.toFixed(2)}
        </text>
      </g>

      {/* Zero baseline */}
      <line
        x1={PAD.left}
        x2={PAD.left + innerW}
        y1={zeroY}
        y2={zeroY}
        stroke="rgba(31,30,27,0.18)"
        className="dark:stroke-[rgba(255,250,235,0.18)]"
      />

      {/* Histogram bars */}
      {series.map((s, i) => {
        if (s == null) return null;
        const x = PAD.left + i * stepX - stepX * 0.35;
        const w = stepX * 0.7;
        const h = Math.abs(yOf(s.histogram) - zeroY);
        const y = s.histogram >= 0 ? yOf(s.histogram) : zeroY;
        return (
          <rect
            key={i}
            x={x}
            y={y}
            width={Math.max(1, w)}
            height={Math.max(1, h)}
            fill={s.histogram >= 0 ? "rgba(45,106,79,0.55)" : "rgba(181,54,75,0.55)"}
          />
        );
      })}

      {/* MACD line */}
      <polyline
        points={macdLinePts}
        stroke="#1E3A8A"
        strokeWidth={1.4}
        fill="none"
        strokeLinejoin="round"
      />
      {/* Signal line */}
      <polyline
        points={signalLinePts}
        stroke="#A05A00"
        strokeWidth={1.2}
        fill="none"
        strokeLinejoin="round"
        strokeDasharray="3 2"
      />
    </svg>
  );
}
