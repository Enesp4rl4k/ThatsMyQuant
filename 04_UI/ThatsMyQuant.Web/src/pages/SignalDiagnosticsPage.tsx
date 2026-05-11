import { useEffect, useMemo, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
  signalDiagnosticsApi,
  SIGNAL_DIAGNOSTICS_QUERY_KEY,
  type SignalDiagnosticRow,
} from "@/api/signalDiagnostics";
import { getDiagnosticsHubConnection } from "@/lib/signalrDiagnostics";
import {
  SuppressionFilters,
  type FilterState,
} from "@/components/diagnostics/SuppressionFilters";
import { DiagnosticsTable } from "@/components/diagnostics/DiagnosticsTable";
import { decisionGroup } from "@/components/diagnostics/DecisionBadge";

/**
 * Signal Diagnostics — operator-facing observability page.
 *
 * Answers the paper-wait question: "13 strategies live, 0 fills — why?".
 * Reads the live state from the worker (per-strategy × symbol/pair signal value,
 * threshold, distance, decision, suppression reason). Initial data via REST,
 * subsequent updates via SignalR push (3s cadence).
 *
 * READ-ONLY. Never mutates strategy state. Pre-reg-discipline-safe.
 */
export function SignalDiagnosticsPage() {
  const qc = useQueryClient();
  const [filter, setFilter] = useState<FilterState>({
    tier: "all",
    decision: "all",
    search: "",
    onlySuppressed: false,
  });
  const [lastUpdate, setLastUpdate] = useState<Date | null>(null);

  // Initial REST load + cache identity for SignalR push updates.
  const query = useQuery({
    queryKey: SIGNAL_DIAGNOSTICS_QUERY_KEY,
    queryFn: signalDiagnosticsApi.all,
    refetchInterval: 30_000, // safety net if SignalR drops
    staleTime: 5_000,
  });

  // Wire SignalR push: every "SignalDiagnosticsUpdate" replaces the cache.
  useEffect(() => {
    const conn = getDiagnosticsHubConnection();

    const handler = (rows: SignalDiagnosticRow[]) => {
      qc.setQueryData(SIGNAL_DIAGNOSTICS_QUERY_KEY, rows);
      setLastUpdate(new Date());
    };

    conn.on("SignalDiagnosticsUpdate", handler);
    return () => {
      conn.off("SignalDiagnosticsUpdate", handler);
    };
  }, [qc]);

  const allRows = query.data ?? [];

  const availableTiers = useMemo(
    () => Array.from(new Set(allRows.map((r) => r.tier))).sort(),
    [allRows],
  );

  const filtered = useMemo(() => {
    return allRows.filter((row) => {
      if (filter.tier !== "all" && row.tier !== filter.tier) return false;

      const wantedDecision = filter.onlySuppressed ? "suppressed" : filter.decision;
      if (wantedDecision !== "all") {
        if (decisionGroup(row.decision) !== wantedDecision) return false;
      }

      if (filter.search.trim().length > 0) {
        const needle = filter.search.trim().toLowerCase();
        if (
          !row.key.toLowerCase().includes(needle) &&
          !row.strategyTag.toLowerCase().includes(needle) &&
          !row.tier.toLowerCase().includes(needle)
        ) {
          return false;
        }
      }
      return true;
    });
  }, [allRows, filter]);

  const counts = useMemo(() => {
    const buckets: Record<string, number> = { active: 0, near: 0, below: 0, skipped: 0, suppressed: 0 };
    for (const r of allRows) buckets[decisionGroup(r.decision)]++;
    return buckets;
  }, [allRows]);

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="relative">
        <div className="absolute -left-4 top-0 bottom-0 w-1 bg-accent shadow-[0_0_15px_rgba(0,191,255,0.5)]" />
        <h2 className="text-[10px] font-black uppercase tracking-[0.4em] text-accent mb-1 ml-2">
          Operator Observability
        </h2>
        <p className="text-2xl font-black tracking-tight ml-2">
          Signal Diagnostics{" "}
          <span className="text-muted-foreground/30 font-light">
            — strategy × symbol per-iteration state · ~3s push
          </span>
        </p>
      </div>

      {/* Filters */}
      <SuppressionFilters
        state={filter}
        onChange={setFilter}
        availableTiers={availableTiers}
        lastUpdate={lastUpdate}
        totalRows={allRows.length}
        visibleRows={filtered.length}
      />

      {/* Loading / error / table */}
      {query.isLoading ? (
        <div className="p-12 text-center text-sm text-muted-foreground border border-white/5 rounded-lg bg-[#0D1117]/40">
          Loading diagnostics…
        </div>
      ) : query.isError ? (
        <div className="p-12 text-center text-sm text-rose-400 border border-rose-500/30 rounded-lg bg-rose-500/5">
          Failed to load diagnostics. Worker may be offline or the registry is empty.
        </div>
      ) : (
        <DiagnosticsTable rows={filtered} />
      )}

      {/* Summary footer */}
      {allRows.length > 0 && (
        <div className="flex flex-wrap items-center gap-4 text-[11px] text-muted-foreground/70 px-3">
          <SummaryPill label="Active"     value={counts.active}     dotClass="bg-emerald-400" />
          <SummaryPill label="Near"       value={counts.near}       dotClass="bg-amber-400" />
          <SummaryPill label="Below"      value={counts.below}      dotClass="bg-slate-400" />
          <SummaryPill label="Skipped"    value={counts.skipped}    dotClass="bg-violet-400" />
          <SummaryPill label="Suppressed" value={counts.suppressed} dotClass="bg-red-400" />
          <span className="ml-auto text-muted-foreground/40">
            {availableTiers.length} tiers · {allRows.length} total rows tracked
          </span>
        </div>
      )}
    </div>
  );
}

function SummaryPill({ label, value, dotClass }: { label: string; value: number; dotClass: string }) {
  return (
    <span className="inline-flex items-center gap-1.5">
      <span className={`w-1.5 h-1.5 rounded-full ${dotClass}`} />
      <span className="font-bold text-foreground">{value}</span>
      <span className="uppercase tracking-wider">{label}</span>
    </span>
  );
}
