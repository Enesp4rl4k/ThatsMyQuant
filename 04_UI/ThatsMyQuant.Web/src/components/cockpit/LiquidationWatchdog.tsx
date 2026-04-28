import { useEffect, useMemo, useRef } from "react";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";
import { pushNotification } from "@/lib/notifications";
import type { Position } from "@/api/types";

interface Props {
  positions: Position[];
  /** Maintenance margin rate the exchange uses (paper default 0.5%). */
  maintenanceMarginRate?: number;
}

const FIRED_KEY = "tmq.liqAlerts.fired";

/**
 * Approximates each leveraged position's liquidation price and renders a
 * "distance to liquidation" gauge.  When ANY position crosses the configured
 * alert threshold (default <30% buffer), a warning toast fires once per
 * position-day so the operator sees it on a phone notification.
 *
 * Formula (isolated margin, ignoring funding for simplicity):
 *   For a LONG:   liq ≈ entry × (1 - 1/leverage + maintRate)
 *   For a SHORT:  liq ≈ entry × (1 + 1/leverage - maintRate)
 * "% buffer" = absolute distance from current mark to liq, normalised by entry.
 */
export function LiquidationWatchdog({ positions, maintenanceMarginRate = 0.005 }: Props) {
  const rows = useMemo(() => {
    return positions
      .filter((p) => p.leverage > 1) // unleveraged positions can't be liquidated
      .map((p) => {
        const entry = p.entryPrice;
        const mark  = p.markPrice > 0 ? p.markPrice : entry;
        const lev   = p.leverage;
        const isLong = p.side === "LONG";

        // Approximate liquidation price (isolated margin)
        const liq = isLong
          ? entry * (1 - 1 / lev + maintenanceMarginRate)
          : entry * (1 + 1 / lev - maintenanceMarginRate);

        // Distance from current mark to liq, expressed as % of entry
        const distAbs = Math.abs(mark - liq);
        const bufferPct = entry > 0 ? (distAbs / entry) * 100 : 0;

        // Initial budget = total adverse-move tolerance from entry to liq
        const totalBudgetPct = entry > 0 ? (Math.abs(entry - liq) / entry) * 100 : 0;
        // How much of that budget have we consumed?
        const consumedPct = totalBudgetPct > 0 ? Math.max(0, Math.min(100, ((totalBudgetPct - bufferPct) / totalBudgetPct) * 100)) : 0;

        // Tier
        const tier: "critical" | "warning" | "watch" | "safe" =
          bufferPct < totalBudgetPct * 0.1  ? "critical" :
          bufferPct < totalBudgetPct * 0.3  ? "warning"  :
          bufferPct < totalBudgetPct * 0.6  ? "watch"    :
                                              "safe";

        return { p, liq, bufferPct, totalBudgetPct, consumedPct, tier };
      })
      .sort((a, b) => a.bufferPct - b.bufferPct);
  }, [positions, maintenanceMarginRate]);

  // Fire alerts (once per position per day)
  const firedRef = useRef<Set<string>>(new Set());
  useEffect(() => {
    try {
      const stored = localStorage.getItem(FIRED_KEY);
      if (stored) firedRef.current = new Set(JSON.parse(stored));
    } catch { /* ignore */ }
  }, []);

  useEffect(() => {
    const today = new Date().toISOString().slice(0, 10);
    let dirty = false;
    for (const r of rows) {
      if (r.tier === "critical" || r.tier === "warning") {
        const key = `${r.p.id}-${today}-${r.tier}`;
        if (!firedRef.current.has(key)) {
          firedRef.current.add(key);
          dirty = true;
          pushNotification({
            type: r.tier === "critical" ? "error" : "warning",
            title: r.tier === "critical"
              ? `🚨 LIQUIDATION IMMINENT · ${r.p.symbol}`
              : `⚠ Liq buffer low · ${r.p.symbol}`,
            body: `Mark ${formatUsd(r.p.markPrice)} → liq ${formatUsd(r.liq)} (buffer ${r.bufferPct.toFixed(2)}%). ${r.consumedPct.toFixed(0)}% of margin consumed.`,
          });
        }
      }
    }
    if (dirty) {
      try {
        localStorage.setItem(FIRED_KEY, JSON.stringify(Array.from(firedRef.current)));
      } catch { /* ignore */ }
    }
  }, [rows]);

  const leveragedCount = rows.length;
  const dangerCount = rows.filter((r) => r.tier === "critical" || r.tier === "warning").length;

  if (leveragedCount === 0) {
    return (
      <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-2">
          🛡 Liquidation Watchdog
        </p>
        <p className="text-[11px] italic font-serif text-muted-foreground/55 text-center py-3">
          — no leveraged open positions —
        </p>
      </div>
    );
  }

  return (
    <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
      <div className="flex items-baseline justify-between mb-3">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
            🛡 Liquidation Watchdog
          </p>
          <p className="text-[10px] font-mono text-muted-foreground/45 mt-0.5">
            {leveragedCount} leveraged · maint {(maintenanceMarginRate * 100).toFixed(2)}%
          </p>
        </div>
        {dangerCount > 0 && (
          <span className="px-2 py-0.5 rounded-full border border-rose-400/30 bg-rose-500/15 text-rose-300 text-[9px] font-mono uppercase tracking-widest font-black">
            {dangerCount} at risk
          </span>
        )}
      </div>

      <div className="space-y-2">
        {rows.map((r) => (
          <LiqRow key={r.p.id} row={r} />
        ))}
      </div>
    </div>
  );
}

function LiqRow({
  row,
}: {
  row: {
    p: Position;
    liq: number;
    bufferPct: number;
    consumedPct: number;
    tier: "critical" | "warning" | "watch" | "safe";
  };
}) {
  const tierColor =
    row.tier === "critical" ? "#F05A5A" :
    row.tier === "warning"  ? "#F59E0B" :
    row.tier === "watch"    ? "#0EA5E9" :
                              "#22C55E";

  return (
    <div className="rounded-lg border border-white/[0.05] bg-white/[0.02] p-2.5">
      <div className="flex items-center justify-between mb-1.5">
        <div className="flex items-center gap-2 text-[10px]">
          <span className={cn(
            "px-1.5 py-0.5 rounded border font-black uppercase tracking-widest text-[9px]",
            row.p.side === "LONG"
              ? "text-emerald-300 border-emerald-400/30 bg-emerald-500/10"
              : "text-rose-300 border-rose-400/30 bg-rose-500/10"
          )}>
            {row.p.side === "LONG" ? "↑" : "↓"} {row.p.symbol}
          </span>
          <span className="text-muted-foreground/65 font-mono">{row.p.leverage}× lev</span>
        </div>
        <span
          className={cn(
            "px-1.5 py-0.5 rounded text-[8px] font-mono uppercase tracking-widest font-black",
            row.tier === "critical" && "bg-rose-500/25 text-rose-100 animate-pulse",
            row.tier === "warning"  && "bg-amber-500/20 text-amber-200",
            row.tier === "watch"    && "bg-cyan-500/15 text-cyan-200",
            row.tier === "safe"     && "bg-emerald-500/10 text-emerald-200",
          )}
        >
          {row.tier === "critical" ? "🚨 critical" :
           row.tier === "warning"  ? "⚠ warning"  :
           row.tier === "watch"    ? "👀 watch"    : "✓ safe"}
        </span>
      </div>

      {/* Consumed-margin bar */}
      <div className="relative h-2 rounded-full bg-white/[0.05] overflow-hidden mb-1">
        <div
          className="absolute inset-y-0 left-0 rounded-full transition-all duration-700"
          style={{ width: `${row.consumedPct}%`, backgroundColor: tierColor, opacity: 0.85 }}
        />
        {/* 60%, 80% threshold ticks */}
        <div className="absolute top-0 bottom-0 w-px bg-white/15" style={{ left: "60%" }} />
        <div className="absolute top-0 bottom-0 w-px bg-white/25" style={{ left: "80%" }} />
      </div>

      <div className="grid grid-cols-3 gap-2 text-[9px] font-mono">
        <Stat label="Entry" value={formatUsd(row.p.entryPrice)} />
        <Stat label="Mark"  value={formatUsd(row.p.markPrice)} />
        <Stat label="Liq"   value={formatUsd(row.liq)} tone={tierColor} />
      </div>
      <div className="flex justify-between text-[8px] font-mono text-muted-foreground/45 mt-1">
        <span>{row.consumedPct.toFixed(0)}% consumed</span>
        <span>buffer {row.bufferPct.toFixed(2)}%</span>
      </div>
    </div>
  );
}

function Stat({ label, value, tone }: { label: string; value: string; tone?: string }) {
  return (
    <div>
      <p className="text-[8px] uppercase tracking-widest text-muted-foreground/40">{label}</p>
      <p className="font-bold tabular-nums" style={{ color: tone ?? "rgba(255,255,255,0.85)" }}>
        {value}
      </p>
    </div>
  );
}
