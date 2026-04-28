import { useEffect, useMemo, useState } from "react";
import { customizationStore, type WidgetSpec } from "@/lib/pageCustomization";

export interface PageCustomizationApi {
  /** Whether a given widget should render right now. */
  isVisible: (widgetId: string) => boolean;
  /** Toggle a widget's visibility. */
  toggle: (widgetId: string) => void;
  /** Force a widget visible / hidden. */
  set: (widgetId: string, visible: boolean) => void;
  /** Reset to default template. */
  reset: () => void;
  /** Number of widgets currently visible. */
  visibleCount: number;
  /** Total widgets the page declares. */
  totalCount: number;
  /** True when the operator has any deviation from defaults. */
  isCustomized: boolean;
  /** All widget specs (for the customize drawer). */
  widgets: WidgetSpec[];
}

/**
 * Hook for page-level widget customization. Pass a stable `pageId` and the
 * full list of widget specs the page can render. Returns helpers + a state
 * that auto-updates when the user toggles things in the customize drawer.
 */
export function usePageCustomization(pageId: string, widgets: WidgetSpec[]): PageCustomizationApi {
  const [state, setState] = useState<Record<string, boolean>>(() =>
    customizationStore.get(pageId)
  );

  useEffect(() => {
    setState(customizationStore.get(pageId));
    return customizationStore.subscribe(pageId, setState);
  }, [pageId]);

  const isCustomized = useMemo(() => Object.keys(state).length > 0, [state]);

  const visibleCount = useMemo(() => {
    return widgets.filter((w) => {
      if (w.required) return true;
      return w.id in state ? state[w.id] : w.defaultVisible;
    }).length;
  }, [widgets, state]);

  return {
    isVisible: (widgetId) => {
      const spec = widgets.find((w) => w.id === widgetId);
      if (!spec) return true; // unknown ID → safe default visible
      if (spec.required) return true;
      return widgetId in state ? state[widgetId] : spec.defaultVisible;
    },
    toggle: (widgetId) => {
      const spec = widgets.find((w) => w.id === widgetId);
      if (spec?.required) return;
      const cur = widgetId in state ? state[widgetId] : (spec?.defaultVisible ?? true);
      customizationStore.toggle(pageId, widgetId, cur);
    },
    set: (widgetId, visible) => customizationStore.set(pageId, widgetId, visible),
    reset: () => customizationStore.reset(pageId),
    visibleCount,
    totalCount: widgets.length,
    isCustomized,
    widgets,
  };
}
