import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";

interface Props {
  symbol: string;
  /** Max rows to display. */
  limit?: number;
}

/**
 * Compact "Time & Sales" tape — most recent closed trades on the symbol,
 * scrollable list. Each row: time · side pill · qty · entry → exit · PnL.
 * Pulls from /api/trades?limit=200, filtered client-side by symbol.
 */
export function TimeSalesTape({ symbol, limit = 50 }: Props) {
  const tradesQ = useQuery({
    queryKey: ["t-and-s", "trades"],
    queryFn:  () => dashboardApi.recentTrades(200),
    refetchInterval: 30_000,
  });

  const rows = useMemo(() => {
    const sym = symbol.toUpperCase();
    return (tradesQ.data ?? [])
      .filter((t) => t.symbol.toUpperCase() === sym)
      .slice(0, limit);
  }, [tradesQ.data, symbol, limit]);

  return (
    <div className="rounded-xl border border-white/[0.05] bg-[#0A0A0A]/80 backdrop-blur-xl overflow-hidden flex flex-col h-full">
      <div className="px-3 py-2 border-b border-white/[0.05] flex items-baseline justify-between shrink-0">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
          Time &amp; Sales
        </p>
        <p className="text-[9px] font-mono text-muted-foreground/45">{rows.length} on {symbol}</p>
      </div>

      {rows.length === 0 ? (
        <p className="text-[11px] italic font-serif text-muted-foreground/50 text-center py-6">
          — no closed trades on this symbol —
        </p>
      ) : (
        <div className="flex-1 overflow-y-auto text-[10px] font-mono">
          <div className="grid grid-cols-[60px_46px_60px_1fr_72px] gap-2 px-3 py-1 text-[8px] uppercase tracking-widest text-muted-foreground/45 border-b border-white/[0.04] sticky top-0 bg-black/85 backdrop-blur-sm">
            <span>Time</span>
            <span>Side</span>
            <span className="text-right">Qty</span>
            <span className="text-right">Px</span>
            <span className="text-right">PnL</span>
          </div>
          {rows.map((t) => {
            const isLong = t.side === "LONG";
            const sideCls = isLong
              ? "bg-emerald-500/10 text-emerald-300 border-emerald-400/25"
              : "bg-rose-500/10 text-rose-300 border-rose-400/25";
            const pnlTone = t.realizedPnL >= 0 ? "text-emerald-300" : "text-rose-300";
            return (
              <div
                key={t.id}
                className="grid grid-cols-[60px_46px_60px_1fr_72px] gap-2 px-3 py-1 hover:bg-white/[0.02] border-b border-white/[0.02]"
                title={`${t.entryTime} → ${t.exitTime}`}
              >
                <span className="text-muted-foreground/55">{fmtTime(t.exitTime ?? t.entryTime)}</span>
                <span className={cn("rounded-full border px-1 text-[8px] uppercase tracking-widest text-center font-black", sideCls)}>
                  {isLong ? "L" : "S"}
                </span>
                <span className="text-right text-foreground/75 tabular-nums">{t.quantity.toFixed(2)}</span>
                <span className="text-right text-muted-foreground/65 tabular-nums">
                  {formatUsd(t.exitPrice || t.entryPrice)}
                </span>
                <span className={cn("text-right font-bold tabular-nums", pnlTone)}>
                  {formatUsd(t.realizedPnL)}
                </span>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

function fmtTime(iso: string | null): string {
  if (!iso) return "—";
  try {
    const d = new Date(iso);
    return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
  } catch { return ""; }
}
