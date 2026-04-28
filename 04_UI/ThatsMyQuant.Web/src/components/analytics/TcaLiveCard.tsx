import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";

/**
 * Transaction Cost Analysis — rolling-window estimation derived from closed
 * trades. Shows how much of GROSS PnL is being eaten by execution friction.
 *
 *   Gross PnL  = Σ (exit - entry) × qty × side
 *   Est Fees   = Σ |notional| × FEE_RATE (round-trip)
 *   Net PnL    = recorded RealizedPnL (already net of internal fees)
 *   Leakage %  = (Gross - Net) / Gross × 100
 *
 * Color tiers:
 *   < 25% leakage → green   (alpha intact)
 *   25–50%       → amber   (watch)
 *   > 50%        → rose    (execution killing edge)
 *
 * Backend has TcaLiveAnalyzerHostedService running every 30 min — when its
 * dedicated endpoint is wired, swap to that. For now, compute live from
 * /api/trades so UI is informative immediately.
 */
export function TcaLiveCard({
  windowSize = 50,
  feeRate = 0.0008,  // 8 bps round-trip default (matches DailyDigest estimate)
}: {
  windowSize?: number;
  feeRate?: number;
}) {
  const tradesQ = useQuery({
    queryKey: ["tca-live", "trades"],
    queryFn:  () => dashboardApi.recentTrades(200),
    refetchInterval: 60_000,
  });

  const stats = useMemo(() => {
    const closed = (tradesQ.data ?? [])
      .filter((t) => t.status?.toLowerCase().includes("clos") || t.exitTime)
      .slice(0, windowSize);
    if (closed.length === 0) return null;

    let netPnl = 0, grossEst = 0, fees = 0, slipEst = 0;
    let wins = 0, losses = 0;
    for (const t of closed) {
      const qty = t.quantity > 0 ? t.quantity : 0;
      const px  = (t.entryPrice + (t.exitPrice || t.entryPrice)) / 2;
      const notional = Math.abs(qty * px);
      const f = notional * feeRate;
      fees     += f;
      slipEst  += notional * 0.0002; // 2 bps slip estimate
      netPnl   += t.realizedPnL ?? 0;
      grossEst += (t.realizedPnL ?? 0) + f;
      if ((t.realizedPnL ?? 0) > 0) wins++;
      else if ((t.realizedPnL ?? 0) < 0) losses++;
    }
    const leakagePct = grossEst > 0 ? (fees / grossEst) * 100 : 0;
    const tier = leakagePct < 25 ? "ok" : leakagePct < 50 ? "warn" : "danger";
    const wr = closed.length > 0 ? (wins / closed.length) * 100 : 0;

    return {
      count: closed.length,
      netPnl,
      grossEst,
      fees,
      slipEst,
      leakagePct,
      tier,
      wins, losses, wr,
    };
  }, [tradesQ.data, windowSize, feeRate]);

  if (!stats) {
    return (
      <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-2">
          💰 TCA · Cost Leakage
        </p>
        <p className="text-[11px] italic font-serif text-muted-foreground/55 text-center py-3">
          — needs ≥1 closed trade —
        </p>
      </div>
    );
  }

  const leakageColor =
    stats.tier === "ok"     ? "#22C55E" :
    stats.tier === "warn"   ? "#F59E0B" :
                              "#F05A5A";
  const leakageBg =
    stats.tier === "ok"   ? "border-emerald-400/30 bg-emerald-500/10 text-emerald-200" :
    stats.tier === "warn" ? "border-amber-400/30   bg-amber-500/10   text-amber-200"   :
                            "border-rose-400/30    bg-rose-500/10    text-rose-200";

  return (
    <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
      <div className="flex items-baseline justify-between mb-3">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
            💰 TCA · Cost Leakage
          </p>
          <p className="text-[10px] font-mono text-muted-foreground/45 mt-0.5">
            rolling last {stats.count} closed trades · fee rate {(feeRate * 10000).toFixed(0)}bp
          </p>
        </div>
        <span className={cn(
          "px-2.5 py-1 rounded-full text-[9px] font-mono font-black uppercase tracking-widest border",
          leakageBg
        )}>
          {stats.tier === "ok" ? "✓ HEALTHY" : stats.tier === "warn" ? "⚠ ELEVATED" : "🚨 CRITICAL"}
        </span>
      </div>

      {/* Big leakage % */}
      <div className="mb-3 text-center">
        <p className="text-[8px] uppercase tracking-widest text-muted-foreground/45 mb-1">Leakage</p>
        <p className="font-mono text-4xl font-black tabular-nums leading-none" style={{ color: leakageColor }}>
          {stats.leakagePct.toFixed(1)}%
        </p>
        <p className="text-[10px] font-mono text-muted-foreground/55 mt-1">
          {formatUsd(stats.fees)} fees on {formatUsd(stats.grossEst)} gross
        </p>
      </div>

      {/* Bar visualisation */}
      <div className="relative h-2 rounded-full bg-white/[0.05] overflow-hidden mb-3">
        <div
          className="absolute inset-y-0 left-0 rounded-full transition-all duration-700"
          style={{ width: `${Math.min(100, stats.leakagePct)}%`, backgroundColor: leakageColor, opacity: 0.85 }}
        />
        {/* threshold ticks at 25 and 50 */}
        <div className="absolute top-0 bottom-0 w-px bg-amber-400/60" style={{ left: "25%" }} title="Warn" />
        <div className="absolute top-0 bottom-0 w-px bg-rose-400/60"  style={{ left: "50%" }} title="Critical" />
      </div>

      {/* Stats row */}
      <div className="grid grid-cols-4 gap-2 text-[10px] font-mono">
        <Stat label="Net PnL"  value={formatUsd(stats.netPnl)}  tone={stats.netPnl >= 0 ? "text-emerald-300" : "text-rose-300"} />
        <Stat label="Gross"    value={formatUsd(stats.grossEst)} tone="text-foreground/80" />
        <Stat label="Fees"     value={formatUsd(stats.fees)}     tone="text-amber-300" />
        <Stat label="Slip est" value={formatUsd(stats.slipEst)}  tone="text-muted-foreground/65" />
      </div>

      <p className="mt-3 pt-3 border-t border-white/[0.04] text-[10px] font-serif italic text-muted-foreground/50 leading-relaxed">
        {stats.tier === "ok"
          ? "Edge is intact. Less than a quarter of gross PnL going to execution costs."
          : stats.tier === "warn"
            ? "Costs are eating a meaningful slice of the edge. Consider larger position sizes (fixed cost, more profit) or maker-only mode."
            : "Execution costs are dominating PnL. Either alpha is too thin OR fees are too high. Review TCA per-strategy attribution."}
      </p>
    </div>
  );
}

function Stat({ label, value, tone }: { label: string; value: string; tone: string }) {
  return (
    <div className="text-center">
      <p className="text-[8px] uppercase tracking-widest text-muted-foreground/40 mb-0.5">{label}</p>
      <p className={cn("font-bold tabular-nums", tone)}>{value}</p>
    </div>
  );
}
