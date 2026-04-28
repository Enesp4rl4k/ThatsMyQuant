import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";

/**
 * "Today's session at a glance" — single-line strip showing:
 *   • Number of trades closed today
 *   • Net realized P&L for today (color-coded)
 *   • Today's win rate
 *   • Day's worst dip from peak (intraday DD)
 *
 * Intentionally compact — drops into the Trade Mode header next to the bot
 * status strip so the operator always knows "how am I doing today?" without
 * navigating away. Refreshes every 60 s.
 */
export function SessionStatsBadge() {
  const tradesQ = useQuery({
    queryKey: ["session-stats", "trades"],
    queryFn:  () => dashboardApi.recentTrades(100),
    refetchInterval: 60_000,
  });

  const stats = useMemo(() => {
    const todayStart = new Date();
    todayStart.setHours(0, 0, 0, 0);
    const todayMs = todayStart.getTime();

    const todays = (tradesQ.data ?? []).filter((t) => {
      if (!t.exitTime) return false;
      const ts = new Date(t.exitTime).getTime();
      return Number.isFinite(ts) && ts >= todayMs;
    });

    if (todays.length === 0) {
      return { count: 0, wins: 0, pnl: 0, wr: 0, peak: 0, dip: 0 };
    }

    // Sort chronologically for intraday DD calc
    todays.sort((a, b) => new Date(a.exitTime!).getTime() - new Date(b.exitTime!).getTime());

    let cum = 0;
    let peak = 0;
    let worstDip = 0;
    let wins = 0;
    for (const t of todays) {
      const pnl = t.realizedPnL ?? 0;
      cum += pnl;
      if (cum > peak) peak = cum;
      const dip = cum - peak;
      if (dip < worstDip) worstDip = dip;
      if (pnl > 0) wins++;
    }
    return {
      count: todays.length,
      wins,
      pnl: cum,
      wr: (wins / todays.length) * 100,
      peak,
      dip: worstDip,
    };
  }, [tradesQ.data]);

  const pnlTone = stats.pnl > 0 ? "text-emerald-300" : stats.pnl < 0 ? "text-rose-300" : "text-muted-foreground/65";
  const wrTone  = stats.wr >= 55 ? "text-emerald-300" : stats.wr >= 45 ? "text-amber-300" : "text-rose-300";

  return (
    <div className="flex items-center gap-3 px-3 py-1.5 rounded-md border border-white/[0.06] bg-white/[0.02] text-[10px] font-mono">
      <span className="uppercase tracking-widest text-[9px] text-muted-foreground/45">Today</span>
      <Stat label="N"   value={stats.count.toString()} />
      <Sep />
      <Stat label="PnL" value={stats.count === 0 ? "—" : (stats.pnl > 0 ? "+" : "") + formatUsd(stats.pnl)} valueClass={pnlTone} />
      <Sep />
      <Stat label="WR"  value={stats.count === 0 ? "—" : `${stats.wr.toFixed(0)}%`}  valueClass={wrTone} />
      <Sep />
      <Stat label="Dip" value={stats.dip < 0 ? formatUsd(stats.dip) : "—"} valueClass={stats.dip < -10 ? "text-rose-300" : "text-muted-foreground/65"} />
    </div>
  );
}

function Stat({ label, value, valueClass = "text-foreground/85" }: { label: string; value: string; valueClass?: string }) {
  return (
    <span className="flex items-baseline gap-1">
      <span className="uppercase tracking-widest text-[9px] text-muted-foreground/45">{label}</span>
      <span className={cn("font-bold tabular-nums", valueClass)}>{value}</span>
    </span>
  );
}

function Sep() {
  return <span className="w-px h-3 bg-white/[0.08]" aria-hidden />;
}
