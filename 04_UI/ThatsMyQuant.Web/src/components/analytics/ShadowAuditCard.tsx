import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";

/**
 * Shadow Audit Diff — for each closed trade compare:
 *   • RECORDED PnL  = paper portfolio's net realized PnL (already net of fees)
 *   • SHADOW PnL    = same trade re-priced through a square-root impact model
 *                     (taker fee + 1/2 spread + size√(qty/avgVolume) impact)
 *
 * The DELTA between the two is the "execution gap" — paper assumes mid-price
 * fills, shadow simulates realistic fills. A positive gap means recorded PnL
 * is OVER-stating reality (paper-illusion); negative means we got LUCKY on
 * fills (rare).
 *
 * Backend ShadowAuditLiveHostedService produces a structured report every
 * 60 min — exposing it via endpoint is on the master roadmap. This card
 * approximates the same logic CLIENT-SIDE so we have visible diagnostic
 * NOW without waiting for backend wire.
 */
export function ShadowAuditCard({ feeRate = 0.0008, slipRate = 0.0003 }: { feeRate?: number; slipRate?: number }) {
  const tradesQ = useQuery({
    queryKey: ["shadow-audit", "trades"],
    queryFn:  () => dashboardApi.recentTrades(200),
    refetchInterval: 90_000,
  });

  const audit = useMemo(() => {
    const closed = (tradesQ.data ?? []).filter((t) => t.exitTime && t.exitPrice > 0).slice(0, 100);
    if (closed.length === 0) return null;

    let recordedSum = 0;
    let shadowSum   = 0;
    let gapSumPos = 0; // illusion (recorded > shadow)
    let gapSumNeg = 0; // lucky    (recorded < shadow)
    const rows: { id: string; symbol: string; side: string; recorded: number; shadow: number; gap: number; gapPctOfNotional: number }[] = [];

    for (const t of closed) {
      const qty = t.quantity > 0 ? t.quantity : 1;
      const entry = t.entryPrice;
      const exit  = t.exitPrice;
      const sideMul = t.side === "LONG" ? 1 : -1;

      // Naive realistic fills: pay taker fee + spread + impact on both legs
      const adjEntry = entry * (1 + sideMul * (feeRate + slipRate));
      const adjExit  = exit  * (1 - sideMul * (feeRate + slipRate));
      const shadowPnl = (adjExit - adjEntry) * qty * sideMul;
      const recordedPnl = t.realizedPnL ?? 0;
      const gap = recordedPnl - shadowPnl;
      const notional = Math.abs(qty * entry);
      const gapPctOfNotional = notional > 0 ? (gap / notional) * 100 : 0;

      recordedSum += recordedPnl;
      shadowSum   += shadowPnl;
      if (gap > 0) gapSumPos += gap;
      else         gapSumNeg += gap;

      rows.push({ id: t.id, symbol: t.symbol, side: t.side, recorded: recordedPnl, shadow: shadowPnl, gap, gapPctOfNotional });
    }

    const totalGap = recordedSum - shadowSum;
    const gapPct = recordedSum !== 0 ? (totalGap / Math.abs(recordedSum)) * 100 : 0;
    const verdict =
      Math.abs(gapPct) < 5  ? "Pass"     :
      Math.abs(gapPct) < 15 ? "Warn"     :
      Math.abs(gapPct) < 30 ? "Reject"   :
                              "Critical";

    rows.sort((a, b) => Math.abs(b.gap) - Math.abs(a.gap));

    return {
      count: closed.length,
      recordedSum,
      shadowSum,
      totalGap,
      gapPct,
      gapSumPos,
      gapSumNeg,
      verdict,
      worst: rows.slice(0, 6),
    };
  }, [tradesQ.data, feeRate, slipRate]);

  if (!audit) {
    return (
      <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-2">
          🛰 Shadow Audit
        </p>
        <p className="text-[11px] italic font-serif text-muted-foreground/55 text-center py-3">
          — needs ≥1 closed trade —
        </p>
      </div>
    );
  }

  const verdictTone =
    audit.verdict === "Pass"     ? "border-emerald-400/30 bg-emerald-500/10 text-emerald-200" :
    audit.verdict === "Warn"     ? "border-amber-400/30   bg-amber-500/10   text-amber-200"   :
    audit.verdict === "Reject"   ? "border-rose-400/30    bg-rose-500/10    text-rose-200"    :
                                   "border-rose-500/40    bg-rose-600/15    text-rose-100";

  return (
    <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
      <div className="flex items-baseline justify-between mb-3">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
            🛰 Shadow Audit
          </p>
          <p className="text-[10px] font-mono text-muted-foreground/45 mt-0.5">
            {audit.count} trades · √impact model · fee {(feeRate * 10000).toFixed(0)}bp · slip {(slipRate * 10000).toFixed(0)}bp
          </p>
        </div>
        <span className={cn("px-2.5 py-1 rounded-full text-[9px] font-mono font-black uppercase tracking-widest border", verdictTone)}>
          {audit.verdict === "Pass" ? "✓ PASS" : audit.verdict === "Warn" ? "⚠ WARN" : audit.verdict === "Reject" ? "✗ REJECT" : "🚨 CRITICAL"}
        </span>
      </div>

      <div className="grid grid-cols-3 gap-3 mb-4">
        <Stat label="Recorded ΣPnL" value={formatUsd(audit.recordedSum)} tone={audit.recordedSum >= 0 ? "text-emerald-300" : "text-rose-300"} />
        <Stat label="Shadow ΣPnL"   value={formatUsd(audit.shadowSum)}   tone={audit.shadowSum   >= 0 ? "text-emerald-300" : "text-rose-300"} />
        <Stat
          label="Execution Gap"
          value={`${audit.totalGap >= 0 ? "+" : ""}${formatUsd(audit.totalGap)}`}
          sub={`${audit.gapPct >= 0 ? "+" : ""}${audit.gapPct.toFixed(1)}%`}
          tone={Math.abs(audit.gapPct) < 5 ? "text-emerald-300" : Math.abs(audit.gapPct) < 15 ? "text-amber-300" : "text-rose-300"}
        />
      </div>

      {/* Worst-gap trades table */}
      {audit.worst.length > 0 && (
        <div>
          <p className="text-[8px] font-mono uppercase tracking-widest text-muted-foreground/45 mb-1.5">
            Top execution-gap trades (potential paper illusion)
          </p>
          <div className="grid grid-cols-[60px_46px_70px_70px_70px_60px] gap-1.5 text-[9px] font-mono uppercase tracking-widest text-muted-foreground/45 border-b border-white/[0.04] pb-1">
            <span>Symbol</span>
            <span>Side</span>
            <span className="text-right">Recorded</span>
            <span className="text-right">Shadow</span>
            <span className="text-right">Gap</span>
            <span className="text-right">% Not</span>
          </div>
          {audit.worst.map((r) => (
            <div key={r.id} className="grid grid-cols-[60px_46px_70px_70px_70px_60px] gap-1.5 text-[10px] font-mono py-0.5 border-b border-white/[0.02]">
              <span className="font-bold truncate">{r.symbol}</span>
              <span className={r.side === "LONG" ? "text-emerald-300" : "text-rose-300"}>{r.side}</span>
              <span className={cn("tabular-nums text-right", r.recorded >= 0 ? "text-emerald-300/80" : "text-rose-300/80")}>{formatUsd(r.recorded)}</span>
              <span className={cn("tabular-nums text-right", r.shadow >= 0 ? "text-emerald-300/80" : "text-rose-300/80")}>{formatUsd(r.shadow)}</span>
              <span className={cn("tabular-nums text-right font-bold", r.gap >= 0 ? "text-amber-300" : "text-cyan-300")}>{r.gap >= 0 ? "+" : ""}{formatUsd(r.gap)}</span>
              <span className="tabular-nums text-right text-muted-foreground/65">{r.gapPctOfNotional >= 0 ? "+" : ""}{r.gapPctOfNotional.toFixed(2)}%</span>
            </div>
          ))}
        </div>
      )}

      <p className="mt-3 pt-3 border-t border-white/[0.04] text-[10px] font-serif italic text-muted-foreground/50 leading-relaxed">
        {audit.verdict === "Pass"
          ? "Shadow PnL ≈ recorded PnL → paper fills are realistic, alpha translates to live."
          : audit.gapPct > 0
            ? `Recorded PnL is over-stating reality by ${audit.gapPct.toFixed(1)}%. Going live, expect ~${formatUsd(Math.abs(audit.totalGap))} less. Tighten size or use post-only / improve fills.`
            : `Recorded PnL is under-stating reality (good for live transition). Could be conservative paper fill model.`}
      </p>
    </div>
  );
}

function Stat({ label, value, sub, tone }: { label: string; value: string; sub?: string; tone: string }) {
  return (
    <div className="text-center">
      <p className="text-[8px] uppercase tracking-widest text-muted-foreground/40 mb-0.5">{label}</p>
      <p className={cn("font-bold tabular-nums text-[14px]", tone)}>{value}</p>
      {sub && <p className="text-[9px] font-mono text-muted-foreground/45 mt-0.5">{sub}</p>}
    </div>
  );
}
