import { useQuery } from "@tanstack/react-query";
import { marketsApi, type Timeframe } from "@/api/markets";
import { cn } from "@/lib/utils";

interface Props {
  symbol: string;
  timeframe?: Timeframe;
}

/**
 * Live AI analyst commentary panel — calls /api/markets/narrative/{symbol}
 * (deterministic Claude-style template, no LLM cost) every 30 s and renders
 * the paragraph + actionable bullets + bias chip. Drops into the right
 * column of Trade Mode.
 */
export function AICommentaryPanel({ symbol, timeframe = "1h" }: Props) {
  const q = useQuery({
    queryKey: ["narrative", symbol, timeframe],
    queryFn:  () => marketsApi.narrative(symbol, timeframe),
    refetchInterval: 30_000,
    staleTime: 20_000,
  });

  const data = q.data;

  return (
    <div className="rounded-xl border border-white/[0.05] bg-[#0A0A0A]/80 backdrop-blur-xl p-3">
      <div className="flex items-baseline justify-between mb-2">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
          🧠 AI Analyst
        </p>
        {data && (
          <span className={cn(
            "px-1.5 py-0.5 rounded-full border text-[8px] font-mono uppercase tracking-widest font-black",
            biasTone(data.bias)
          )}>
            {data.bias}
          </span>
        )}
      </div>
      {q.isLoading ? (
        <p className="text-[11px] italic font-serif text-muted-foreground/40">— composing analyst read… —</p>
      ) : !data ? (
        <p className="text-[11px] italic font-serif text-muted-foreground/40">— no narrative available —</p>
      ) : (
        <>
          <p className="text-[11px] font-serif leading-relaxed text-foreground/82 mb-2">
            {data.body}
          </p>
          {data.bullets.length > 0 && (
            <ul className="space-y-1 mt-1">
              {data.bullets.map((b, i) => (
                <li key={i} className="text-[10px] font-mono text-muted-foreground/65 leading-relaxed flex gap-1.5">
                  <span className="text-accent/60 shrink-0">▸</span>
                  <span className="flex-1">{b}</span>
                </li>
              ))}
            </ul>
          )}
          <p className="text-[8px] font-mono uppercase tracking-widest text-muted-foreground/30 mt-2">
            {symbol} · {timeframe} · {fmtTime(data.generatedUtc)}
          </p>
        </>
      )}
    </div>
  );
}

function biasTone(bias: string): string {
  const b = bias.toLowerCase();
  if (b.includes("bull"))    return "bg-emerald-500/10 text-emerald-300 border-emerald-400/25";
  if (b.includes("over"))    return "bg-amber-500/10  text-amber-300  border-amber-400/25";
  if (b.includes("oversold"))return "bg-cyan-500/10   text-cyan-300   border-cyan-400/25";
  if (b.includes("bear"))    return "bg-rose-500/10   text-rose-300   border-rose-400/25";
  return "bg-white/[0.05] text-muted-foreground border-white/10";
}

function fmtTime(iso: string): string {
  try {
    const d = new Date(iso);
    return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  } catch { return ""; }
}
