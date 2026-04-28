import { useQuery } from "@tanstack/react-query";
import { marketsApi, type Timeframe } from "@/api/markets";
import { cn } from "@/lib/utils";

interface Props {
  symbol: string;
  timeframe?: Timeframe;
}

const REGIME_TONE: Record<string, string> = {
  "Trending Up": "bg-emerald-500/15 text-emerald-400 border-emerald-500/30",
  "Trending Down": "bg-rose-500/15 text-rose-400 border-rose-500/30",
  Ranging: "bg-accent/15 text-accent border-accent/30",
  Volatile: "bg-amber-500/15 text-amber-400 border-amber-500/30",
  Unknown: "bg-white/5 text-muted-foreground border-white/10",
};

const BIAS_TONE: Record<string, string> = {
  Bullish: "text-emerald-400",
  Bearish: "text-rose-400",
  Overbought: "text-amber-400",
  Oversold: "text-amber-400",
  Neutral: "text-muted-foreground",
};

export function AnalysisPanel({ symbol, timeframe = "1h" }: Props) {
  const q = useQuery({
    queryKey: ["markets", "analysis", symbol, timeframe],
    queryFn: () => marketsApi.analysis(symbol, timeframe),
    refetchInterval: 30_000,
    enabled: !!symbol,
  });

  const a = q.data;

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden h-full">
      <div className="px-4 py-2.5 border-b border-white/5 flex items-baseline justify-between">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
            System Analysis
          </p>
          <p className="text-[11px] font-serif italic text-muted-foreground/60">
            indicators &amp; regime · {timeframe}
          </p>
        </div>
        {a && (
          <div
            className={cn(
              "px-2 py-1 rounded text-[9px] font-black uppercase tracking-widest border",
              REGIME_TONE[a.regime] ?? REGIME_TONE.Unknown
            )}
          >
            {a.regime}
          </div>
        )}
      </div>

      {q.isLoading || !a ? (
        <p className="p-8 text-center text-xs italic font-serif text-muted-foreground">
          — analysing —
        </p>
      ) : (
        <div className="p-4 space-y-4">
          {/* RSI gauge */}
          <RsiGauge rsi={a.rsi14} bias={a.bias} />

          {/* Indicator grid */}
          <div className="grid grid-cols-3 gap-3 text-[11px]">
            <Stat label="MA 20" value={fmtPrice(a.ma20)} tone={a.lastPrice > a.ma20 ? "pos" : "neg"} />
            <Stat label="MA 50" value={fmtPrice(a.ma50)} tone={a.lastPrice > a.ma50 ? "pos" : "neg"} />
            <Stat label="ATR%" value={`${a.atrPct.toFixed(2)}%`} tone={a.atrPct > 4 ? "warn" : "neutral"} />
            <Stat label="Vol 24h" value={`${a.volatility24h.toFixed(2)}%`} />
            <Stat
              label="Bias"
              value={a.bias}
              tone={
                a.bias === "Bullish"
                  ? "pos"
                  : a.bias === "Bearish"
                    ? "neg"
                    : a.bias === "Overbought" || a.bias === "Oversold"
                      ? "warn"
                      : "neutral"
              }
            />
            <Stat
              label="Last"
              value={fmtPrice(a.lastPrice)}
              tone="neutral"
            />
          </div>

          {/* Notes / signal narrative */}
          {a.notes.length > 0 && (
            <div className="pt-3 border-t border-white/5">
              <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/50 mb-1.5">
                Notes
              </p>
              <ul className="space-y-1">
                {a.notes.map((n, i) => (
                  <li
                    key={i}
                    className={cn(
                      "text-[11px] font-serif italic leading-snug pl-3 border-l border-accent/30",
                      BIAS_TONE[a.bias] ?? "text-muted-foreground/80"
                    )}
                  >
                    {n}
                  </li>
                ))}
              </ul>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function RsiGauge({ rsi, bias }: { rsi: number; bias: string }) {
  const pct = Math.max(0, Math.min(100, rsi));
  const color =
    rsi >= 70
      ? "#B5364B"
      : rsi <= 30
        ? "#B5364B"
        : rsi >= 55
          ? "#2D6A4F"
          : rsi <= 45
            ? "#A05A00"
            : "#1E3A8A";

  return (
    <div>
      <div className="flex items-baseline justify-between mb-1.5">
        <span className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/60">
          RSI 14
        </span>
        <span className="font-mono text-[13px] font-bold tabular-nums" style={{ color }}>
          {rsi.toFixed(1)}
        </span>
      </div>
      <div className="relative h-2 rounded-full bg-white/5 overflow-hidden">
        {/* Threshold zones */}
        <div className="absolute inset-y-0 left-0 right-[70%] bg-amber-500/20" />
        <div className="absolute inset-y-0 left-[70%] right-0 bg-amber-500/20" />
        {/* Marker */}
        <div
          className="absolute top-0 bottom-0 w-1 rounded-full transition-all"
          style={{ left: `calc(${pct}% - 2px)`, backgroundColor: color }}
        />
      </div>
      <div className="flex justify-between mt-1 text-[8px] font-mono text-muted-foreground/40">
        <span>0</span>
        <span>30</span>
        <span>50</span>
        <span>70</span>
        <span>100</span>
      </div>
      <p
        className={cn(
          "text-[10px] font-serif italic mt-1.5 text-center",
          BIAS_TONE[bias] ?? "text-muted-foreground"
        )}
      >
        {bias}
      </p>
    </div>
  );
}

function Stat({
  label,
  value,
  tone = "neutral",
}: {
  label: string;
  value: string;
  tone?: "pos" | "neg" | "warn" | "neutral";
}) {
  return (
    <div>
      <p className="text-[8px] font-black uppercase tracking-widest text-muted-foreground/45 mb-0.5">
        {label}
      </p>
      <p
        className={cn(
          "font-mono font-bold tabular-nums text-[12px]",
          tone === "pos" && "text-emerald-400",
          tone === "neg" && "text-rose-400",
          tone === "warn" && "text-amber-400"
        )}
      >
        {value}
      </p>
    </div>
  );
}

function fmtPrice(v: number): string {
  if (v >= 1000) return v.toLocaleString(undefined, { maximumFractionDigits: 2 });
  if (v >= 1) return v.toFixed(2);
  return v.toFixed(5);
}
