import { cn } from "@/lib/utils";

export type DecisionFilter = "all" | "active" | "near" | "below" | "skipped" | "suppressed";

export interface FilterState {
  tier: string;            // "all" | tier prefix substring
  decision: DecisionFilter;
  search: string;          // free-text symbol/key search
  onlySuppressed: boolean; // shortcut toggle (overrides decision filter to "suppressed")
}

interface Props {
  state: FilterState;
  onChange: (next: FilterState) => void;
  availableTiers: string[];
  lastUpdate?: Date | null;
  totalRows: number;
  visibleRows: number;
}

/**
 * Filter bar across the top of the Diagnostics page. The "only suppressed"
 * toggle is a deliberate shortcut for the most common operator question —
 * "what's currently being silently killed by a downstream gate?"
 */
export function SuppressionFilters({
  state,
  onChange,
  availableTiers,
  lastUpdate,
  totalRows,
  visibleRows,
}: Props) {
  return (
    <div className="flex flex-wrap items-center gap-3 p-3 rounded-lg border border-white/5 bg-[#0D1117]/60 backdrop-blur-sm">
      {/* Tier dropdown */}
      <label className="flex items-center gap-2 text-[10px] uppercase tracking-[0.18em] text-muted-foreground/70">
        <span>Tier</span>
        <select
          value={state.tier}
          onChange={(e) => onChange({ ...state, tier: e.target.value })}
          className="bg-[#070A0F] border border-white/10 rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
        >
          <option value="all">All tiers</option>
          {availableTiers.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </select>
      </label>

      {/* Decision filter */}
      <label className="flex items-center gap-2 text-[10px] uppercase tracking-[0.18em] text-muted-foreground/70">
        <span>Decision</span>
        <select
          value={state.decision}
          onChange={(e) => onChange({ ...state, decision: e.target.value as DecisionFilter, onlySuppressed: false })}
          disabled={state.onlySuppressed}
          className="bg-[#070A0F] border border-white/10 rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent disabled:opacity-40"
        >
          <option value="all">All</option>
          <option value="active">Active (Fired/Hold)</option>
          <option value="near">Near trigger (≥80%)</option>
          <option value="below">Below threshold</option>
          <option value="skipped">Skipped by runner</option>
          <option value="suppressed">Suppressed by gate</option>
        </select>
      </label>

      {/* Search */}
      <input
        type="search"
        placeholder="Search symbol / pair…"
        value={state.search}
        onChange={(e) => onChange({ ...state, search: e.target.value })}
        className="flex-1 min-w-[200px] bg-[#070A0F] border border-white/10 rounded px-3 py-1 text-xs text-foreground placeholder:text-muted-foreground/40 focus:outline-none focus:border-accent"
      />

      {/* Only suppressed shortcut */}
      <label className="flex items-center gap-2 text-[10px] uppercase tracking-[0.18em] cursor-pointer select-none">
        <input
          type="checkbox"
          checked={state.onlySuppressed}
          onChange={(e) => onChange({ ...state, onlySuppressed: e.target.checked })}
          className="accent-accent"
        />
        <span className={cn(state.onlySuppressed ? "text-rose-400" : "text-muted-foreground/70")}>
          Only suppressed
        </span>
      </label>

      {/* Status: count + last update */}
      <div className="ml-auto text-[10px] text-muted-foreground/60 font-mono whitespace-nowrap">
        <span className="text-foreground/80">{visibleRows}</span>
        <span className="mx-1">/</span>
        <span>{totalRows}</span>
        {lastUpdate && (
          <span className="ml-3 text-muted-foreground/50">
            ↻ {lastUpdate.toLocaleTimeString()}
          </span>
        )}
      </div>
    </div>
  );
}
