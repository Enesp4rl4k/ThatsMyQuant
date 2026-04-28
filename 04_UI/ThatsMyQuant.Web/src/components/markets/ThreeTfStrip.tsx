import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { marketsApi, type MarketCandle } from "@/api/markets";
import { ema } from "@/lib/indicators";
import { cn } from "@/lib/utils";

interface Props {
  symbol: string;
  className?: string;
}

const TFS = [
  { tf: "1d" as const, label: "1D", bars: 50 },
  { tf: "4h" as const, label: "4H", bars: 60 },
  { tf: "1h" as const, label: "1H", bars: 80 },
] as const;

/**
 * Three-Timeframe Context Strip — the classic trader MTF approach:
 *   1D → defines trend direction (bias)
 *   4H → refines setup (momentum alignment)
 *   1H → execution timing
 *
 * Each thumbnail shows: mini candle chart + EMA21 + EMA50 + bias badge.
 * "Higher TF Bias" badge derived automatically (EMA crossover + momentum).
 */
export function ThreeTfStrip({ symbol, className }: Props) {
  return (
    <div className={cn("rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-3", className)}>
      <p className="text-[9px] font-black uppercase tracking-[0.25em] text-accent/70 mb-2.5">
        Multi-Timeframe Context
        <span className="ml-2 text-muted-foreground/40 normal-case tracking-normal font-normal">
          {symbol}
        </span>
      </p>
      <div className="grid grid-cols-3 gap-2">
        {TFS.map((t) => (
          <TfPanel key={t.tf} symbol={symbol} tf={t.tf} label={t.label} bars={t.bars} />
        ))}
      </div>
    </div>
  );
}

function TfPanel({
  symbol,
  tf,
  label,
  bars,
}: {
  symbol: string;
  tf: "1d" | "4h" | "1h";
  label: string;
  bars: number;
}) {
  const q = useQuery({
    queryKey: ["mtf", symbol, tf],
    queryFn: () => marketsApi.candles(symbol, tf, bars),
    staleTime: 60_000,
    enabled: !!symbol,
  });

  const candles = q.data ?? [];
  const analysis = useMemo(() => analyse(candles), [candles]);

  const W = 140;
  const H = 80;
  const PAD = { t: 6, r: 4, b: 14, l: 2 };
  const iW = W - PAD.l - PAD.r;
  const iH = H - PAD.t - PAD.b;
  const n = candles.length;

  if (n === 0) {
    return (
      <div className="rounded-lg border border-white/5 bg-white/[0.02] p-2" style={{ width: W }}>
        <p className="text-[9px] text-muted-foreground/40 text-center py-4">{label}</p>
      </div>
    );
  }

  const prices = candles.flatMap((c) => [c.high, c.low]);
  const pMin = Math.min(...prices) * 0.9995;
  const pMax = Math.max(...prices) * 1.0005;
  const pRange = pMax - pMin || 1;
  const step = iW / n;
  const scX = (i: number) => PAD.l + i * step + step / 2;
  const scY = (p: number) => PAD.t + iH * (1 - (p - pMin) / pRange);
  const candleW = Math.max(1, step * 0.7);

  const closes = candles.map((c) => c.close);
  const ema21 = ema(closes, 21);
  const ema50 = ema(closes, 50);

  const mkPath = (vals: (number | null)[]) => {
    let d = "";
    for (let i = 0; i < vals.length; i++) {
      const v = vals[i];
      if (v == null) continue;
      d += d ? ` L${scX(i).toFixed(1)},${scY(v).toFixed(1)}` : `M${scX(i).toFixed(1)},${scY(v).toFixed(1)}`;
    }
    return d || null;
  };

  const biasColor = analysis.bias === "BULL" ? "#37A95B" : analysis.bias === "BEAR" ? "#F05A5A" : "#F0883E";
  const last = candles[n - 1];

  return (
    <div className="rounded-lg border border-white/[0.07] bg-white/[0.02] overflow-hidden">
      {/* Header */}
      <div className="flex items-center justify-between px-2 py-1 border-b border-white/5">
        <span className="text-[9px] font-black uppercase tracking-widest text-foreground/70">{label}</span>
        <span
          className="px-1.5 py-0.5 rounded text-[8px] font-black uppercase tracking-wider"
          style={{ backgroundColor: `${biasColor}22`, color: biasColor }}
        >
          {analysis.bias}
        </span>
      </div>

      {/* Mini chart */}
      <svg width={W} height={H}>
        {/* EMA50 */}
        {mkPath(ema50) && (
          <path d={mkPath(ema50)!} fill="none" stroke="#A05A00" strokeWidth={1} opacity={0.6} />
        )}
        {/* EMA21 */}
        {mkPath(ema21) && (
          <path d={mkPath(ema21)!} fill="none" stroke="#3B8C68" strokeWidth={1.2} opacity={0.75} />
        )}
        {/* Candles */}
        {candles.map((c, i) => {
          const x = scX(i);
          const isUp = c.close >= c.open;
          const fill = isUp ? "#3B8C68" : "#C9485F";
          const top = scY(Math.max(c.open, c.close));
          const bot = scY(Math.min(c.open, c.close));
          return (
            <g key={i}>
              <line x1={x} x2={x} y1={scY(c.high)} y2={scY(c.low)} stroke={fill} strokeWidth={0.8} />
              <rect x={x - candleW / 2} y={top} width={candleW} height={Math.max(1, bot - top)} fill={fill} opacity={0.85} />
            </g>
          );
        })}
        {/* Time label */}
        <text x={PAD.l + 2} y={H - 3} fontSize={7} fill="rgba(255,255,255,0.3)" fontFamily="ui-monospace">
          {fmtTime(candles[0]?.time ?? "")} — {fmtTime(candles[n - 1]?.time ?? "")}
        </text>
      </svg>

      {/* Stats footer */}
      <div className="grid grid-cols-2 gap-x-2 px-2 py-1.5 text-[8px] font-mono border-t border-white/5">
        <div>
          <span className="text-muted-foreground/40">RSI  </span>
          <span style={{ color: analysis.rsi > 70 ? "#F05A5A" : analysis.rsi < 30 ? "#37A95B" : "rgba(255,255,255,0.7)" }}>
            {analysis.rsi.toFixed(0)}
          </span>
        </div>
        <div className="text-right">
          <span className="text-muted-foreground/40">Δ  </span>
          <span className={last && last.close >= candles[0]?.open ? "text-emerald-400" : "text-rose-400"}>
            {last ? ((last.close - candles[0].open) / candles[0].open * 100).toFixed(1) + "%" : "—"}
          </span>
        </div>
        <div>
          <span className="text-muted-foreground/40">E21 </span>
          <span className={last && ema21[n - 1] != null && last.close > ema21[n - 1]! ? "text-emerald-400" : "text-rose-400"}>
            {ema21[n - 1]?.toFixed(last?.close >= 100 ? 0 : 2) ?? "—"}
          </span>
        </div>
        <div className="text-right">
          <span className="text-muted-foreground/40">ATR </span>
          <span className="text-foreground/60">{analysis.atrPct.toFixed(1)}%</span>
        </div>
      </div>
    </div>
  );
}

interface Analysis {
  bias: "BULL" | "BEAR" | "NEUT";
  rsi: number;
  atrPct: number;
}

function analyse(candles: MarketCandle[]): Analysis {
  const n = candles.length;
  if (n < 20) return { bias: "NEUT", rsi: 50, atrPct: 0 };

  const closes = candles.map((c) => c.close);

  // RSI(14) — simple
  let avgGain = 0, avgLoss = 0;
  for (let i = n - 14; i < n; i++) {
    const d = closes[i] - closes[i - 1];
    if (d >= 0) avgGain += d; else avgLoss -= d;
  }
  avgGain /= 14; avgLoss /= 14;
  const rsi = avgLoss === 0 ? 100 : 100 - 100 / (1 + avgGain / avgLoss);

  // ATR(14)
  let atrSum = 0;
  for (let i = n - 14; i < n; i++) {
    const tr = Math.max(
      candles[i].high - candles[i].low,
      Math.abs(candles[i].high - candles[i - 1].close),
      Math.abs(candles[i].low - candles[i - 1].close)
    );
    atrSum += tr;
  }
  const atr = atrSum / 14;
  const atrPct = closes[n - 1] > 0 ? (atr / closes[n - 1]) * 100 : 0;

  // Bias: close vs EMA21 + EMA50
  const e21 = ema(closes, 21).at(-1);
  const e50 = ema(closes, 50).at(-1);
  const last = closes[n - 1];
  const bias =
    e21 != null && e50 != null && last > e21 && e21 > e50 ? "BULL" :
    e21 != null && e50 != null && last < e21 && e21 < e50 ? "BEAR" : "NEUT";

  return { bias, rsi, atrPct };
}

function fmtTime(iso: string): string {
  try {
    const d = new Date(iso);
    return d.toLocaleDateString([], { month: "short", day: "numeric" });
  } catch { return ""; }
}
