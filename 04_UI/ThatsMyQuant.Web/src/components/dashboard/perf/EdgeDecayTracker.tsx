import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { Sparkline } from "@/components/charts/Sparkline";
import { cn } from "@/lib/utils";

/**
 * Per-strategy rolling Sharpe sparkline. Drift detection: if EWMA Sharpe over
 * the latest window drops below 60% of the all-time average, flag as decaying.
 */

const WINDOW = 20; // rolling window (in trades)

interface DecayRow {
  strategyTag: string;
  trades: number;
  allTimeSharpe: number;
  recentSharpe: number;
  decayRatio: number; // recent / all-time
  series: number[]; // rolling Sharpe per cumulative trade
  flag: "stable" | "weakening" | "decaying";
}

function computeRollingSharpe(returns: number[], window: number): number[] {
  const out: number[] = [];
  for (let i = 0; i < returns.length; i++) {
    const start = Math.max(0, i - window + 1);
    const slice = returns.slice(start, i + 1);
    if (slice.length < 3) {
      out.push(0);
      continue;
    }
    const mean = slice.reduce((s, r) => s + r, 0) / slice.length;
    const variance = slice.reduce((s, r) => s + (r - mean) ** 2, 0) / Math.max(1, slice.length - 1);
    const stdev = Math.sqrt(variance);
    const sharpe = stdev > 0 ? (mean / stdev) * Math.sqrt(252) : 0;
    out.push(sharpe);
  }
  return out;
}

export function EdgeDecayTracker() {
  const tradesQ = useQuery({
    queryKey: ["trades", "filtered", { limit: 500 }],
    queryFn: () => dashboardApi.filteredTrades({ limit: 500 }),
    refetchInterval: 60_000,
  });

  const rows = useMemo<DecayRow[]>(() => {
    const trades = (tradesQ.data ?? []).filter((t) => t.exitTime != null);
    if (trades.length === 0) return [];

    const byStrategy = new Map<string, typeof trades>();
    for (const t of trades) {
      const key = t.strategyTag || "Untagged";
      if (!byStrategy.has(key)) byStrategy.set(key, []);
      byStrategy.get(key)!.push(t);
    }

    return Array.from(byStrategy.entries())
      .filter(([, list]) => list.length >= 5)
      .map(([tag, list]) => {
        const sorted = [...list].sort(
          (a, b) => new Date(a.exitTime!).getTime() - new Date(b.exitTime!).getTime()
        );
        const returns = sorted.map((t) => t.pnlPercent);
        const series = computeRollingSharpe(returns, WINDOW);

        // All-time Sharpe
        const m = returns.reduce((s, r) => s + r, 0) / returns.length;
        const v = returns.reduce((s, r) => s + (r - m) ** 2, 0) / Math.max(1, returns.length - 1);
        const sd = Math.sqrt(v);
        const allTimeSharpe = sd > 0 ? (m / sd) * Math.sqrt(252) : 0;
        const recentSharpe = series[series.length - 1] ?? 0;
        const decayRatio = allTimeSharpe !== 0 ? recentSharpe / allTimeSharpe : 1;

        let flag: DecayRow["flag"] = "stable";
        if (allTimeSharpe > 0.5 && decayRatio < 0.6) flag = "decaying";
        else if (allTimeSharpe > 0.5 && decayRatio < 0.8) flag = "weakening";

        return {
          strategyTag: tag,
          trades: returns.length,
          allTimeSharpe,
          recentSharpe,
          decayRatio,
          series,
          flag,
        };
      })
      .sort((a, b) => a.decayRatio - b.decayRatio); // worst decay first
  }, [tradesQ.data]);

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      <div className="px-5 py-2.5 border-b border-white/5">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
          Edge Decay
        </p>
        <p className="text-[11px] font-serif italic text-muted-foreground/60">
          rolling {WINDOW}-trade Sharpe per strategy · drift detector
        </p>
      </div>

      {rows.length === 0 ? (
        <p className="p-6 text-xs italic font-serif text-muted-foreground/60 text-center">
          — need ≥5 closed trades per strategy —
        </p>
      ) : (
        <ul className="divide-y divide-white/[0.04]">
          {rows.map((r) => (
            <li key={r.strategyTag} className="px-4 py-3 flex items-center gap-4">
              <div className="min-w-0 flex-1">
                <div className="flex items-baseline gap-2">
                  <span className="font-bold text-[12px] tracking-tight truncate">{r.strategyTag}</span>
                  <Pill flag={r.flag} />
                </div>
                <p className="text-[10px] font-mono italic text-muted-foreground/60 mt-0.5">
                  all-time {r.allTimeSharpe.toFixed(2)} · recent {r.recentSharpe.toFixed(2)} · ratio{" "}
                  {(r.decayRatio * 100).toFixed(0)}%
                </p>
              </div>
              <Sparkline
                values={r.series}
                width={120}
                height={30}
                stroke={
                  r.flag === "decaying"
                    ? "rgba(181, 54, 75, 0.85)"
                    : r.flag === "weakening"
                      ? "rgba(160, 90, 0, 0.85)"
                      : "rgba(45, 106, 79, 0.85)"
                }
                strokeWidth={1.4}
              />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function Pill({ flag }: { flag: DecayRow["flag"] }) {
  const map = {
    stable: { bg: "bg-emerald-400/15 text-emerald-400 border-emerald-500/30", label: "STABLE" },
    weakening: { bg: "bg-amber-400/15 text-amber-400 border-amber-500/30", label: "WEAKENING" },
    decaying: { bg: "bg-rose-400/15 text-rose-400 border-rose-500/30", label: "DECAYING" },
  } as const;
  const v = map[flag];
  return (
    <span
      className={cn(
        "px-1.5 py-0.5 rounded text-[8px] font-black uppercase tracking-widest border",
        v.bg
      )}
    >
      {v.label}
    </span>
  );
}
