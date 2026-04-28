import { createContext, useContext } from "react";

/**
 * "Venue view" — which subset of trades/positions/portfolio the user is
 * currently viewing. This is a CLIENT-SIDE filter dimension that maps to
 * the `Trade.ExecutionMode` field on the backend:
 *
 *   "all"       → no filter (default)
 *   "PAPER"     → simulator paper-trade engine results
 *   "OKX_DEMO"  → OKX demo venue trades
 *   "REAL"      → live trades (any venue)
 *
 * Persisted to localStorage so the user's view choice survives reloads.
 *
 * Note: this is a VIEW filter — it doesn't change which exchange the bot
 * trades on. To change the trading venue, use Settings → Exchange Mode
 * (writes exchange_settings.json + service restart).
 */
export type VenueView = "all" | "PAPER" | "OKX_DEMO" | "REAL";

export interface VenueContextValue {
  venue: VenueView;
  setVenue: (v: VenueView) => void;
  /** Returns the executionMode query parameter (or undefined for "all"). */
  executionModeParam: string | undefined;
}

export const VenueContext = createContext<VenueContextValue>({
  venue: "all",
  setVenue: () => { /* default no-op */ },
  executionModeParam: undefined,
});

export const STORAGE_KEY = "tmq.venue";

export function useVenue(): VenueContextValue {
  return useContext(VenueContext);
}

export function venueLabel(v: VenueView): string {
  return v === "PAPER"     ? "Paper"
       : v === "OKX_DEMO"  ? "OKX Demo"
       : v === "REAL"      ? "Live"
       :                     "All Venues";
}

export function venueColor(v: VenueView): string {
  return v === "PAPER"     ? "amber"
       : v === "OKX_DEMO"  ? "cyan"
       : v === "REAL"      ? "emerald"
       :                     "violet";
}
