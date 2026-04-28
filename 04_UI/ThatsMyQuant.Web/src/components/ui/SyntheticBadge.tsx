import { cn } from "@/lib/utils";

export type DataSource = "synthetic" | "derived" | "client-side" | "live" | "deterministic";

const META: Record<DataSource, { label: string; tone: string; tooltip: string }> = {
  synthetic: {
    label: "SYNTH",
    tone: "bg-amber-500/15 text-amber-400 border-amber-500/30",
    tooltip:
      "Data is synthesized for UI demonstration — real exchange feed not yet wired. Do NOT make trade decisions on this number.",
  },
  derived: {
    label: "DERIVED",
    tone: "bg-amber-400/10 text-amber-300 border-amber-400/20",
    tooltip:
      "Computed client-side from existing data — accurate aggregate, but not a fresh upstream measurement.",
  },
  "client-side": {
    label: "CLIENT",
    tone: "bg-accent/10 text-accent border-accent/20",
    tooltip:
      "Measured by your browser, not the server. Reflects your network path, not the system's true performance.",
  },
  deterministic: {
    label: "TEMPLATE",
    tone: "bg-amber-400/10 text-amber-300 border-amber-400/20",
    tooltip:
      "Generated server-side by a deterministic template, not a language model. Identical inputs → identical wording.",
  },
  live: {
    label: "LIVE",
    tone: "bg-emerald-400/15 text-emerald-400 border-emerald-500/30",
    tooltip: "Real upstream data feed — exchange WebSocket / database query.",
  },
};

/**
 * Visible source-of-truth label. Place near any panel that shows synthetic /
 * derived / template data so the operator never accidentally trusts a number
 * that wasn't measured upstream.
 */
export function SyntheticBadge({
  source,
  className,
}: {
  source: DataSource;
  className?: string;
}) {
  const m = META[source];
  return (
    <span
      title={m.tooltip}
      aria-label={m.tooltip}
      className={cn(
        "inline-flex items-center px-1.5 py-0.5 rounded text-[8px] font-black uppercase tracking-widest border cursor-help",
        m.tone,
        className
      )}
    >
      {m.label}
    </span>
  );
}
