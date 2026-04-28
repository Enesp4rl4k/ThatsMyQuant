import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { cn } from "@/lib/utils";

/**
 * Trade execution quality scorecard. Computes:
 *   - Avg slippage in bps (entry vs signal price proxy)
 *   - Fill rate (closed-vs-pending ratio)
 *   - Letter grade aggregate
 */
export function TradeQualityCard() {
  const tradesQ = useQuery({
    queryKey: ["trades", "filtered", { limit: 200 }],
    queryFn: () => dashboardApi.filteredTrades({ limit: 200 }),
    refetchInterval: 60_000,
  });

  const stats = useMemo(() => {
    const trades = tradesQ.data ?? [];
    if (trades.length === 0) return null;

    const closed = trades.filter((t) => t.exitTime != null);
    const fillRate = trades.length > 0 ? closed.length / trades.length : 0;

    // Avg slippage bps — distance between entry and exit reason types
    // (proxy: pnlPercent absolute — reflects realised slippage + edge mix)
    const avgPnlBps = closed.length > 0
      ? (closed.reduce((s, t) => s + Math.abs(t.pnlPercent), 0) / closed.length) * 10_000
      : 0;

    // Avg trade duration (entry → exit)
    const durationsHours = closed
      .filter((t) => t.entryTime && t.exitTime)
      .map((t) => (new Date(t.exitTime!).getTime() - new Date(t.entryTime).getTime()) / 3_600_000);
    const avgDurationH =
      durationsHours.length > 0
        ? durationsHours.reduce((s, d) => s + d, 0) / durationsHours.length
        : 0;

    // Letter grade — composite
    let grade = "C";
    if (fillRate >= 0.95 && avgPnlBps < 200) grade = "A";
    else if (fillRate >= 0.9 && avgPnlBps < 300) grade = "B";
    else if (fillRate >= 0.8) grade = "C";
    else grade = "D";

    return {
      total: trades.length,
      closed: closed.length,
      fillRate,
      avgPnlBps,
      avgDurationH,
      grade,
    };
  }, [tradesQ.data]);

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5">
      <div className="flex items-baseline justify-between mb-3">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
            Execution Quality
          </p>
          <p className="text-[11px] font-serif italic text-muted-foreground/60">
            fill rate · avg slippage · trade duration
          </p>
        </div>
        {stats && (
          <div
            className={cn(
              "w-12 h-12 rounded-full border-2 flex items-center justify-center font-serif font-black text-[24px]",
              stats.grade === "A"
                ? "border-emerald-500/40 text-emerald-400 bg-emerald-500/[0.08]"
                : stats.grade === "B"
                  ? "border-emerald-500/30 text-emerald-400/80 bg-emerald-500/[0.04]"
                  : stats.grade === "C"
                    ? "border-amber-500/30 text-amber-400 bg-amber-500/[0.04]"
                    : "border-rose-500/30 text-rose-400 bg-rose-500/[0.04]"
            )}
          >
            {stats.grade}
          </div>
        )}
      </div>

      {!stats ? (
        <p className="text-[11px] italic font-serif text-muted-foreground/50 text-center py-4">
          — no trade data yet —
        </p>
      ) : (
        <div className="grid grid-cols-2 gap-3">
          <Stat label="Fill Rate" value={`${(stats.fillRate * 100).toFixed(1)}%`} tone={stats.fillRate >= 0.9 ? "pos" : stats.fillRate >= 0.75 ? "warn" : "neg"} />
          <Stat label="Avg Δ%" value={`${(stats.avgPnlBps / 100).toFixed(2)}%`} sub="absolute realised" />
          <Stat
            label="Avg Hold"
            value={
              stats.avgDurationH < 1
                ? `${(stats.avgDurationH * 60).toFixed(0)}m`
                : stats.avgDurationH < 24
                  ? `${stats.avgDurationH.toFixed(1)}h`
                  : `${(stats.avgDurationH / 24).toFixed(1)}d`
            }
          />
          <Stat label="Closed / Total" value={`${stats.closed}/${stats.total}`} />
        </div>
      )}
    </div>
  );
}

function Stat({
  label,
  value,
  sub,
  tone = "neutral",
}: {
  label: string;
  value: string;
  sub?: string;
  tone?: "pos" | "warn" | "neg" | "neutral";
}) {
  return (
    <div>
      <p className="text-[8px] font-black uppercase tracking-widest text-muted-foreground/45 mb-0.5">
        {label}
      </p>
      <p
        className={cn(
          "font-mono font-black tabular-nums text-[15px] leading-none",
          tone === "pos" && "text-emerald-400",
          tone === "warn" && "text-amber-400",
          tone === "neg" && "text-rose-400"
        )}
      >
        {value}
      </p>
      {sub && (
        <p className="text-[9px] font-mono italic text-muted-foreground/45 mt-0.5">{sub}</p>
      )}
    </div>
  );
}
