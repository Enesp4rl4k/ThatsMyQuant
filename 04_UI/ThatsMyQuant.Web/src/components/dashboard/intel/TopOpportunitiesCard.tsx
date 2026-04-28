import { useQuery } from "@tanstack/react-query";
import { Link } from "react-router-dom";
import { proModeApi } from "@/api/proMode";
import { cn } from "@/lib/utils";

/**
 * Surface the freshest funding-alpha opportunities from the Pro Mode scanner.
 * Each row deep-links to the Markets page (clicking persists symbol selection
 * via the same `tmq.markets.state` localStorage key).
 */
const STORAGE_KEY = "tmq.markets.state";

export function TopOpportunitiesCard() {
  const q = useQuery({
    queryKey: ["pro-mode", "snapshot"],
    queryFn: proModeApi.snapshot,
    refetchInterval: 30_000,
  });

  const ops = (q.data?.recentOpportunities ?? []).slice(0, 6);

  const onSymbolClick = (sym: string) => {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      const cur = raw ? JSON.parse(raw) : {};
      localStorage.setItem(
        STORAGE_KEY,
        JSON.stringify({ ...cur, selected: sym })
      );
    } catch {
      /* ignore */
    }
  };

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      <div className="px-4 py-2.5 border-b border-white/5 flex items-baseline justify-between">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
            Top Opportunities
          </p>
          <p className="text-[11px] font-serif italic text-muted-foreground/60">
            funding alpha · ranked by absolute bps
          </p>
        </div>
        <span className="text-[9px] font-mono text-muted-foreground/50">
          {ops.length} live
        </span>
      </div>

      {q.isLoading ? (
        <p className="p-6 text-center text-xs italic font-serif text-muted-foreground/60">
          — scanning —
        </p>
      ) : ops.length === 0 ? (
        <p className="p-6 text-center text-xs italic font-serif text-muted-foreground/60">
          — nothing fresh · run a force scan from Pro Mode —
        </p>
      ) : (
        <ul className="divide-y divide-white/[0.04]">
          {ops.map((o, i) => {
            const positive = o.fundingBps > 0;
            return (
              <li key={`${o.symbol}-${i}`}>
                <Link
                  to="/markets"
                  onClick={() => onSymbolClick(o.symbol)}
                  className="px-4 py-2.5 flex items-center justify-between gap-3 hover:bg-white/[0.03] transition-colors"
                >
                  <div className="flex items-center gap-3 min-w-0">
                    <span className="text-[10px] font-mono text-muted-foreground/40 w-4 text-right">
                      {i + 1}
                    </span>
                    <span className="font-bold text-[12px] tracking-tight">{o.symbol}</span>
                    <span
                      className={cn(
                        "px-1.5 py-0.5 rounded text-[8px] font-black uppercase tracking-widest",
                        o.direction.toLowerCase().includes("long")
                          ? "bg-emerald-400/10 text-emerald-400"
                          : "bg-rose-400/10 text-rose-400"
                      )}
                    >
                      {o.direction}
                    </span>
                  </div>
                  <div className="flex items-center gap-3">
                    <span className="text-[9px] font-mono uppercase tracking-widest text-muted-foreground/60 hidden sm:inline">
                      {o.conviction}
                    </span>
                    <span
                      className={cn(
                        "font-mono font-bold tabular-nums text-[12px] w-20 text-right",
                        positive ? "text-emerald-400" : "text-rose-400"
                      )}
                    >
                      {positive ? "+" : ""}
                      {o.fundingBps.toFixed(2)} bps
                    </span>
                  </div>
                </Link>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
