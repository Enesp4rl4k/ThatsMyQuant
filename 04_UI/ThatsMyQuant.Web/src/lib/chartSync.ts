import { createContext, useContext } from "react";

export interface ChartSyncValue {
  /** Index (0-based) of the bar under the cursor — null when mouse is outside. */
  hoveredIndex: number | null;
  setHoveredIndex: (i: number | null) => void;
}

export const ChartSyncContext = createContext<ChartSyncValue | null>(null);

/** Use inside any chart that participates in the cross-pane crosshair. */
export function useChartSync(): ChartSyncValue | null {
  return useContext(ChartSyncContext);
}
