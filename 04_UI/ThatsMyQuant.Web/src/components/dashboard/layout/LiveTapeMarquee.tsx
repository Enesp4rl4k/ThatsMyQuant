import { useEffect, useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { cn } from "@/lib/utils";
import { formatUsd, pnlColor } from "@/lib/format";

/**
 * Bloomberg-style scrolling ticker — recent fills + market events. Mounted
 * at the bottom of the dashboard. Pauses on hover.
 */
export function LiveTapeMarquee() {
  const tradesQ = useQuery({
    queryKey: ["trades", "filtered", { limit: 10 }],
    queryFn: () => dashboardApi.filteredTrades({ limit: 10 }),
    refetchInterval: 15_000,
  });

  const [paused, setPaused] = useState(false);

  // Items recompute on data change; double them for seamless loop
  const items = useMemo(() => {
    const trades = (tradesQ.data ?? []).filter((t) => t.exitTime);
    if (trades.length === 0) return [];
    return [...trades, ...trades];
  }, [tradesQ.data]);

  // Lazy mount marquee CSS once
  useEffect(() => {
    const id = "tmq-marquee-style";
    if (document.getElementById(id)) return;
    const style = document.createElement("style");
    style.id = id;
    style.innerHTML = `
      @keyframes tmqMarqueeScroll {
        from { transform: translateX(0); }
        to   { transform: translateX(-50%); }
      }
      .tmq-marquee-track {
        animation: tmqMarqueeScroll 60s linear infinite;
      }
      .tmq-marquee-track:hover, .tmq-marquee-track[data-paused="true"] {
        animation-play-state: paused;
      }
    `;
    document.head.appendChild(style);
  }, []);

  if (items.length === 0) return null;

  return (
    <div
      className="rounded-xl border border-white/5 bg-[#0D1117]/80 backdrop-blur-xl overflow-hidden"
      onMouseEnter={() => setPaused(true)}
      onMouseLeave={() => setPaused(false)}
    >
      <div className="flex items-center">
        <div className="shrink-0 px-3 py-2 bg-rose-500/10 border-r border-white/5">
          <span className="text-[9px] font-black uppercase tracking-[0.3em] text-rose-400 flex items-center gap-2">
            <span className="w-1.5 h-1.5 rounded-full bg-rose-400 animate-pulse" />
            LIVE
          </span>
        </div>
        <div className="flex-1 overflow-hidden whitespace-nowrap">
          <div
            className="tmq-marquee-track inline-flex items-center gap-6 py-2 px-4"
            data-paused={paused}
          >
            {items.map((t, i) => (
              <span
                key={`${t.id}-${i}`}
                className="inline-flex items-center gap-2 text-[11px] font-mono"
              >
                <span className="text-muted-foreground/60">
                  {new Date(t.exitTime!).toLocaleTimeString([], {
                    hour: "2-digit",
                    minute: "2-digit",
                  })}
                </span>
                <span className="font-bold">{t.symbol}</span>
                <span
                  className={cn(
                    "px-1 rounded text-[9px] font-black",
                    t.side === "LONG"
                      ? "bg-emerald-400/10 text-emerald-400"
                      : "bg-rose-400/10 text-rose-400"
                  )}
                >
                  {t.side}
                </span>
                <span className={cn("font-bold tabular-nums", pnlColor(t.realizedPnL))}>
                  {formatUsd(t.realizedPnL)}
                </span>
                <span className={cn("text-[10px] tabular-nums", pnlColor(t.pnlPercent))}>
                  ({t.pnlPercent >= 0 ? "+" : ""}
                  {(t.pnlPercent * 100).toFixed(2)}%)
                </span>
                <span className="text-muted-foreground/50 italic font-serif">
                  {t.exitReason || t.strategyTag}
                </span>
                <span className="text-muted-foreground/30">·</span>
              </span>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}
