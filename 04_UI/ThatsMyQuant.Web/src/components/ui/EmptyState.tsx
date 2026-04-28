import React from "react";
import { Link } from "react-router-dom";
import { cn } from "@/lib/utils";

interface Props {
  title: string;
  description: string;
  /** Optional CTA — internal route or button. */
  cta?: { label: string; to?: string; onClick?: () => void };
  className?: string;
  icon?: "trades" | "signals" | "positions" | "data" | "alerts";
}

const ICONS: Record<NonNullable<Props["icon"]>, React.ReactNode> = {
  trades: (
    <svg width="32" height="32" viewBox="0 0 32 32" fill="none">
      <path
        d="M4 24L11 16L17 21L28 8"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
      <circle cx="28" cy="8" r="2" stroke="currentColor" strokeWidth="1.4" />
    </svg>
  ),
  signals: (
    <svg width="32" height="32" viewBox="0 0 32 32" fill="none">
      <path d="M4 28V20M12 28V12M20 28V16M28 28V4" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
    </svg>
  ),
  positions: (
    <svg width="32" height="32" viewBox="0 0 32 32" fill="none">
      <rect x="6" y="10" width="20" height="14" rx="2" stroke="currentColor" strokeWidth="1.4" />
      <path d="M6 14h20" stroke="currentColor" strokeWidth="1.4" />
    </svg>
  ),
  data: (
    <svg width="32" height="32" viewBox="0 0 32 32" fill="none">
      <ellipse cx="16" cy="8" rx="10" ry="3" stroke="currentColor" strokeWidth="1.4" />
      <path d="M6 8v16c0 1.66 4.48 3 10 3s10-1.34 10-3V8" stroke="currentColor" strokeWidth="1.4" />
      <path d="M6 16c0 1.66 4.48 3 10 3s10-1.34 10-3" stroke="currentColor" strokeWidth="1.4" />
    </svg>
  ),
  alerts: (
    <svg width="32" height="32" viewBox="0 0 32 32" fill="none">
      <path
        d="M16 4a8 8 0 0 0-8 8v5L5 22h22l-3-5v-5a8 8 0 0 0-8-8zM12 26a4 4 0 0 0 8 0"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinejoin="round"
      />
    </svg>
  ),
};

/**
 * Reusable actionable empty state — never just "no data". Always tells the
 * operator what would make data appear, ideally with a deep-link CTA.
 */
export function EmptyState({ title, description, cta, className, icon = "data" }: Props) {
  const ctaContent = cta ? (
    <span className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-md bg-accent/15 text-accent border border-accent/30 hover:bg-accent/25 transition-colors text-[11px] font-black uppercase tracking-widest">
      {cta.label}
      <svg width="10" height="10" viewBox="0 0 16 16" fill="none">
        <path d="M3 8h10M9 4l4 4-4 4" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
      </svg>
    </span>
  ) : null;

  return (
    <div
      className={cn(
        "flex flex-col items-center justify-center text-center px-6 py-10 gap-3",
        className
      )}
    >
      <div className="text-muted-foreground/30">{ICONS[icon]}</div>
      <p className="font-serif text-[16px] tracking-tight text-foreground/80">{title}</p>
      <p className="font-serif italic text-[12px] text-muted-foreground/70 max-w-sm leading-relaxed">
        {description}
      </p>
      {cta &&
        (cta.to ? (
          <Link to={cta.to} className="mt-1">
            {ctaContent}
          </Link>
        ) : (
          <button onClick={cta.onClick} className="mt-1">
            {ctaContent}
          </button>
        ))}
    </div>
  );
}
