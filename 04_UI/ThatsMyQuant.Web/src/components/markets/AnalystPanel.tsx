import { useQuery } from "@tanstack/react-query";
import { marketsApi, type Timeframe } from "@/api/markets";
import { cn } from "@/lib/utils";
import { formatDateTime } from "@/lib/format";
import { SyntheticBadge } from "@/components/ui/SyntheticBadge";

interface Props {
  symbol: string;
  timeframe?: Timeframe;
}

const BIAS_TONE: Record<string, string> = {
  Bullish: "border-emerald-500/30 bg-emerald-500/[0.04]",
  Bearish: "border-rose-500/30 bg-rose-500/[0.04]",
  Overbought: "border-amber-500/30 bg-amber-500/[0.04]",
  Oversold: "border-amber-500/30 bg-amber-500/[0.04]",
  Neutral: "border-white/10 bg-white/[0.02]",
};

const BIAS_DOT: Record<string, string> = {
  Bullish: "bg-emerald-400",
  Bearish: "bg-rose-400",
  Overbought: "bg-amber-400",
  Oversold: "bg-amber-400",
  Neutral: "bg-muted-foreground/40",
};

export function AnalystPanel({ symbol, timeframe = "1h" }: Props) {
  const q = useQuery({
    queryKey: ["markets", "narrative", symbol, timeframe],
    queryFn: () => marketsApi.narrative(symbol, timeframe),
    refetchInterval: 60_000,
    enabled: !!symbol,
  });

  const n = q.data;

  return (
    <div
      className={cn(
        "rounded-xl border backdrop-blur-xl overflow-hidden h-full flex flex-col transition-colors",
        n ? BIAS_TONE[n.bias] ?? BIAS_TONE.Neutral : "border-white/5 bg-[#0D1117]/60"
      )}
    >
      <div className="px-4 py-2.5 border-b border-white/5 flex items-baseline justify-between">
        <div>
          <div className="flex items-center gap-1.5">
            <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
              Analyst
            </p>
            <SyntheticBadge source="deterministic" />
          </div>
          <p className="text-[11px] font-serif italic text-muted-foreground/60 mt-0.5">
            indicator-driven template · {timeframe} · no LLM call
          </p>
        </div>
        {n && (
          <div className="flex items-center gap-1.5">
            <span className={cn("w-1.5 h-1.5 rounded-full animate-pulse", BIAS_DOT[n.bias] ?? BIAS_DOT.Neutral)} />
            <span className="text-[10px] font-mono uppercase tracking-widest text-foreground/80">
              {n.bias}
            </span>
          </div>
        )}
      </div>

      <div className="flex-1 p-5 overflow-y-auto">
        {q.isLoading || !n ? (
          <p className="text-xs italic font-serif text-muted-foreground/60">
            — analyst is reading the tape —
          </p>
        ) : (
          <>
            <p className="font-serif text-[14px] leading-[1.65] text-foreground/95 first-letter:font-bold first-letter:text-[18px]">
              {n.body}
            </p>

            {n.bullets.length > 0 && (
              <ul className="mt-4 pt-4 border-t border-white/5 space-y-1.5">
                {n.bullets.map((b, i) => (
                  <li
                    key={i}
                    className="text-[11px] font-mono text-muted-foreground/80 flex items-start gap-2"
                  >
                    <span className="shrink-0 text-accent/70 mt-0.5">·</span>
                    <span>{b}</span>
                  </li>
                ))}
              </ul>
            )}

            <p className="mt-4 pt-4 border-t border-white/5 text-[10px] font-mono text-muted-foreground/40 italic">
              generated {formatDateTime(n.generatedUtc)} · refreshes every 60s
            </p>
          </>
        )}
      </div>
    </div>
  );
}
