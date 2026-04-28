import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";

/**
 * Weekly performance summary — single screenshot-worthy card.
 * Aggregates the last 7 days of closed trades into:
 *   • Total trades + WR + net PnL (with delta vs prior 7d)
 *   • Best/worst trade (clickable into journal)
 *   • Daily PnL sparkline (7 bars, colour-coded green/red)
 *   • Edge status — relies on rolling-20 vs inception WR delta
 *
 * Uses the existing trade journal endpoint — no new backend dependency.
 */
export function WeeklyDigestCard() {
  const tradesQ = useQuery({
    queryKey: ["weekly-digest", "trades"],
    queryFn: () => dashboardApi.recentTrades(500),
    refetchInterval: 60_000,
    staleTime: 30_000,
  });

  const data = useMemo(() => {
    const all = (tradesQ.data ?? []).filter((t) => t.exitTime && t.realizedPnL !== undefined);
    if (all.length === 0) return null;

    const now = Date.now();
    const dayMs = 24 * 60 * 60 * 1000;
    const cutoff7  = now - 7  * dayMs;
    const cutoff14 = now - 14 * dayMs;

    const week  = all.filter((t) => new Date(t.exitTime!).getTime() >= cutoff7);
    const prior = all.filter((t) => {
      const ts = new Date(t.exitTime!).getTime();
      return ts >= cutoff14 && ts < cutoff7;
    });

    const sumPnl = (xs: typeof all) => xs.reduce((s, t) => s + (t.realizedPnL ?? 0), 0);
    const wins   = (xs: typeof all) => xs.filter((t) => (t.realizedPnL ?? 0) > 0).length;

    const weekPnl  = sumPnl(week);
    const priorPnl = sumPnl(prior);
    const wkWins   = wins(week);
    const wkWR     = week.length > 0 ? wkWins / week.length : 0;

    // Daily PnL bars for 7 days (today first → 6 days ago)
    const days: { date: string; pnl: number; trades: number }[] = [];
    for (let i = 6; i >= 0; i--) {
      const dStart = now - (i + 1) * dayMs;
      const dEnd   = now - i * dayMs;
      const dTrades = week.filter((t) => {
        const ts = new Date(t.exitTime!).getTime();
        return ts >= dStart && ts < dEnd;
      });
      const d = new Date(dEnd);
      days.push({
        date: d.toLocaleDateString(undefined, { weekday: "short" }),
        pnl: sumPnl(dTrades),
        trades: dTrades.length,
      });
    }

    // Best / worst trade
    let best  = week[0];
    let worst = week[0];
    for (const t of week) {
      if ((t.realizedPnL ?? 0) > (best?.realizedPnL ?? 0))  best  = t;
      if ((t.realizedPnL ?? 0) < (worst?.realizedPnL ?? 0)) worst = t;
    }

    // Edge status: rolling-20 WR vs all-time WR
    const allWins = wins(all);
    const inceptionWR = all.length > 0 ? allWins / all.length : 0;
    const last20 = all.slice(-20);
    const rollingWR = last20.length > 0 ? wins(last20) / last20.length : 0;
    const drift = rollingWR - inceptionWR;
    const edge: "STABLE" | "MONITORING" | "DECAY" =
      drift >= -0.05 ? "STABLE" :
      drift >= -0.15 ? "MONITORING" : "DECAY";

    const maxAbs = Math.max(...days.map((d) => Math.abs(d.pnl)), 1);

    return {
      week, prior,
      weekPnl, priorPnl,
      pnlDelta: weekPnl - priorPnl,
      wkWR, wkWins, wkCount: week.length,
      days, maxAbs,
      best, worst,
      inceptionWR, rollingWR, drift, edge,
    };
  }, [tradesQ.data]);

  if (!data || data.wkCount === 0) {
    return (
      <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5">
        <p className="text-[10px] font-black uppercase tracking-[0.25em] text-accent/80 mb-2">
          📅 Weekly Digest
        </p>
        <p className="text-[11px] italic font-serif text-muted-foreground/50 py-4 text-center">
          — no closed trades in the last 7 days —
        </p>
      </div>
    );
  }

  const edgeColor =
    data.edge === "STABLE"     ? "#37A95B" :
    data.edge === "MONITORING" ? "#F0883E" :
                                 "#F05A5A";
  const deltaSign = data.pnlDelta >= 0 ? "+" : "";

  return (
    <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5">
      <div className="flex items-baseline justify-between mb-4">
        <div>
          <p className="text-[10px] font-black uppercase tracking-[0.25em] text-accent/80">
            📅 Weekly Digest
          </p>
          <p className="text-[11px] font-serif italic text-muted-foreground/60 mt-0.5">
            last 7 days · {data.wkCount} closed trades
          </p>
        </div>
        <span
          className="px-2.5 py-1 rounded text-[9px] font-black uppercase tracking-widest"
          style={{ backgroundColor: `${edgeColor}22`, color: edgeColor }}
        >
          {data.edge === "DECAY" ? "⚠ " : ""}{data.edge}
        </span>
      </div>

      {/* Top row — three big stats */}
      <div className="grid grid-cols-3 gap-3 mb-4">
        <Stat
          label="Net P&L"
          value={formatUsd(data.weekPnl)}
          sub={`${deltaSign}${formatUsd(data.pnlDelta)} vs prev wk`}
          tone={data.weekPnl >= 0 ? "pos" : "neg"}
        />
        <Stat
          label="Win Rate"
          value={`${(data.wkWR * 100).toFixed(1)}%`}
          sub={`${data.wkWins} of ${data.wkCount}`}
          tone={data.wkWR >= 0.55 ? "pos" : data.wkWR >= 0.45 ? "neutral" : "neg"}
        />
        <Stat
          label="Edge Drift"
          value={`${data.drift >= 0 ? "+" : ""}${(data.drift * 100).toFixed(1)}pp`}
          sub={`R20 ${(data.rollingWR * 100).toFixed(0)}% vs inc ${(data.inceptionWR * 100).toFixed(0)}%`}
          tone={data.drift >= -0.05 ? "neutral" : data.drift >= -0.15 ? "warn" : "neg"}
        />
      </div>

      {/* Daily PnL sparkbars */}
      <div className="mb-4">
        <p className="text-[8px] uppercase tracking-widest text-muted-foreground/40 mb-1.5">Daily P&L</p>
        <div className="flex items-end gap-1 h-16">
          {data.days.map((d, i) => {
            const pct = data.maxAbs > 0 ? Math.abs(d.pnl) / data.maxAbs : 0;
            const h = Math.max(2, pct * 56);
            const isUp = d.pnl >= 0;
            return (
              <div key={i} className="flex-1 flex flex-col items-center gap-0.5">
                <div className="flex-1 flex items-end w-full">
                  <div
                    className={cn("w-full rounded-t transition-all", isUp ? "bg-emerald-500/70" : "bg-rose-500/70")}
                    style={{ height: `${h}px` }}
                    title={`${d.date}: ${formatUsd(d.pnl)} (${d.trades} trades)`}
                  />
                </div>
                <span className="text-[8px] font-mono text-muted-foreground/40">{d.date.slice(0, 1)}</span>
              </div>
            );
          })}
        </div>
      </div>

      {/* Best / Worst trade strip */}
      <div className="grid grid-cols-2 gap-2 text-[10px]">
        <div className="rounded-lg border border-emerald-500/15 bg-emerald-500/[0.04] px-2.5 py-2">
          <p className="text-[8px] uppercase tracking-widest text-emerald-400/70 mb-0.5">★ Best</p>
          {data.best && (
            <>
              <p className="font-mono font-bold text-emerald-300">
                {formatUsd(data.best.realizedPnL ?? 0)} · {data.best.symbol}
              </p>
              <p className="text-muted-foreground/50 text-[9px] mt-0.5 truncate">
                {data.best.strategyTag} · {data.best.exitReason ?? "—"}
              </p>
            </>
          )}
        </div>
        <div className="rounded-lg border border-rose-500/15 bg-rose-500/[0.04] px-2.5 py-2">
          <p className="text-[8px] uppercase tracking-widest text-rose-400/70 mb-0.5">↓ Worst</p>
          {data.worst && (
            <>
              <p className="font-mono font-bold text-rose-300">
                {formatUsd(data.worst.realizedPnL ?? 0)} · {data.worst.symbol}
              </p>
              <p className="text-muted-foreground/50 text-[9px] mt-0.5 truncate">
                {data.worst.strategyTag} · {data.worst.exitReason ?? "—"}
              </p>
            </>
          )}
        </div>
      </div>
    </div>
  );
}

function Stat({
  label, value, sub, tone = "neutral",
}: {
  label: string;
  value: string;
  sub?: string;
  tone?: "pos" | "neg" | "neutral" | "warn";
}) {
  const color =
    tone === "pos"  ? "text-emerald-400" :
    tone === "neg"  ? "text-rose-400"    :
    tone === "warn" ? "text-amber-400"   :
                      "text-foreground/85";
  return (
    <div>
      <p className="text-[8px] uppercase tracking-widest text-muted-foreground/50 mb-1">{label}</p>
      <p className={cn("font-mono text-lg font-black tabular-nums leading-none", color)}>{value}</p>
      {sub && <p className="text-[9px] text-muted-foreground/45 mt-1 leading-tight">{sub}</p>}
    </div>
  );
}
