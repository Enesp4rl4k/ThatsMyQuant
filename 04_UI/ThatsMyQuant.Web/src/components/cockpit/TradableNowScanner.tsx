import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { pairsApi } from "@/api/pairs";
import { fundingApi } from "@/api/funding";
import { cn } from "@/lib/utils";

interface Props {
  /** Optional callback when an opportunity row is clicked. */
  onSelect?: (symbol: string) => void;
}

/**
 * Universe-wide "what's tradeable RIGHT NOW" scanner. Composes data from:
 *   • /api/pairs/active        → cointegration state per active pair
 *   • /api/funding/rates       → funding bps per watchlist symbol
 *
 * Categorises every row into:
 *   ✅ FIRE     — over threshold, bot would enter on next tick
 *   ⚠ NEAR     — 80–100% of threshold, watch list
 *   👀 WATCH    — 50–80% of threshold, on the radar
 *   💤 IDLE     — <50% of threshold, hidden by default
 *
 * Each row has a one-line "missing condition" — the human-readable reason
 * the bot ISN'T pulling the trigger yet (e.g. "needs +0.45σ more z-score" or
 * "rate must reach 1.0 bp, currently 0.42 bp").
 */
export function TradableNowScanner({ onSelect }: Props) {
  const pairsQ   = useQuery({ queryKey: ["scan", "pairs"],   queryFn: pairsApi.active,         refetchInterval: 5_000 });
  const fundingQ = useQuery({ queryKey: ["scan", "funding"], queryFn: () => fundingApi.rates(), refetchInterval: 10_000 });

  const entryZ = pairsQ.data?.entryZ ?? 2.0;
  const fundingThr = 1.0; // bps

  const rows = useMemo(() => {
    const out: ScanRow[] = [];
    for (const p of (pairsQ.data?.pairs ?? [])) {
      const cur = Math.abs(p.currentZ);
      const need = Math.max(0, entryZ - cur);
      const tier =
        cur >= entryZ        ? "fire"  :
        cur >= entryZ * 0.8  ? "near"  :
        cur >= entryZ * 0.5  ? "watch" : "idle";
      out.push({
        kind: "pair",
        symbol: p.symbolY,
        secondary: p.symbolX,
        currentText: `|z|=${cur.toFixed(2)}`,
        tier,
        missing: tier === "fire"
          ? `Z crossed ${entryZ}σ — direction: ${p.currentZ >= 0 ? "spread HIGH (short Y/long X)" : "spread LOW (long Y/short X)"}`
          : `needs +${need.toFixed(2)}σ more (z at ${cur.toFixed(2)}, target ${entryZ.toFixed(1)})`,
        sortKey: cur / entryZ,
      });
    }
    for (const r of (fundingQ.data ?? [])) {
      const cur = Math.abs(r.rateBps);
      const need = Math.max(0, fundingThr - cur);
      const tier =
        cur >= fundingThr        ? "fire"  :
        cur >= fundingThr * 0.8  ? "near"  :
        cur >= fundingThr * 0.5  ? "watch" : "idle";
      out.push({
        kind: "funding",
        symbol: r.symbol,
        currentText: `${r.rateBps >= 0 ? "+" : ""}${r.rateBps.toFixed(2)} bp`,
        tier,
        missing: tier === "fire"
          ? `Rate crossed ${fundingThr.toFixed(1)} bp — fade ${r.rateBps >= 0 ? "SHORT (collect from longs)" : "LONG (collect from shorts)"}`
          : `needs +${need.toFixed(2)} bp more (at ${cur.toFixed(2)}, target ${fundingThr.toFixed(1)})`,
        sortKey: cur / fundingThr,
      });
    }
    return out.sort((a, b) => b.sortKey - a.sortKey);
  }, [pairsQ.data, fundingQ.data, entryZ, fundingThr]);

  const fire  = rows.filter((r) => r.tier === "fire");
  const near  = rows.filter((r) => r.tier === "near");
  const watch = rows.filter((r) => r.tier === "watch");
  const idle  = rows.filter((r) => r.tier === "idle");

  return (
    <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
      <div className="flex items-baseline justify-between mb-3">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
          🔍 Tradable Now
        </p>
        <div className="flex items-center gap-1.5 text-[9px] font-mono">
          <CountPill emoji="🔥"  count={fire.length}  tone="emerald" label="ready" />
          <CountPill emoji="⚡" count={near.length}  tone="amber"   label="near" />
          <CountPill emoji="👀" count={watch.length} tone="cyan"    label="watch" />
          <CountPill emoji="💤" count={idle.length}  tone="gray"    label="idle" />
        </div>
      </div>

      {fire.length === 0 && near.length === 0 && watch.length === 0 && (
        <p className="text-[11px] italic font-serif text-muted-foreground/55 text-center py-3">
          — universe quiet · {idle.length} symbols all idle —
        </p>
      )}

      {fire.length > 0 && (
        <div className="space-y-1 mb-2">
          {fire.map((r) => <ScanRowView key={r.kind+r.symbol} row={r} onSelect={onSelect} />)}
        </div>
      )}
      {near.length > 0 && (
        <div className="space-y-1 mb-2">
          {near.map((r) => <ScanRowView key={r.kind+r.symbol} row={r} onSelect={onSelect} />)}
        </div>
      )}
      {watch.length > 0 && (
        <div className="space-y-1 mb-2">
          {watch.map((r) => <ScanRowView key={r.kind+r.symbol} row={r} onSelect={onSelect} />)}
        </div>
      )}
    </div>
  );
}

interface ScanRow {
  kind: "pair" | "funding";
  symbol: string;
  secondary?: string;
  currentText: string;
  tier: "fire" | "near" | "watch" | "idle";
  missing: string;
  sortKey: number;
}

function ScanRowView({ row, onSelect }: { row: ScanRow; onSelect?: (s: string) => void }) {
  const tierTone =
    row.tier === "fire"  ? "border-emerald-400/40 bg-emerald-500/10 text-emerald-200" :
    row.tier === "near"  ? "border-amber-400/30  bg-amber-500/10  text-amber-200"     :
    row.tier === "watch" ? "border-cyan-400/25   bg-cyan-500/[0.06] text-cyan-200"    :
                           "border-white/5       bg-white/[0.02]  text-muted-foreground/65";
  const tierEmoji =
    row.tier === "fire"  ? "🔥" :
    row.tier === "near"  ? "⚡" :
    row.tier === "watch" ? "👀" : "💤";

  return (
    <button
      onClick={() => onSelect?.(row.symbol)}
      className={cn(
        "w-full grid grid-cols-[20px_140px_64px_1fr] items-center gap-2 px-2.5 py-1.5 rounded-md border text-[10px] font-mono text-left transition-colors hover:brightness-125",
        tierTone,
        row.tier === "fire" && "animate-pulse"
      )}
      title={`Click to inspect ${row.symbol}`}
    >
      <span>{tierEmoji}</span>
      <span className="font-bold">
        {row.symbol}
        {row.secondary && <span className="opacity-50 mx-0.5">/</span>}
        {row.secondary && <span className="font-bold">{row.secondary}</span>}
        <span className="ml-1.5 text-[8px] uppercase tracking-widest opacity-60">
          {row.kind === "pair" ? "PAIR" : "FUND"}
        </span>
      </span>
      <span className="tabular-nums text-right opacity-90">{row.currentText}</span>
      <span className="text-right opacity-70 truncate" title={row.missing}>{row.missing}</span>
    </button>
  );
}

function CountPill({ emoji, count, tone, label }: { emoji: string; count: number; tone: "emerald" | "amber" | "cyan" | "gray"; label: string }) {
  const cls =
    tone === "emerald" ? "border-emerald-400/30 bg-emerald-500/10 text-emerald-300" :
    tone === "amber"   ? "border-amber-400/30   bg-amber-500/10   text-amber-300" :
    tone === "cyan"    ? "border-cyan-400/25    bg-cyan-500/10    text-cyan-300" :
                         "border-white/10       bg-white/[0.03]   text-muted-foreground/55";
  return (
    <span className={cn("inline-flex items-center gap-1 px-1.5 py-0.5 rounded-full border uppercase tracking-widest font-black", cls)}>
      <span aria-hidden>{emoji}</span>
      {count} {label}
    </span>
  );
}
