// Per-page widget visibility store. Operator can toggle which panels show
// up on a page; default template is the curated layout we ship with. Stored
// in localStorage under `tmq.customize.{pageId}` as a `Record<widgetId, boolean>`.
//
// Unknown widget IDs (e.g. when we ship a new panel) default to the spec's
// `defaultVisible` flag so existing users see new panels without resetting.

export interface WidgetSpec {
  /** Stable ID — used as storage key. Don't rename without a migration. */
  id: string;
  /** Human label shown in the customize drawer. */
  label: string;
  /** One-line description of what the panel shows. */
  description: string;
  /** Optional category for grouping in the drawer. */
  category?: string;
  /** Whether to show the panel for new operators. */
  defaultVisible: boolean;
  /** When true, the operator cannot hide this panel (always-on). */
  required?: boolean;
}

const KEY_PREFIX = "tmq.customize.";

function read(pageId: string): Record<string, boolean> {
  try {
    const raw = localStorage.getItem(KEY_PREFIX + pageId);
    if (!raw) return {};
    const parsed = JSON.parse(raw);
    return typeof parsed === "object" && parsed !== null ? parsed : {};
  } catch {
    return {};
  }
}

function write(pageId: string, state: Record<string, boolean>): void {
  try {
    localStorage.setItem(KEY_PREFIX + pageId, JSON.stringify(state));
    listeners.get(pageId)?.forEach((fn) => fn(state));
  } catch {
    /* ignore quota / private mode */
  }
}

const listeners = new Map<string, Array<(s: Record<string, boolean>) => void>>();

export const customizationStore = {
  get(pageId: string): Record<string, boolean> {
    return read(pageId);
  },

  isVisible(pageId: string, widgetId: string, fallback: boolean): boolean {
    const state = read(pageId);
    return widgetId in state ? state[widgetId] : fallback;
  },

  toggle(pageId: string, widgetId: string, currentlyVisible: boolean): void {
    const state = read(pageId);
    state[widgetId] = !currentlyVisible;
    write(pageId, state);
  },

  set(pageId: string, widgetId: string, visible: boolean): void {
    const state = read(pageId);
    state[widgetId] = visible;
    write(pageId, state);
  },

  reset(pageId: string): void {
    try {
      localStorage.removeItem(KEY_PREFIX + pageId);
      listeners.get(pageId)?.forEach((fn) => fn({}));
    } catch {
      /* ignore */
    }
  },

  /** Subscribe to mutations for a specific page. */
  subscribe(pageId: string, fn: (state: Record<string, boolean>) => void): () => void {
    if (!listeners.has(pageId)) listeners.set(pageId, []);
    listeners.get(pageId)!.push(fn);
    return () => {
      const arr = listeners.get(pageId);
      if (!arr) return;
      const idx = arr.indexOf(fn);
      if (idx >= 0) arr.splice(idx, 1);
    };
  },
};
