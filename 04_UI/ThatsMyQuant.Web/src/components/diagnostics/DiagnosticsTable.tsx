import { useMemo, useState } from "react";
import { cn } from "@/lib/utils";
import type { SignalDiagnosticRow } from "@/api/signalDiagnostics";
import { DecisionBadge } from "./DecisionBadge";
import { DistanceBar } from "./DistanceBar";

type SortKey = "tier" | "key" | "currentValue" | "distance" | "decision" | "updatedAgo";

interface Props {
  rows: SignalDiagnosticRow[];
}

/**
 * Sortable, dense table of diagnostic rows. Designed for at-a-glance scanning
 * of 20-50 rows; for higher volumes we'd virtualise (not currently needed —
 * the worker maxes out at ~30 strategies × symbols).
 *
 * Tier semantics in the value/threshold columns:
 *   - Funding tiers: value = funding rate (decimal, e.g. 0.00012); threshold same.
 *     Displayed in basis points (×10000) for human readability.
 *   - Pairs tiers: value = z-score, threshold = entry z. Displayed as raw decimals.
 *   - EdgeLab: value = target weight (-1..+1), threshold = 0. Displayed as raw decimals.
 *
 * The formatter inspects {@link SignalDiagnosticRow.tier} to pick the right format.
 */
export function DiagnosticsTable({ rows }: Props) {
  const [sortKey, setSortKey] = useState<SortKey>("distance");
  const [sortDir, setSortDir] = useState<"asc" | "desc">("desc");

  const sorted = useMemo(() => {
    const cmp = (a: SignalDiagnosticRow, b: SignalDiagnosticRow): number => {
      switch (sortKey) {
        case "tier":         return a.tier.localeCompare(b.tier);
        case "key":          return a.key.localeCompare(b.key);
        case "currentValue": return a.currentValue - b.currentValue;
        case "distance":     return a.distanceToTriggerPct - b.distanceToTriggerPct;
        case "decision":     return a.decision.localeCompare(b.decision);
        case "updatedAgo":   return new Date(a.utcTimestamp).getTime() - new Date(b.utcTimestamp).getTime();
      }
    };
    const out = [...rows].sort(cmp);
    if (sortDir === "desc") out.reverse();
    return out;
  }, [rows, sortKey, sortDir]);

  const handleSort = (key: SortKey) => {
    if (sortKey === key) {
      setSortDir(sortDir === "asc" ? "desc" : "asc");
    } else {
      setSortKey(key);
      setSortDir(key === "tier" || key === "key" ? "asc" : "desc");
    }
  };

  if (rows.length === 0) {
    return (
      <div className="p-12 text-center text-sm text-muted-foreground border border-white/5 rounded-lg bg-[#0D1117]/40">
        No rows match the current filter.
      </div>
    );
  }

  return (
    <div className="rounded-lg border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b border-white/5 text-[9px] uppercase tracking-[0.2em] text-muted-foreground/60">
            <SortHeader label="Tier"     sortKey="tier"         active={sortKey} dir={sortDir} onClick={handleSort} />
            <SortHeader label="Key"      sortKey="key"          active={sortKey} dir={sortDir} onClick={handleSort} />
            <SortHeader label="Value"    sortKey="currentValue" active={sortKey} dir={sortDir} onClick={handleSort} align="right" />
            <th className="text-right px-3 py-3 font-black">Threshold</th>
            <SortHeader label="Distance" sortKey="distance"     active={sortKey} dir={sortDir} onClick={handleSort} />
            <SortHeader label="Decision" sortKey="decision"     active={sortKey} dir={sortDir} onClick={handleSort} />
            <th className="text-left  px-3 py-3 font-black">Reason / Regime</th>
            <SortHeader label="Updated" sortKey="updatedAgo"    active={sortKey} dir={sortDir} onClick={handleSort} />
          </tr>
        </thead>
        <tbody>
          {sorted.map((row) => (
            <Row key={`${row.tier}|${row.key}`} row={row} />
          ))}
        </tbody>
      </table>
    </div>
  );
}

function SortHeader({
  label, sortKey, active, dir, onClick, align = "left",
}: {
  label: string;
  sortKey: SortKey;
  active: SortKey;
  dir: "asc" | "desc";
  onClick: (k: SortKey) => void;
  align?: "left" | "right";
}) {
  const isActive = active === sortKey;
  return (
    <th
      className={cn(
        "px-3 py-3 font-black select-none cursor-pointer hover:text-foreground transition",
        align === "right" ? "text-right" : "text-left",
        isActive && "text-accent",
      )}
      onClick={() => onClick(sortKey)}
    >
      {label}
      {isActive && <span className="ml-1 opacity-70">{dir === "asc" ? "↑" : "↓"}</span>}
    </th>
  );
}

function Row({ row }: { row: SignalDiagnosticRow }) {
  const ageSeconds = Math.max(0, Math.floor((Date.now() - new Date(row.utcTimestamp).getTime()) / 1000));
  const stale = ageSeconds > 120; // 2 min — executor cadences max out near 30 min so 2min is "fine"

  return (
    <tr className="border-b border-white/[0.02] hover:bg-white/[0.02] align-middle">
      <td className="px-3 py-2 text-xs text-muted-foreground/70 font-mono whitespace-nowrap">
        {row.tier}
      </td>
      <td className="px-3 py-2 font-bold whitespace-nowrap">
        {row.key}
        {row.hasOpenPosition && (
          <span className="ml-2 inline-block w-1.5 h-1.5 rounded-full bg-emerald-400" title="Open position" />
        )}
      </td>
      <td className="px-3 py-2 text-right font-mono text-xs">
        {formatValue(row.tier, row.currentValue)}
      </td>
      <td className="px-3 py-2 text-right font-mono text-xs text-muted-foreground/70">
        {formatValue(row.tier, row.entryThreshold)}
      </td>
      <td className="px-3 py-2">
        <div className="flex items-center gap-2">
          <DistanceBar pct={row.distanceToTriggerPct} />
          <span className="text-[10px] font-mono text-muted-foreground/70 w-10 tabular-nums text-right">
            {row.distanceToTriggerPct.toFixed(0)}%
          </span>
        </div>
      </td>
      <td className="px-3 py-2">
        <DecisionBadge decision={row.decision} />
      </td>
      <td className="px-3 py-2 text-xs text-muted-foreground/80 max-w-[280px] truncate" title={row.skipOrSuppressReason ?? row.regimeContext ?? ""}>
        {row.skipOrSuppressReason ?? (
          row.regimeContext ? <span className="text-muted-foreground/50">regime: {row.regimeContext}</span> : ""
        )}
      </td>
      <td className={cn("px-3 py-2 text-[10px] font-mono whitespace-nowrap", stale ? "text-rose-400" : "text-muted-foreground/50")}>
        {formatAge(ageSeconds)}
      </td>
    </tr>
  );
}

function formatValue(tier: string, value: number): string {
  if (tier.startsWith("Funding")) {
    // Display funding rate in basis points × 10000 (decimal → bp)
    return `${(value * 10000).toFixed(2)}bp`;
  }
  if (tier.startsWith("Pairs")) {
    return `z=${value >= 0 ? "+" : ""}${value.toFixed(2)}`;
  }
  if (tier.startsWith("EdgeLab")) {
    return `w=${value >= 0 ? "+" : ""}${value.toFixed(3)}`;
  }
  return value.toFixed(4);
}

function formatAge(seconds: number): string {
  if (seconds < 60) return `${seconds}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m`;
  return `${Math.floor(seconds / 3600)}h`;
}
