import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { computeMultiPeriod } from "@/lib/riskMetrics";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";

export function MultiPeriodStrip() {
  const q = useQuery({
    queryKey: ["portfolio", "equity-timeline"],
    queryFn: dashboardApi.equityTimeline,
    refetchInterval: 60_000,
  });

  const periods = useMemo(() => computeMultiPeriod(q.data ?? []), [q.data]);

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      <div className="px-5 py-2.5 border-b border-white/5">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
          Period Returns
        </p>
        <p className="text-[11px] font-serif italic text-muted-foreground/60">
          1D · 7D · 30D · 90D · YTD · cumulative realised P&amp;L
        </p>
      </div>

      {q.isLoading ? (
        <p className="p-6 text-xs italic font-serif text-muted-foreground/60 text-center">
          — loading periods —
        </p>
      ) : periods.length === 0 ? (
        <p className="p-6 text-xs italic font-serif text-muted-foreground/60 text-center">
          — no equity history —
        </p>
      ) : (
        <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-5 divide-x divide-y divide-white/[0.04] sm:divide-y-0">
          {periods.map((p) => {
            const positive = p.pnl >= 0;
            return (
              <div
                key={p.label}
                className={cn(
                  "px-4 py-3 flex flex-col gap-0.5 transition-colors hover:bg-white/[0.02]",
                  positive ? "border-l-2 border-l-emerald-500/40" : "border-l-2 border-l-rose-500/40"
                )}
              >
                <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/45">
                  {p.label}
                </p>
                <p
                  className={cn(
                    "font-mono font-black tabular-nums text-[16px] leading-none mt-1",
                    positive ? "text-emerald-400" : "text-rose-400"
                  )}
                >
                  {positive ? "+" : ""}
                  {formatUsd(p.pnl)}
                </p>
                <p
                  className={cn(
                    "font-mono text-[10px] tabular-nums",
                    positive ? "text-emerald-400/70" : "text-rose-400/70"
                  )}
                >
                  {positive ? "+" : ""}
                  {p.pct.toFixed(2)}%
                </p>
                <p className="text-[9px] font-mono text-muted-foreground/40 mt-1">
                  {p.trades} trade{p.trades === 1 ? "" : "s"}
                  {p.bestDay !== null && p.bestDay > 0 && (
                    <span className="ml-1 text-emerald-400/60">· best {formatUsd(p.bestDay)}</span>
                  )}
                </p>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
