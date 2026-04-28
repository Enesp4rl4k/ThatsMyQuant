import { useEffect, useState, useMemo } from "react";
import { drawingStore, type Drawing } from "@/lib/chartDrawings";

/**
 * Subscribe to chart drawings for a symbol and produce CandleChart-ready
 * overlay objects (currently horizontals only — trendlines are kept in the
 * data model for the upcoming click-to-draw interaction).
 */
export function useChartDrawings(symbol: string) {
  const [drawings, setDrawings] = useState<Drawing[]>(() => drawingStore.list(symbol));

  useEffect(() => {
    setDrawings(drawingStore.list(symbol));
    return drawingStore.subscribe((sym, list) => {
      if (sym === symbol) setDrawings(list);
    });
  }, [symbol]);

  const overlays = useMemo(
    () =>
      drawings
        .filter((d): d is Extract<Drawing, { kind: "horizontal" }> => d.kind === "horizontal")
        .map((d) => ({
          price: d.price,
          color: d.color,
          label: d.label ?? "LVL",
          dash: false,
        })),
    [drawings]
  );

  return { drawings, overlays };
}
