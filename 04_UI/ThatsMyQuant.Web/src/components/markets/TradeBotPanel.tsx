import { useQuery } from "@tanstack/react-query";
import { Link } from "react-router-dom";
import { proModeApi } from "@/api/proMode";
import { cn } from "@/lib/utils";

const STATE_TONE: Record<string, string> = {
  Armed: "bg-emerald-500/15 text-emerald-400 border-emerald-500/40",
  Warning: "bg-amber-500/15 text-amber-400 border-amber-500/40",
  Tripped: "bg-rose-500/15 text-rose-400 border-rose-500/40",
};

/**
 * Read-only bot status panel for the Markets sidebar. Shows armed state,
 * circuit breaker, regime, leverage. All actual controls live on Pro Mode
 * — single source of truth (avoids divergent ARM/DISARM state across pages).
 */
export function TradeBotPanel() {
  const q = useQuery({
    queryKey: ["pro-mode", "snapshot"],
    queryFn: proModeApi.snapshot,
    refetchInterval: 10_000,
  });

  const snap = q.data;
  const cb = snap?.circuitBreaker;
  const armed = snap?.isArmed ?? false;
  const ddPct = cb ? cb.realizedDrawdown * 100 : 0;

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl flex flex-col h-full">
      {/* Header */}
      <div className="px-4 py-3 border-b border-white/5">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
          Trade Bot
        </p>
        <p className="text-sm font-bold">
          Status{" "}
          <span className="text-muted-foreground/50 font-light text-xs">
            — read-only view
          </span>
        </p>
      </div>

      <div className="p-4 space-y-4 flex-1 overflow-y-auto">
        {/* Status block */}
        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <span className="text-[9px] uppercase tracking-widest text-muted-foreground/60">
              Armed
            </span>
            <span
              className={cn(
                "px-2 py-0.5 rounded text-[9px] font-black uppercase tracking-widest border",
                armed
                  ? "bg-accent/15 text-accent border-accent/40"
                  : "bg-white/5 text-muted-foreground border-white/10"
              )}
            >
              {armed ? "● ARMED" : "○ DISARMED"}
            </span>
          </div>

          {cb && (
            <>
              <div className="flex items-center justify-between">
                <span className="text-[9px] uppercase tracking-widest text-muted-foreground/60">
                  Circuit
                </span>
                <span
                  className={cn(
                    "px-2 py-0.5 rounded text-[9px] font-black uppercase tracking-widest border",
                    STATE_TONE[cb.state] ?? "bg-white/5 text-muted-foreground border-white/10"
                  )}
                >
                  {cb.state}
                </span>
              </div>

              <div className="grid grid-cols-2 gap-2 pt-2 border-t border-white/5">
                <Cell label="Equity" value={`$${cb.currentEquity.toFixed(0)}`} />
                <Cell label="Peak" value={`$${cb.peakEquity.toFixed(0)}`} />
                <Cell
                  label="Drawdown"
                  value={`${ddPct.toFixed(2)}%`}
                  tone={ddPct > 3 ? "neg" : ddPct > 1 ? "warn" : "neutral"}
                />
                <Cell label="Regime" value={snap.currentRegime || "—"} />
              </div>

              {cb.lastTripReason && (
                <p className="text-[10px] italic font-serif text-rose-300 mt-2 leading-snug">
                  Last trip: {cb.lastTripReason}
                </p>
              )}
            </>
          )}
        </div>

        {/* Leverage display */}
        {snap && (
          <div className="pt-3 border-t border-white/5">
            <div className="flex items-center justify-between text-[10px]">
              <span className="text-muted-foreground/60 uppercase tracking-widest text-[9px]">
                Leverage
              </span>
              <span className="font-mono font-bold tabular-nums">
                {snap.baseLeverage.toFixed(1)}× → {snap.effectiveLeverage.toFixed(1)}×
              </span>
            </div>
            {snap.leverageExplanation && (
              <p className="text-[9px] text-muted-foreground/50 font-mono italic mt-1 leading-snug">
                {snap.leverageExplanation}
              </p>
            )}
          </div>
        )}

        {/* Recent funding scans */}
        {snap && snap.recentOpportunities.length > 0 && (
          <div className="pt-3 border-t border-white/5">
            <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/50 mb-1.5">
              Recent funding scans
            </p>
            <ul className="space-y-1">
              {snap.recentOpportunities.slice(0, 4).map((o, i) => (
                <li
                  key={i}
                  className="flex items-center justify-between text-[10px] font-mono"
                >
                  <span className="font-bold">{o.symbol}</span>
                  <span
                    className={cn(
                      "tabular-nums",
                      o.fundingBps > 0 ? "text-emerald-400" : "text-rose-400"
                    )}
                  >
                    {o.fundingBps > 0 ? "+" : ""}
                    {o.fundingBps.toFixed(1)} bps
                  </span>
                </li>
              ))}
            </ul>
          </div>
        )}

        {/* Single source of truth → Pro Mode */}
        <div className="pt-3 border-t border-white/5">
          <Link
            to="/pro-mode"
            className="flex items-center justify-between gap-2 px-3 py-2 rounded-md bg-accent/[0.08] border border-accent/30 hover:bg-accent/15 transition-colors text-[11px]"
          >
            <span className="font-black uppercase tracking-widest text-accent">
              Open Pro Mode
            </span>
            <span className="text-[9px] font-serif italic text-accent/70">
              arm / disarm / kill
            </span>
            <svg width="12" height="12" viewBox="0 0 16 16" fill="none">
              <path
                d="M3 8h10M9 4l4 4-4 4"
                stroke="currentColor"
                strokeWidth="1.4"
                strokeLinecap="round"
                className="text-accent"
              />
            </svg>
          </Link>
          <p className="text-[9px] font-mono italic text-muted-foreground/40 mt-1.5 text-center">
            controls live on Pro Mode · single source of truth
          </p>
        </div>
      </div>
    </div>
  );
}

function Cell({
  label,
  value,
  tone = "neutral",
}: {
  label: string;
  value: string;
  tone?: "pos" | "neg" | "warn" | "neutral";
}) {
  return (
    <div>
      <p className="text-[8px] uppercase tracking-widest text-muted-foreground/50 mb-0.5">{label}</p>
      <p
        className={cn(
          "font-mono font-bold text-[11px] tabular-nums",
          tone === "pos" && "text-emerald-400",
          tone === "neg" && "text-rose-400",
          tone === "warn" && "text-amber-400"
        )}
      >
        {value}
      </p>
    </div>
  );
}
