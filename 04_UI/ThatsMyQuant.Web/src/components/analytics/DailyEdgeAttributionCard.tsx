import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";

/**
 * Day-by-day edge attribution. Builds a small N-day matrix where each row is
 * a date and each column is a strategy family — the cell value is that
 * day's net realized PnL from that strategy. Rightmost column is the daily
 * total. Bottom row is the strategy total.
 *
 *   day        Pairs   Funding  Manual   Total
 *   2026-04-26  +12.40  +3.10    -0.40    +15.10
 *   2026-04-25  -3.20   +1.80    0        -1.40
 *   ...
 *
 * Lets the operator answer: "did pairs make today's profit, or was it
 * funding?" — a question the existing PnlAttributionCard answers in
 * aggregate but not over time.
 */
export function DailyEdgeAttributionCard({ days = 14 }: { days?: number }) {
  const tradesQ = useQuery({
    queryKey: ["edge-attr", "trades"],
    queryFn:  () => dashboardApi.recentTrades(500),
    refetchInterval: 60_000,
  });

  const matrix = useMemo(() => {
    const cutoff = Date.now() - days * 24 * 60 * 60 * 1000;
    const closed = (tradesQ.data ?? [])
      .filter((t) => t.exitTime && new Date(t.exitTime).getTime() >= cutoff && t.realizedPnL !== undefined);
    if (closed.length === 0) return null;

    // Bucket strategy tags into 4 families
    function family(tag: string): "Pairs" | "Funding" | "Manual" | "Other" {
      const t = (tag || "").toLowerCase();
      if (t.includes("pair"))     return "Pairs";
      if (t.includes("fund"))     return "Funding";
      if (t.includes("manual"))   return "Manual";
      return "Other";
    }

    const families: ("Pairs" | "Funding" | "Manual" | "Other")[] = ["Pairs", "Funding", "Manual", "Other"];
    const byDay = new Map<string, Record<string, number>>();
    let totalsByFamily: Record<string, number> = { Pairs: 0, Funding: 0, Manual: 0, Other: 0, _Total: 0 };

    for (const t of closed) {
      const day = (t.exitTime ?? "").slice(0, 10);
      const fam = family(t.strategyTag ?? "");
      const pnl = t.realizedPnL ?? 0;
      if (!byDay.has(day)) byDay.set(day, { Pairs: 0, Funding: 0, Manual: 0, Other: 0, _Total: 0 });
      const row = byDay.get(day)!;
      row[fam] += pnl;
      row._Total += pnl;
      totalsByFamily[fam] += pnl;
      totalsByFamily._Total += pnl;
    }

    const sortedDays = Array.from(byDay.keys()).sort().reverse();
    return { families, byDay, totalsByFamily, days: sortedDays };
  }, [tradesQ.data, days]);

  if (!matrix) {
    return (
      <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-2">
          📅 Daily Edge Attribution
        </p>
        <p className="text-[11px] italic font-serif text-muted-foreground/55 text-center py-3">
          — no closed trades in the last {days} days —
        </p>
      </div>
    );
  }

  // Find max abs cell value for color intensity
  const maxAbs = Math.max(
    1,
    ...matrix.days.flatMap((d) => matrix.families.map((f) => Math.abs(matrix.byDay.get(d)?.[f] ?? 0))),
  );

  return (
    <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
      <div className="flex items-baseline justify-between mb-3">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
          📅 Daily Edge Attribution
        </p>
        <p className="text-[10px] font-mono text-muted-foreground/45">
          last {matrix.days.length} active days
        </p>
      </div>

      <div className="overflow-x-auto">
        <table className="w-full text-[10px] font-mono">
          <thead>
            <tr className="text-[8px] uppercase tracking-widest text-muted-foreground/45">
              <th className="text-left px-1 py-1 font-mono">Date</th>
              {matrix.families.map((f) => (
                <th key={f} className="text-right px-1 py-1 font-mono">{f}</th>
              ))}
              <th className="text-right px-1 py-1 font-mono text-foreground/65 border-l border-white/[0.05]">Total</th>
            </tr>
          </thead>
          <tbody>
            {matrix.days.map((day) => {
              const row = matrix.byDay.get(day)!;
              return (
                <tr key={day} className="border-t border-white/[0.03] hover:bg-white/[0.02]">
                  <td className="px-1 py-0.5 text-muted-foreground/65">{day}</td>
                  {matrix.families.map((f) => {
                    const v = row[f];
                    const intensity = Math.min(1, Math.abs(v) / maxAbs);
                    const bg = v === 0 ? "transparent" : v > 0
                      ? `rgba(34,197,94,${(intensity * 0.35).toFixed(3)})`
                      : `rgba(240,90,90,${(intensity * 0.35).toFixed(3)})`;
                    return (
                      <td
                        key={f}
                        className={cn(
                          "px-1 py-0.5 text-right tabular-nums",
                          v > 0 ? "text-emerald-300" : v < 0 ? "text-rose-300" : "text-muted-foreground/35"
                        )}
                        style={{ backgroundColor: bg }}
                      >
                        {v === 0 ? "—" : (v > 0 ? "+" : "") + formatUsd(v)}
                      </td>
                    );
                  })}
                  <td className={cn(
                    "px-1 py-0.5 text-right tabular-nums font-bold border-l border-white/[0.04]",
                    row._Total > 0 ? "text-emerald-200" : row._Total < 0 ? "text-rose-200" : "text-muted-foreground/55"
                  )}>
                    {row._Total > 0 ? "+" : ""}{formatUsd(row._Total)}
                  </td>
                </tr>
              );
            })}
            {/* Bottom totals row */}
            <tr className="border-t-2 border-white/[0.08] font-bold">
              <td className="px-1 py-1 text-muted-foreground/55 uppercase tracking-widest text-[8px]">Σ {matrix.days.length}d</td>
              {matrix.families.map((f) => {
                const v = matrix.totalsByFamily[f];
                return (
                  <td key={f} className={cn(
                    "px-1 py-1 text-right tabular-nums",
                    v > 0 ? "text-emerald-200" : v < 0 ? "text-rose-200" : "text-muted-foreground/35"
                  )}>
                    {v === 0 ? "—" : (v > 0 ? "+" : "") + formatUsd(v)}
                  </td>
                );
              })}
              <td className={cn(
                "px-1 py-1 text-right tabular-nums font-black border-l border-white/[0.04]",
                matrix.totalsByFamily._Total > 0 ? "text-emerald-200" : matrix.totalsByFamily._Total < 0 ? "text-rose-200" : "text-muted-foreground/55"
              )}>
                {matrix.totalsByFamily._Total > 0 ? "+" : ""}{formatUsd(matrix.totalsByFamily._Total)}
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>
  );
}
