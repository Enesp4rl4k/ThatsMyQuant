import { cn } from "@/lib/utils";

export type RingTone = "cyan" | "pink" | "green" | "amber" | "rose" | "gray" | "violet";
export type RingSize = "sm" | "md" | "lg";

interface Props {
  /** 0–100 fill ratio for the ring; clamps automatically. */
  value: number;
  /** Centre text — "56%", "$58", "1.83", "12 / 50", etc. Provide explicitly so callers control formatting. */
  display: string;
  /** Bottom caption (under the ring). */
  label: string;
  /** Optional secondary line under the label (e.g. "WLFI/USDT"). */
  sub?: string;
  /** Color theme — defaults to gray when value is 0/undefined. */
  tone?: RingTone;
  size?: RingSize;
  /** When true, renders the disabled "No data" look (gray, dashed centre). */
  empty?: boolean;
  /** Optional click handler — adds hover affordance. */
  onClick?: () => void;
  className?: string;
  /** Optional tooltip / aria-label override. */
  title?: string;
}

const TONES: Record<RingTone, { stroke: string; glow: string; bg: string; text: string }> = {
  cyan:   { stroke: "#0EA5E9", glow: "rgba(14,165,233,0.35)",  bg: "rgba(14,165,233,0.08)",  text: "#0EA5E9" },
  pink:   { stroke: "#E879F9", glow: "rgba(232,121,249,0.35)", bg: "rgba(232,121,249,0.08)", text: "#E879F9" },
  green:  { stroke: "#37A95B", glow: "rgba(55,169,91,0.35)",   bg: "rgba(55,169,91,0.08)",   text: "#37A95B" },
  amber:  { stroke: "#F59E0B", glow: "rgba(245,158,11,0.35)",  bg: "rgba(245,158,11,0.08)",  text: "#F59E0B" },
  rose:   { stroke: "#F05A5A", glow: "rgba(240,90,90,0.35)",   bg: "rgba(240,90,90,0.08)",   text: "#F05A5A" },
  violet: { stroke: "#A78BFA", glow: "rgba(167,139,250,0.35)", bg: "rgba(167,139,250,0.08)", text: "#A78BFA" },
  gray:   { stroke: "#52525B", glow: "rgba(82,82,91,0.20)",    bg: "rgba(82,82,91,0.06)",    text: "#A1A1AA" },
};

const SIZES: Record<RingSize, { d: number; sw: number; fs: number; sub: number; gap: number }> = {
  sm: { d: 60,  sw: 4, fs: 12, sub: 9,  gap: 4 },
  md: { d: 86,  sw: 5, fs: 16, sub: 10, gap: 6 },
  lg: { d: 116, sw: 6, fs: 22, sub: 11, gap: 8 },
};

/**
 * Notion-template-style donut KPI tile.
 *
 *   ╭───╮
 *   │56%│   <- display (any string)
 *   ╰───╯
 *    PnL    <- label
 *  WLFI/USDT <- sub (optional)
 *
 * The outer ring is a faint background track; the inner arc fills clockwise to
 * `value`% with the chosen tone color. When `empty` is true (or value is 0 with
 * tone gray), the centre shows the display text in a muted color and the ring
 * track is drawn as a dashed grey circle.
 *
 * Pure SVG — no animations beyond a CSS `transition` on the stroke-dashoffset
 * so the ring smoothly fills when the value changes.
 */
export function RingStat({
  value,
  display,
  label,
  sub,
  tone = "cyan",
  size = "md",
  empty = false,
  onClick,
  className,
  title,
}: Props) {
  const isEmpty = empty || tone === "gray";
  const t = TONES[isEmpty ? "gray" : tone];
  const s = SIZES[size];

  const v = Math.max(0, Math.min(100, value));
  const r = (s.d - s.sw) / 2;
  const c = s.d / 2;
  const circumference = 2 * Math.PI * r;
  const dash = (v / 100) * circumference;

  return (
    <div
      className={cn(
        "flex flex-col items-center select-none",
        onClick && "cursor-pointer hover:scale-[1.02] transition-transform",
        className
      )}
      onClick={onClick}
      title={title ?? `${label}: ${display}`}
      role={onClick ? "button" : undefined}
    >
      <svg width={s.d} height={s.d} viewBox={`0 0 ${s.d} ${s.d}`} className="overflow-visible">
        {/* Soft inner glow (only when not empty) */}
        {!isEmpty && (
          <circle
            cx={c}
            cy={c}
            r={r}
            fill="none"
            stroke={t.glow}
            strokeWidth={s.sw + 4}
            opacity={0.35}
          />
        )}
        {/* Track */}
        <circle
          cx={c}
          cy={c}
          r={r}
          fill="none"
          stroke={isEmpty ? "rgba(255,255,255,0.06)" : "rgba(255,255,255,0.07)"}
          strokeWidth={s.sw}
          strokeDasharray={isEmpty ? "3 3" : undefined}
        />
        {/* Filled arc — start at 12 o'clock, fill clockwise */}
        {!isEmpty && (
          <circle
            cx={c}
            cy={c}
            r={r}
            fill="none"
            stroke={t.stroke}
            strokeWidth={s.sw}
            strokeLinecap="round"
            strokeDasharray={`${dash.toFixed(2)} ${(circumference - dash).toFixed(2)}`}
            strokeDashoffset={0}
            transform={`rotate(-90 ${c} ${c})`}
            style={{ transition: "stroke-dasharray 600ms ease-out" }}
          />
        )}
        {/* Centre text */}
        <text
          x={c}
          y={c}
          dy="0.35em"
          textAnchor="middle"
          fontSize={s.fs}
          fontFamily="ui-monospace, SFMono-Regular, monospace"
          fontWeight={700}
          fill={isEmpty ? "rgba(255,255,255,0.35)" : "#FFFFFF"}
        >
          {display}
        </text>
      </svg>
      <div className="mt-1.5 text-center" style={{ marginTop: s.gap }}>
        <p
          className="font-mono uppercase tracking-widest leading-none"
          style={{ fontSize: s.sub, color: t.text, opacity: isEmpty ? 0.5 : 0.85 }}
        >
          {label}
        </p>
        {sub && (
          <p
            className="font-mono leading-none mt-0.5 text-muted-foreground/50"
            style={{ fontSize: Math.max(8, s.sub - 2) }}
          >
            {sub}
          </p>
        )}
      </div>
    </div>
  );
}

/**
 * Convenience helper — a horizontal row container that lays out RingStats
 * with the consistent gap and centring used across the app.
 */
export function RingStatRow({ children, className }: { children: React.ReactNode; className?: string }) {
  return (
    <div className={cn("flex items-start justify-around gap-3 flex-wrap", className)}>
      {children}
    </div>
  );
}
