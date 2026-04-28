import { useQuery } from "@tanstack/react-query";
import { promotionApi, type Criterion, type StrategyReadiness } from "@/api/promotion";
import { cn } from "@/lib/utils";
import { formatUsd, pnlColor } from "@/lib/format";
import { SkeletonCard } from "@/components/ui/Skeleton";

export function PromotionRadarCard() {
  const q = useQuery({
    queryKey: ["promotion", "readiness"],
    queryFn: promotionApi.readiness,
    refetchInterval: 30_000,
  });

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5">
      <div className="flex items-baseline justify-between mb-3">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
            Promotion Gate · Paper → Live
          </p>
          <p className="text-sm font-bold">
            Readiness Radar{" "}
            <span className="text-muted-foreground/40 font-light text-xs">
              — formal criteria pass/fail per strategy
            </span>
          </p>
        </div>
        {q.data && (
          <p className="text-[9px] uppercase tracking-widest text-muted-foreground/40 hidden md:block">
            Min trades {q.data.thresholds.minTrades} · Sharpe {q.data.thresholds.minSharpe.toFixed(2)} · PF {q.data.thresholds.minProfitFactor.toFixed(2)} · DD ≤ {q.data.thresholds.maxDrawdownPct}%
          </p>
        )}
      </div>

      {q.isLoading ? (
        <SkeletonCard className="border-0 bg-transparent p-0" />
      ) : !q.data || q.data.strategies.length === 0 ? (
        <p className="text-xs text-muted-foreground/50">
          No closed paper trades yet — readiness scoring activates after first close.
        </p>
      ) : (
        <div className="space-y-3">
          {q.data.strategies.map((s) => (
            <StrategyRow key={s.strategyTag} strategy={s} />
          ))}
        </div>
      )}
    </div>
  );
}

function StrategyRow({ strategy: s }: { strategy: StrategyReadiness }) {
  const tone = s.approved
    ? "border-emerald-500/30 bg-emerald-500/[0.04]"
    : s.readinessScore >= 0.7
    ? "border-amber-500/30 bg-amber-500/[0.03]"
    : "border-rose-500/20 bg-rose-500/[0.02]";

  return (
    <div className={cn("rounded-lg border p-3", tone)}>
      <div className="flex items-center justify-between mb-2">
        <div className="flex items-center gap-2">
          <span className="font-bold text-sm">{s.strategyTag}</span>
          <span
            className={cn(
              "text-[9px] uppercase tracking-widest px-2 py-0.5 rounded font-black",
              s.approved
                ? "bg-emerald-400/20 text-emerald-300"
                : "bg-white/5 text-muted-foreground"
            )}
          >
            {s.approved ? "✓ APPROVED" : "PENDING"}
          </span>
          <span className="text-[10px] text-muted-foreground/60">
            {s.passCount}/{s.totalCount} criteria
          </span>
        </div>
        <div className="flex items-center gap-3 text-[10px]">
          <span className={cn("font-mono font-bold", pnlColor(s.netPnL))}>{formatUsd(s.netPnL)}</span>
          <ScoreBar score={s.readinessScore} />
        </div>
      </div>

      <div className="grid grid-cols-2 md:grid-cols-7 gap-1.5">
        {s.criteria.map((c) => (
          <CriterionPill key={c.name} criterion={c} />
        ))}
      </div>
    </div>
  );
}

function CriterionPill({ criterion: c }: { criterion: Criterion }) {
  const valueText = formatCriterion(c.name, c.value);
  const thresholdText = formatCriterion(c.name, c.threshold);
  return (
    <div
      className={cn(
        "rounded px-2 py-1.5 border text-[10px]",
        c.passed
          ? "border-emerald-500/40 bg-emerald-500/[0.06] text-emerald-400"
          : "border-rose-500/30 bg-rose-500/[0.04] text-rose-400"
      )}
      title={`${c.name}: ${valueText} (threshold ${c.direction === "min" ? "≥" : "≤"} ${thresholdText})`}
    >
      <p className="text-[8px] uppercase tracking-widest opacity-60 mb-0.5">{c.name}</p>
      <p className="font-mono font-bold tabular-nums leading-tight">{valueText}</p>
      <p className="text-[8px] opacity-50 leading-tight">
        {c.direction === "min" ? "≥" : "≤"} {thresholdText}
      </p>
    </div>
  );
}

function ScoreBar({ score }: { score: number }) {
  const pct = Math.max(0, Math.min(100, score * 100));
  const tone =
    score >= 1
      ? "bg-emerald-400"
      : score >= 0.7
      ? "bg-amber-400"
      : "bg-rose-400";
  return (
    <div className="flex items-center gap-1.5">
      <div className="w-20 h-1.5 bg-white/5 rounded-full overflow-hidden">
        <div className={cn("h-full transition-all", tone)} style={{ width: `${pct}%` }} />
      </div>
      <span className="font-mono text-[10px] text-muted-foreground/70 w-7 text-right">
        {pct.toFixed(0)}%
      </span>
    </div>
  );
}

function formatCriterion(name: string, value: number): string {
  if (name.endsWith("%")) return `${value.toFixed(2)}%`;
  if (name === "Trades") return value.toFixed(0);
  return value.toFixed(2);
}
