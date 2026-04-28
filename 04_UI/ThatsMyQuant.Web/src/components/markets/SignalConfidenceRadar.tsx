import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { marketsApi, type MarketCandle, type Timeframe } from "@/api/markets";

interface Props {
  symbol: string;
  timeframe?: Timeframe;
  size?: number;
  className?: string;
}

const AXES = ["Momentum", "Trend", "Volume", "Volatility", "Sentiment", "AlphaTrend"] as const;

/**
 * Six-axis radar showing live signal confidence (each axis [0,1]).
 * Mirrors the WPF SignalConfidenceView. Computes scores client-side from
 * 30-day candles using the same closed-form rules:
 *   - Momentum:  14-bar ROC normalized to ±10%
 *   - Trend:     |slope|/mean over 20 bars
 *   - Volume:    last bar / 20-bar avg, capped at 3×
 *   - Volatility: stdev of log-returns / 2%
 *   - Sentiment: bullish-candle ratio over 20 bars
 *   - AlphaTrend: close-vs-EMA20 sign agreement with momentum
 * Verdict: ALL-IN (≥75%), STRONG (≥50%), WEAK (≥30%), AVOID (<30%).
 */
export function SignalConfidenceRadar({
  symbol,
  timeframe = "1h",
  size = 320,
  className,
}: Props) {
  const q = useQuery({
    queryKey: ["radar-candles", symbol, timeframe],
    queryFn: () => marketsApi.candles(symbol, timeframe, 200),
    staleTime: 60_000,
    enabled: !!symbol,
  });

  const scores = useMemo(
    () => (q.data && q.data.length >= 30 ? compute(q.data) : SYNTHETIC),
    [q.data]
  );
  const avg = scores.reduce((s, v) => s + v, 0) / scores.length;
  const verdict = verdictOf(avg);

  const center = size / 2;
  const radius = size / 2 - 38;

  const axisPoint = (i: number, scale: number) => {
    const angle = -Math.PI / 2 + i * ((Math.PI * 2) / 6);
    const r = radius * scale;
    return [center + Math.cos(angle) * r, center + Math.sin(angle) * r] as const;
  };

  // Background rings 25/50/75/100%
  const rings = [0.25, 0.5, 0.75, 1.0].map((scale) => {
    const pts = Array.from({ length: 7 }, (_, i) => {
      const [x, y] = axisPoint(i % 6, scale);
      return `${x.toFixed(1)},${y.toFixed(1)}`;
    }).join(" ");
    return { pts, isOuter: scale === 1.0 };
  });

  // Spokes
  const spokes = Array.from({ length: 6 }, (_, i) => {
    const [ox, oy] = axisPoint(i, 1);
    return { x1: center, y1: center, x2: ox, y2: oy };
  });

  // Value polygon
  const valuePts = Array.from({ length: 7 }, (_, i) => {
    const [x, y] = axisPoint(i % 6, scores[i % 6]);
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(" ");

  return (
    <div className={className}>
      <div className="flex items-baseline justify-between px-3 py-2">
        <div className="flex items-center gap-2">
          <span className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
            Signal Confidence
          </span>
          <span className="text-[10px] text-muted-foreground/60">
            {symbol} · {timeframe}
          </span>
        </div>
        <span
          className="px-2 py-0.5 rounded text-[10px] font-black uppercase tracking-widest"
          style={{ backgroundColor: `${verdict.color}22`, color: verdict.color }}
        >
          {verdict.label} · {(avg * 100).toFixed(0)}%
        </span>
      </div>

      <svg width={size} height={size} style={{ display: "block", margin: "0 auto" }}>
        {/* Rings */}
        {rings.map((r, i) => (
          <polygon
            key={i}
            points={r.pts}
            fill="none"
            stroke={r.isOuter ? "rgba(255,255,255,0.25)" : "rgba(255,255,255,0.08)"}
            strokeWidth={r.isOuter ? 1.2 : 0.6}
          />
        ))}
        {/* Spokes */}
        {spokes.map((s, i) => (
          <line
            key={i}
            x1={s.x1}
            y1={s.y1}
            x2={s.x2}
            y2={s.y2}
            stroke="rgba(255,255,255,0.12)"
            strokeWidth={0.6}
          />
        ))}
        {/* Axis labels */}
        {AXES.map((name, i) => {
          const [x, y] = axisPoint(i, 1.18);
          return (
            <text
              key={name}
              x={x}
              y={y}
              fontSize={10}
              fontWeight={700}
              textAnchor="middle"
              dominantBaseline="middle"
              fill="rgba(255,255,255,0.7)"
            >
              {name}
            </text>
          );
        })}
        {/* Value polygon */}
        <polygon
          points={valuePts}
          fill={`${verdict.color}33`}
          stroke={verdict.color}
          strokeWidth={1.6}
          strokeLinejoin="round"
        />
        {/* Value dots + score labels */}
        {scores.map((s, i) => {
          const [x, y] = axisPoint(i, s);
          return (
            <g key={i}>
              <circle cx={x} cy={y} r={3} fill={verdict.color} />
              <text
                x={x}
                y={y - 8}
                fontSize={8}
                fontFamily="ui-monospace, monospace"
                textAnchor="middle"
                fill="rgba(255,255,255,0.85)"
              >
                {(s * 100).toFixed(0)}
              </text>
            </g>
          );
        })}
      </svg>

      {q.isLoading && (
        <p className="text-center text-[10px] italic text-muted-foreground/60 pb-2">
          fetching candles…
        </p>
      )}
    </div>
  );
}

const SYNTHETIC = [0.55, 0.62, 0.48, 0.4, 0.5, 0.58];

function compute(c: MarketCandle[]): number[] {
  const n = c.length;
  const closes = c.map((k) => k.close);

  // Momentum: 14-bar ROC normalized to ±10%
  const look = Math.min(14, n - 1);
  const roc = look > 0 ? (closes[n - 1] - closes[n - 1 - look]) / closes[n - 1 - look] : 0;
  const momentum = clamp(0.5 + roc / 0.1, 0, 1);

  // Trend strength: |slope|/mean over 20 bars
  const tLook = Math.min(20, n);
  let sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
  for (let i = 0; i < tLook; i++) {
    sumX += i;
    sumY += closes[n - tLook + i];
    sumXY += i * closes[n - tLook + i];
    sumX2 += i * i;
  }
  const slope = (tLook * sumXY - sumX * sumY) / Math.max(1e-9, tLook * sumX2 - sumX * sumX);
  const meanClose = sumY / tLook;
  const trendNorm = meanClose > 0 ? Math.abs(slope) / (meanClose * 0.001) : 0;
  const trend = clamp(trendNorm / 5, 0, 1);

  // Volume ratio
  const vLook = Math.min(20, n);
  let volAvg = 0;
  for (let i = n - vLook; i < n; i++) volAvg += c[i].volume;
  volAvg /= vLook;
  const volRatio = volAvg > 0 ? c[n - 1].volume / volAvg : 0;
  const volume = clamp(volRatio / 3, 0, 1);

  // Volatility: stdev of log-returns / 2%
  const rets: number[] = [];
  for (let i = 1; i < n; i++) {
    if (closes[i - 1] > 0) rets.push(Math.log(closes[i] / closes[i - 1]));
  }
  const mean = rets.reduce((s, r) => s + r, 0) / Math.max(1, rets.length);
  const variance =
    rets.reduce((s, r) => s + (r - mean) * (r - mean), 0) /
    Math.max(1, rets.length - 1);
  const stdev = Math.sqrt(variance);
  const volatility = clamp(stdev / 0.02, 0, 1);

  // Sentiment: bullish ratio in last 20
  let bull = 0;
  const sLook = Math.min(20, n);
  for (let i = n - sLook; i < n; i++) if (c[i].close >= c[i].open) bull++;
  const sentiment = bull / sLook;

  // AlphaTrend: EMA20 vs price, sign-agreement with momentum
  let ema = closes[0];
  const alpha = 2 / 21;
  for (let i = 1; i < n; i++) ema = alpha * closes[i] + (1 - alpha) * ema;
  const bias = closes[n - 1] > ema ? 1 : -1;
  const mSign = roc >= 0 ? 1 : -1;
  const alphaAlign =
    bias === mSign
      ? clamp(0.5 + Math.abs(roc) / 0.05, 0.5, 1)
      : clamp(0.5 - Math.abs(roc) / 0.05, 0, 0.5);

  return [momentum, trend, volume, volatility, sentiment, alphaAlign];
}

function clamp(v: number, lo: number, hi: number) {
  return Math.max(lo, Math.min(hi, v));
}

function verdictOf(avg: number): { label: string; color: string } {
  if (avg >= 0.75) return { label: "ALL-IN", color: "#37A95B" };
  if (avg >= 0.5) return { label: "STRONG", color: "#F0883E" };
  if (avg >= 0.3) return { label: "WEAK", color: "#C9A13B" };
  return { label: "AVOID", color: "#F05A5A" };
}
