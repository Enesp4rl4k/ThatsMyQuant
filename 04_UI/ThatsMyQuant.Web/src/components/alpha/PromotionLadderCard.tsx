import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { promotionApi, type Criterion, type StrategyReadiness } from "@/api/promotion";
import { cn } from "@/lib/utils";

/**
 * Detailed promotion-readiness ladder. For every paper-trading strategy,
 * shows ALL criteria (trades, Sharpe, PF, drawdown, win-rate, avg RR,
 * cost-leakage) as horizontal progress bars with the actual value vs the
 * threshold. Failed criteria glow red so the operator can see at a glance
 * which strategies are LIVE-ELIGIBLE and which still need samples / better
 * stats.
 *
 * Backend endpoint /api/promotion/readiness already returns this — we just
 * present it richly. Pairs with the existing PromotionRadarCard (which is a
 * spider chart, this is the table). Both can live on Alpha Portfolio.
 */
export function PromotionLadderCard() {
  const q = useQuery({
    queryKey: ["promotion-ladder"],
    queryFn:  promotionApi.readiness,
    refetchInterval: 30_000,
  });

  const data = q.data;

  const sorted = useMemo(() => {
    if (!data) return [];
    return [...data.strategies].sort((a, b) => b.readinessScore - a.readinessScore);
  }, [data]);

  if (q.isLoading) {
    return <Card title="📈 Promotion Ladder"><Empty msg="Loading readiness…" /></Card>;
  }
  if (!data || sorted.length === 0) {
    return <Card title="📈 Promotion Ladder"><Empty msg="No strategies have closed-trade history yet." /></Card>;
  }

  const eligibleCount = sorted.filter((s) => s.approved).length;

  return (
    <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
      <div className="flex items-baseline justify-between mb-4">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
            📈 Promotion Ladder
          </p>
          <p className="text-[10px] font-mono text-muted-foreground/45 mt-0.5">
            Paper → Live eligibility · {sorted.length} strategies tracked
          </p>
        </div>
        <span className={cn(
          "px-2.5 py-1 rounded-full text-[9px] font-mono font-black uppercase tracking-widest border",
          eligibleCount > 0
            ? "border-emerald-400/40 bg-emerald-500/15 text-emerald-200"
            : "border-white/10 bg-white/[0.03] text-muted-foreground/55"
        )}>
          {eligibleCount} eligible / {sorted.length}
        </span>
      </div>

      <div className="space-y-3">
        {sorted.map((s) => <StrategyRow key={s.strategyTag} s={s} />)}
      </div>
    </div>
  );
}

function StrategyRow({ s }: { s: StrategyReadiness }) {
  return (
    <div className={cn(
      "rounded-xl border p-3",
      s.approved
        ? "border-emerald-400/25 bg-emerald-500/[0.04]"
        : "border-white/[0.05] bg-white/[0.015]"
    )}>
      <div className="flex items-baseline justify-between mb-2">
        <div className="flex items-center gap-2">
          <p className="font-bold text-[12px]">{s.strategyTag}</p>
          <span className="text-[9px] font-mono uppercase tracking-widest text-muted-foreground/55">
            {s.stage}
          </span>
        </div>
        <div className="flex items-center gap-2">
          <span className="text-[10px] font-mono text-muted-foreground/65">
            {s.passCount}/{s.totalCount} criteria
          </span>
          <span className={cn(
            "px-2 py-0.5 rounded text-[9px] font-mono font-black uppercase tracking-widest border",
            s.approved
              ? "border-emerald-400/40 bg-emerald-500/20 text-emerald-200"
              : "border-amber-400/30 bg-amber-500/10 text-amber-200"
          )}>
            {s.approved ? "✓ ELIGIBLE" : `${(s.readinessScore * 100).toFixed(0)}% ready`}
          </span>
        </div>
      </div>

      {/* Master readiness bar */}
      <div className="relative h-1.5 rounded-full bg-white/[0.05] overflow-hidden mb-3">
        <div
          className={cn(
            "absolute inset-y-0 left-0 rounded-full transition-all duration-700",
            s.approved ? "bg-emerald-400" : s.readinessScore >= 0.6 ? "bg-amber-400" : "bg-rose-400/70"
          )}
          style={{ width: `${(s.readinessScore * 100).toFixed(1)}%` }}
        />
      </div>

      {/* Per-criterion */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-x-4 gap-y-1.5">
        {s.criteria.map((c) => <CriterionRow key={c.name} c={c} />)}
      </div>
    </div>
  );
}

function CriterionRow({ c }: { c: Criterion }) {
  // Distance to threshold expressed as a 0..1 fill
  const ratio = c.direction === "min"
    ? (c.threshold > 0 ? Math.min(1, c.value / c.threshold) : (c.passed ? 1 : 0))
    : (c.threshold > 0 ? Math.max(0, 1 - Math.max(0, c.value - c.threshold) / c.threshold) : (c.passed ? 1 : 0));

  return (
    <div className="grid grid-cols-[80px_1fr_60px] items-center gap-2 text-[10px] font-mono">
      <span className="text-muted-foreground/65 truncate" title={c.name}>{c.name}</span>
      <div className="relative h-1.5 rounded-full bg-white/[0.05] overflow-hidden">
        <div
          className={cn(
            "absolute inset-y-0 left-0 rounded-full",
            c.passed ? "bg-emerald-400/80" : "bg-amber-400/60"
          )}
          style={{ width: `${(ratio * 100).toFixed(1)}%` }}
        />
      </div>
      <span className={cn(
        "tabular-nums text-right",
        c.passed ? "text-emerald-300" : "text-amber-300"
      )}>
        {fmt(c.value)} / {fmt(c.threshold)}
      </span>
    </div>
  );
}

function fmt(v: number): string {
  if (Number.isInteger(v)) return v.toString();
  if (Math.abs(v) < 0.01)  return v.toFixed(4);
  if (Math.abs(v) < 1)     return v.toFixed(3);
  return v.toFixed(2);
}

function Card({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
      <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-2">{title}</p>
      {children}
    </div>
  );
}

function Empty({ msg }: { msg: string }) {
  return <p className="text-[11px] italic font-serif text-muted-foreground/55 text-center py-3">— {msg} —</p>;
}
