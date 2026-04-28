import { cn } from "@/lib/utils";

/**
 * Notion-style colored category pill for strategy / asset class / status.
 *
 * Maps the raw strategy tag (or asset class string) to a stable color so the
 * trade journal reads at a glance — pairs are cyan, funding is pink, manual
 * orders are violet, etc. Falls back to a muted gray for unknown tags so we
 * never crash the table on a new strategy.
 */

interface PillProps {
  tag: string | undefined | null;
  /** Optional override; otherwise we infer from tag. */
  tone?: "cyan" | "pink" | "amber" | "violet" | "green" | "rose" | "gray";
  size?: "xs" | "sm";
  className?: string;
}

const TONE_CLASSES: Record<NonNullable<PillProps["tone"]>, string> = {
  cyan:   "bg-cyan-500/10 text-cyan-300 border-cyan-400/25",
  pink:   "bg-pink-500/10 text-pink-300 border-pink-400/25",
  amber:  "bg-amber-500/10 text-amber-300 border-amber-400/25",
  violet: "bg-violet-500/10 text-violet-300 border-violet-400/25",
  green:  "bg-emerald-500/10 text-emerald-300 border-emerald-400/25",
  rose:   "bg-rose-500/10 text-rose-300 border-rose-400/25",
  gray:   "bg-white/[0.04] text-muted-foreground border-white/10",
};

/** Heuristic mapping — pure string match, case-insensitive substrings. */
function inferTone(tag: string): { tone: NonNullable<PillProps["tone"]>; short: string } {
  const t = tag.toLowerCase();
  if (t.includes("pair"))     return { tone: "cyan",   short: "Pair" };
  if (t.includes("fund"))     return { tone: "pink",   short: "Funding" };
  if (t.includes("manual"))   return { tone: "violet", short: "Manual" };
  if (t.includes("trend"))    return { tone: "green",  short: "Trend" };
  if (t.includes("range"))    return { tone: "amber",  short: "Range" };
  if (t.includes("breakout")) return { tone: "rose",   short: "Breakout" };
  if (t.includes("hft"))      return { tone: "violet", short: "HFT" };
  if (t.includes("untag") || t.includes("unknown"))
                              return { tone: "gray",   short: "Untagged" };
  return { tone: "gray", short: tag.length > 14 ? tag.slice(0, 12) + "…" : tag };
}

export function StrategyPill({ tag, tone, size = "xs", className }: PillProps) {
  if (!tag) {
    return (
      <span className={cn(
        "inline-flex items-center rounded-full border px-2 py-0.5 font-mono uppercase tracking-widest",
        size === "xs" ? "text-[9px]" : "text-[10px]",
        TONE_CLASSES.gray,
        className,
      )}>
        —
      </span>
    );
  }
  const inferred = inferTone(tag);
  const finalTone = tone ?? inferred.tone;
  return (
    <span
      title={tag}
      className={cn(
        "inline-flex items-center rounded-full border px-2 py-0.5 font-mono uppercase tracking-widest",
        size === "xs" ? "text-[9px]" : "text-[10px]",
        TONE_CLASSES[finalTone],
        className,
      )}
    >
      {inferred.short}
    </span>
  );
}

/**
 * Status pill — WIN / LOSS / OPEN / PENDING / FAILED / REJECTED.
 * Decides icon + color from the status string + optional pnl sign for closed trades.
 */
export function StatusPill({
  status,
  pnl,
  size = "xs",
  className,
}: {
  status: string | undefined;
  pnl?: number;
  size?: "xs" | "sm";
  className?: string;
}) {
  const s = (status ?? "").toLowerCase();
  let tone: NonNullable<PillProps["tone"]> = "gray";
  let label = status ?? "—";
  let icon = "○";

  if (s.includes("open")) {
    tone = "cyan"; label = "Open"; icon = "○";
  } else if (s.includes("pend")) {
    tone = "amber"; label = "Pending"; icon = "◷";
  } else if (s.includes("fail")) {
    tone = "rose"; label = "Failed"; icon = "✗";
  } else if (s.includes("reject")) {
    tone = "rose"; label = "Rejected"; icon = "✗";
  } else if (s.includes("closed") || s.includes("execut") || s.includes("filled")) {
    if (pnl !== undefined) {
      if (pnl > 0)      { tone = "green"; label = "Win";  icon = "✓"; }
      else if (pnl < 0) { tone = "rose";  label = "Loss"; icon = "✗"; }
      else              { tone = "gray";  label = "Flat"; icon = "−"; }
    } else {
      tone = "gray"; label = "Closed"; icon = "●";
    }
  }

  return (
    <span
      title={status ?? undefined}
      className={cn(
        "inline-flex items-center gap-1 rounded-full border px-2 py-0.5 font-mono uppercase tracking-widest font-black",
        size === "xs" ? "text-[9px]" : "text-[10px]",
        TONE_CLASSES[tone],
        className,
      )}
    >
      <span aria-hidden>{icon}</span>
      {label}
    </span>
  );
}
