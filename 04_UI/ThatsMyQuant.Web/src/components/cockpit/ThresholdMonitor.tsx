import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { pairsApi } from "@/api/pairs";
import { fundingApi } from "@/api/funding";
import { cn } from "@/lib/utils";

/**
 * "How close is the bot to firing a trade?" — live progress bars per strategy.
 *
 * Current paper config (see memory.strategy_research_archive):
 *   • Pairs entry  : |z| ≥ 2.0
 *   • Pairs stop   : |z| ≥ 3.0
 *   • Funding entry: |rate| ≥ 1 bp
 *
 * Each row shows current value vs the threshold as a gauge bar with a
 * coloured fill (gray < 50% there · cyan ≥ 50% · amber ≥ 80% · green = ready).
 * "Ready" rows pulse subtly so the operator can spot tradeable conditions
 * without scanning numbers.
 */
export function ThresholdMonitor() {
  const pairsQ   = useQuery({ queryKey: ["thresh", "pairs"],   queryFn: pairsApi.active,         refetchInterval: 5_000 });
  const fundingQ = useQuery({ queryKey: ["thresh", "funding"], queryFn: () => fundingApi.rates(), refetchInterval: 10_000 });

  const pairsRows = pairsQ.data?.pairs ?? [];
  const entryZ    = pairsQ.data?.entryZ ?? 2.0;
  const stopZ     = pairsQ.data?.stopZ  ?? 3.0;
  const fundingRows = fundingQ.data ?? [];
  const fundingThresholdBps = 1.0; // current paper config

  const rows = useMemo(() => {
    const pairs = pairsRows.map((p) => {
      const cur = Math.abs(p.currentZ);
      const pct = Math.min(100, (cur / entryZ) * 100);
      return {
        type: "pair" as const,
        label: `${p.symbolY}/${p.symbolX}`,
        currentText: `|z|=${cur.toFixed(2)}`,
        thresholdText: `entry ${entryZ.toFixed(1)}σ`,
        pct,
        ready: cur >= entryZ,
        warn:  cur >= entryZ * 0.85,
        // additional context shown when ready
        extraText: cur >= stopZ ? `STOP zone (${stopZ.toFixed(1)}σ)` : p.state,
      };
    });

    const funding = fundingRows.map((r) => {
      const cur = Math.abs(r.rateBps);
      const pct = Math.min(100, (cur / fundingThresholdBps) * 100);
      return {
        type: "funding" as const,
        label: r.symbol,
        currentText: `${r.rateBps >= 0 ? "+" : ""}${r.rateBps.toFixed(2)} bp`,
        thresholdText: `≥ ${fundingThresholdBps.toFixed(1)} bp`,
        pct,
        ready: cur >= fundingThresholdBps,
        warn:  cur >= fundingThresholdBps * 0.85,
        extraText: r.signal ?? "",
      };
    });

    return [...pairs, ...funding].sort((a, b) => b.pct - a.pct);
  }, [pairsRows, fundingRows, entryZ, fundingThresholdBps]);

  const readyCount = rows.filter((r) => r.ready).length;
  const warnCount  = rows.filter((r) => r.warn && !r.ready).length;

  if (rows.length === 0) {
    return (
      <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-2">
          🎯 Threshold Monitor
        </p>
        <p className="text-[11px] italic font-serif text-muted-foreground/55 text-center py-4">
          — no active strategies / waiting for first scan —
        </p>
      </div>
    );
  }

  return (
    <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
      <div className="flex items-baseline justify-between mb-3">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
          🎯 Threshold Monitor
        </p>
        <div className="flex items-center gap-2 text-[9px] font-mono">
          {readyCount > 0 && (
            <span className="px-1.5 py-0.5 rounded-full border border-emerald-400/30 bg-emerald-500/15 text-emerald-300 font-black uppercase tracking-widest">
              {readyCount} ready
            </span>
          )}
          {warnCount > 0 && (
            <span className="px-1.5 py-0.5 rounded-full border border-amber-400/30 bg-amber-500/15 text-amber-300 font-black uppercase tracking-widest">
              {warnCount} close
            </span>
          )}
          <span className="text-muted-foreground/45">{rows.length} watched</span>
        </div>
      </div>

      <div className="space-y-1.5">
        {rows.slice(0, 14).map((r) => (
          <ThresholdRow key={`${r.type}-${r.label}`} row={r} />
        ))}
      </div>

      {rows.length > 14 && (
        <p className="text-[9px] font-mono text-muted-foreground/40 text-center pt-2 mt-2 border-t border-white/[0.04]">
          {rows.length - 14} more below threshold (hidden) · sorted by closeness
        </p>
      )}
    </div>
  );
}

function ThresholdRow({
  row,
}: {
  row: {
    type: "pair" | "funding";
    label: string;
    currentText: string;
    thresholdText: string;
    pct: number;
    ready: boolean;
    warn: boolean;
    extraText: string;
  };
}) {
  const fillColor =
    row.ready ? "#22C55E" :
    row.warn  ? "#F59E0B" :
    row.pct >= 50 ? "#0EA5E9" :
                    "#52525B";

  const typeBadge = row.type === "pair"
    ? <span className="text-[8px] uppercase tracking-widest px-1 py-0 rounded bg-cyan-500/10 text-cyan-300 border border-cyan-400/25 shrink-0">PAIR</span>
    : <span className="text-[8px] uppercase tracking-widest px-1 py-0 rounded bg-pink-500/10  text-pink-300  border border-pink-400/25  shrink-0">FUND</span>;

  return (
    <div
      className={cn(
        "grid grid-cols-[78px_44px_1fr_72px_72px] gap-2 items-center text-[10px] font-mono",
        row.ready && "animate-pulse"
      )}
    >
      <span className="font-bold truncate">{row.label}</span>
      {typeBadge}
      <div className="relative h-2 rounded-full bg-white/[0.05] overflow-hidden">
        <div
          className="absolute inset-y-0 left-0 rounded-full transition-all duration-700"
          style={{ width: `${row.pct}%`, backgroundColor: fillColor, opacity: row.ready ? 0.95 : 0.7 }}
        />
        {/* threshold tick at 100% */}
        <div className="absolute right-0 top-[-2px] bottom-[-2px] w-px bg-white/30" />
      </div>
      <span className="tabular-nums text-right" style={{ color: row.ready ? fillColor : "rgba(255,255,255,0.7)" }}>
        {row.currentText}
      </span>
      <span className="text-right text-muted-foreground/45 truncate" title={row.extraText}>
        {row.thresholdText}
      </span>
    </div>
  );
}
