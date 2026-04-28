import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { cn } from "@/lib/utils";
import { formatDateTime, formatPercentLoose, formatUsd, pnlColor } from "@/lib/format";

interface Props {
  symbol: string;
}

export function RecentExecutionsPanel({ symbol }: Props) {
  const tradesQ = useQuery({
    queryKey: ["trades", "filtered", { limit: 200 }],
    queryFn: () => dashboardApi.filteredTrades({ limit: 200 }),
    refetchInterval: 15_000,
  });

  const signalsQ = useQuery({
    queryKey: ["signals", "recent", 50],
    queryFn: () => dashboardApi.recentSignals(50),
    refetchInterval: 15_000,
  });

  const symbolTrades = useMemo(
    () => (tradesQ.data ?? []).filter((t) => t.symbol === symbol).slice(0, 8),
    [tradesQ.data, symbol]
  );

  const symbolSignals = useMemo(
    () => (signalsQ.data ?? []).filter((s) => s.symbol === symbol).slice(0, 6),
    [signalsQ.data, symbol]
  );

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden h-full flex flex-col">
      <div className="px-4 py-2.5 border-b border-white/5">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
          {symbol} Activity
        </p>
        <p className="text-[11px] font-serif italic text-muted-foreground/60">
          recent system signals &amp; executed trades
        </p>
      </div>

      <div className="flex-1 overflow-y-auto divide-y divide-white/5">
        {/* Recent signals */}
        <section className="p-3">
          <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/50 mb-2">
            Signals · {symbolSignals.length}
          </p>
          {symbolSignals.length === 0 ? (
            <p className="text-[10px] italic font-serif text-muted-foreground/50 py-2">
              — no recent signals on {symbol} —
            </p>
          ) : (
            <ul className="space-y-1.5">
              {symbolSignals.map((s) => (
                <li
                  key={s.id}
                  className="flex items-start gap-2.5 text-[11px] py-1 px-2 rounded hover:bg-white/[0.02]"
                >
                  <span
                    className={cn(
                      "shrink-0 px-1.5 py-0.5 rounded text-[8px] font-black uppercase tracking-widest",
                      s.side === "BUY"
                        ? "bg-emerald-400/10 text-emerald-400"
                        : "bg-rose-400/10 text-rose-400"
                    )}
                  >
                    {s.side}
                  </span>
                  <div className="min-w-0 flex-1">
                    <div className="flex items-baseline justify-between">
                      <span className="font-mono tabular-nums font-bold">
                        {fmtPrice(s.price)}
                      </span>
                      <span className="text-[9px] text-muted-foreground/50 font-mono">
                        {formatDateTime(s.timestamp)}
                      </span>
                    </div>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </section>

        {/* Recent closed trades */}
        <section className="p-3">
          <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/50 mb-2">
            Trades · {symbolTrades.length}
          </p>
          {symbolTrades.length === 0 ? (
            <p className="text-[10px] italic font-serif text-muted-foreground/50 py-2">
              — no trades yet on {symbol} —
            </p>
          ) : (
            <ul className="space-y-1.5">
              {symbolTrades.map((t) => (
                <li
                  key={t.id}
                  className="grid grid-cols-[auto,1fr,auto] items-center gap-2 py-1 px-2 rounded hover:bg-white/[0.02] text-[11px]"
                >
                  <span
                    className={cn(
                      "shrink-0 px-1.5 py-0.5 rounded text-[8px] font-black uppercase tracking-widest",
                      t.side === "LONG"
                        ? "bg-emerald-400/10 text-emerald-400"
                        : "bg-rose-400/10 text-rose-400"
                    )}
                  >
                    {t.side}
                  </span>
                  <div className="min-w-0">
                    <p className="font-mono tabular-nums">
                      {fmtPrice(t.entryPrice)} → {fmtPrice(t.exitPrice)}
                    </p>
                    <p className="text-[9px] text-muted-foreground/50 truncate">
                      {t.exitReason || t.strategyTag || "—"}
                    </p>
                  </div>
                  <div className="text-right">
                    <p className={cn("font-mono font-bold tabular-nums", pnlColor(t.realizedPnL))}>
                      {formatUsd(t.realizedPnL)}
                    </p>
                    <p className={cn("text-[9px] font-mono", pnlColor(t.pnlPercent))}>
                      {formatPercentLoose(t.pnlPercent)}
                    </p>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>
    </div>
  );
}

function fmtPrice(v: number): string {
  if (v >= 1000) return v.toLocaleString(undefined, { maximumFractionDigits: 2 });
  if (v >= 1) return v.toFixed(2);
  return v.toFixed(5);
}
