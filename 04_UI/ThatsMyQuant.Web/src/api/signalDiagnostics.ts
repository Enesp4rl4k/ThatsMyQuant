import { api } from "./client";

/**
 * Decision verdicts emitted by the C# {@link SignalDecision} enum. Serialised
 * as strings on the wire (see SignalDiagnosticRowDto in Listener.Api). Keep in
 * sync with `SignalDiagnosticSnapshot.cs` in the Application project.
 */
export type SignalDecision =
  | "BelowThreshold"
  | "NearTrigger"
  | "WouldFire"
  | "Fired"
  | "HoldOpen"
  | "SuppressedByCircuitBreakerTier"
  | "SuppressedByCircuitBreakerGlobal"
  | "SuppressedByMacroPause"
  | "SuppressedByOrphanHalt"
  | "SuppressedByBlacklist"
  | "SuppressedByPortfolioRisk"
  | "SuppressedByKillSwitch"
  | "SkippedByRunner"
  | "Flat";

/**
 * One row on the Signal Diagnostics table — one strategy × symbol/pair
 * evaluation snapshot. Fan-out cadence is ~3s (see C# BroadcastService).
 */
export interface SignalDiagnosticRow {
  utcTimestamp: string;
  tier: string;                    // "Funding:Long" | "Pairs:Long" | "EdgeLab:single_asset"
  strategyTag: string;
  key: string;                     // symbol "LTCUSDT" or pair "ADAUSDT-XRPUSDT"
  currentValue: number;
  entryThreshold: number;
  exitThreshold: number | null;
  stopThreshold: number | null;
  distanceToTriggerPct: number;
  decision: SignalDecision;
  skipOrSuppressReason: string | null;
  regimeContext: string | null;
  hasOpenPosition: boolean;
  estimatedNotionalIfFired: number | null;
}

/** TanStack Query cache key for the full diagnostics panel. */
export const SIGNAL_DIAGNOSTICS_QUERY_KEY = ["signal-diagnostics"] as const;

export const signalDiagnosticsApi = {
  /** Initial load — REST GET; subsequent updates flow via SignalR push. */
  all: () => api.get<SignalDiagnosticRow[]>("/api/signal-diagnostics"),

  /** Tier-scoped query. Optional helper; the page filters client-side by default. */
  byTier: (tier: string) =>
    api.get<SignalDiagnosticRow[]>(
      `/api/signal-diagnostics/by-tier/${encodeURIComponent(tier)}`,
    ),
};
