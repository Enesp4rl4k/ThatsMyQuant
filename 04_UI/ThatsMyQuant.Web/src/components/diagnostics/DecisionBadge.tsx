import { cn } from "@/lib/utils";
import type { SignalDecision } from "@/api/signalDiagnostics";

/**
 * Color-coded pill for the per-row decision verdict. Color taxonomy:
 *   - emerald: Fired / WouldFire — actually firing
 *   - amber:   NearTrigger        — heads-up, ≥80% to trigger
 *   - sky:     HoldOpen           — managing existing position
 *   - slate:   BelowThreshold     — benign quiet, no signal
 *   - violet:  SkippedByRunner    — runner-internal gate (velocity/regime/spread)
 *   - rose:    Flat               — Quantlab explicitly said zero weight
 *   - red:     Suppressed*        — downstream gate killed an otherwise-fireable signal
 *
 * The Suppressed family shares the same red palette so the operator can spot
 * "we wanted to fire but couldn't" at a glance regardless of which gate.
 */
const DECISION_STYLES: Record<SignalDecision, { label: string; className: string }> = {
  Fired:                            { label: "Fired",         className: "bg-emerald-500/15 text-emerald-300 ring-emerald-500/30" },
  WouldFire:                        { label: "Would Fire",    className: "bg-emerald-500/15 text-emerald-300 ring-emerald-500/30" },
  NearTrigger:                      { label: "Near Trigger",  className: "bg-amber-500/15 text-amber-300 ring-amber-500/30" },
  HoldOpen:                         { label: "Hold Open",     className: "bg-sky-500/15 text-sky-300 ring-sky-500/30" },
  BelowThreshold:                   { label: "Below",         className: "bg-slate-500/10 text-slate-400 ring-slate-500/20" },
  SkippedByRunner:                  { label: "Skipped",       className: "bg-violet-500/15 text-violet-300 ring-violet-500/30" },
  Flat:                             { label: "Flat",          className: "bg-rose-500/10 text-rose-300 ring-rose-500/30" },
  SuppressedByCircuitBreakerTier:   { label: "🛑 Tier CB",    className: "bg-red-500/20 text-red-300 ring-red-500/40" },
  SuppressedByCircuitBreakerGlobal: { label: "🛑 Global CB",  className: "bg-red-500/20 text-red-300 ring-red-500/40" },
  SuppressedByMacroPause:           { label: "⏸ Macro",       className: "bg-red-500/20 text-red-300 ring-red-500/40" },
  SuppressedByOrphanHalt:           { label: "🚧 Orphan",     className: "bg-red-500/20 text-red-300 ring-red-500/40" },
  SuppressedByBlacklist:            { label: "🚫 Blacklist",  className: "bg-red-500/20 text-red-300 ring-red-500/40" },
  SuppressedByPortfolioRisk:        { label: "⚠ Risk Block",  className: "bg-red-500/20 text-red-300 ring-red-500/40" },
  SuppressedByKillSwitch:           { label: "💀 Kill",       className: "bg-red-500/20 text-red-300 ring-red-500/40" },
};

export function DecisionBadge({ decision }: { decision: SignalDecision }) {
  const style = DECISION_STYLES[decision] ?? {
    label: decision,
    className: "bg-slate-500/10 text-slate-400 ring-slate-500/20",
  };

  return (
    <span
      className={cn(
        "inline-flex items-center px-2 py-0.5 rounded text-[10px] font-bold uppercase tracking-wider ring-1 ring-inset whitespace-nowrap",
        style.className,
      )}
    >
      {style.label}
    </span>
  );
}

/**
 * Expose the decision → group classifier for filter UIs. "suppressed" includes
 * both Suppressed* (downstream gates) and Flat (Quantlab said no).
 */
export function decisionGroup(d: SignalDecision): "active" | "near" | "below" | "skipped" | "suppressed" {
  if (d === "Fired" || d === "WouldFire" || d === "HoldOpen") return "active";
  if (d === "NearTrigger") return "near";
  if (d === "BelowThreshold") return "below";
  if (d === "SkippedByRunner") return "skipped";
  return "suppressed";
}
