import { cn } from "@/lib/utils";

/**
 * Horizontal progress bar showing distance-to-trigger %. The 100% mark is
 * rendered as a vertical "trigger line" — anything past it overflows in
 * emerald (we crossed the threshold). Capped visually at 150% so a runaway
 * z-score doesn't blow out the row.
 *
 * Color buckets match {@link DecisionBadge}:
 *   - >= 100%: emerald (past trigger)
 *   - 80-99%:  amber   (near, heads-up)
 *   - 50-79%:  sky     (closing in)
 *   - <  50%:  slate   (quiet)
 */
export function DistanceBar({ pct }: { pct: number }) {
  const clamped = Math.min(Math.max(pct, 0), 150);
  const widthPct = (clamped / 150) * 100;
  const triggerLinePct = (100 / 150) * 100; // 66.67%

  const colorClass =
    clamped >= 100
      ? "bg-emerald-500/70"
      : clamped >= 80
        ? "bg-amber-500/70"
        : clamped >= 50
          ? "bg-sky-500/60"
          : "bg-slate-500/40";

  return (
    <div className="relative w-32 h-2 bg-white/[0.04] rounded-sm overflow-hidden">
      {/* Filled portion */}
      <div
        className={cn("h-full transition-[width] duration-300", colorClass)}
        style={{ width: `${widthPct}%` }}
      />
      {/* Trigger threshold marker */}
      <div
        className="absolute top-0 bottom-0 w-px bg-white/40"
        style={{ left: `${triggerLinePct}%` }}
        aria-hidden
      />
    </div>
  );
}
