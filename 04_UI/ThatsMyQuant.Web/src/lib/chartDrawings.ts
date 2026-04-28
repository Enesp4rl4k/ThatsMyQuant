// Per-symbol chart drawings — horizontal lines + trendlines.
// Persisted in localStorage under tmq.chart.drawings.{SYMBOL}.

export interface HorizontalDrawing {
  id: string;
  kind: "horizontal";
  price: number;
  color: string;
  label?: string;
}

export interface TrendlineDrawing {
  id: string;
  kind: "trendline";
  /** ISO timestamps anchoring each end. */
  fromTime: string;
  fromPrice: number;
  toTime: string;
  toPrice: number;
  color: string;
}

export type Drawing = HorizontalDrawing | TrendlineDrawing;

const PREFIX = "tmq.chart.drawings.";

function key(symbol: string): string {
  return PREFIX + symbol.toUpperCase();
}

function read(symbol: string): Drawing[] {
  try {
    const raw = localStorage.getItem(key(symbol));
    if (!raw) return [];
    const parsed = JSON.parse(raw) as Drawing[];
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

function write(symbol: string, drawings: Drawing[]): void {
  try {
    localStorage.setItem(key(symbol), JSON.stringify(drawings));
    listeners.forEach((fn) => fn(symbol, drawings));
  } catch {
    /* ignore */
  }
}

const listeners: Array<(symbol: string, drawings: Drawing[]) => void> = [];

// === Tool mode (transient — not persisted) ===

export type ToolMode = "none" | "horizontal" | "trendline";

let toolMode: ToolMode = "none";
let pendingAnchor: { time: string; price: number } | null = null;
const modeListeners: Array<(mode: ToolMode, anchor: typeof pendingAnchor) => void> = [];

function notifyMode() {
  modeListeners.forEach((fn) => fn(toolMode, pendingAnchor));
}

export const drawingMode = {
  get(): ToolMode {
    return toolMode;
  },
  pendingAnchor() {
    return pendingAnchor;
  },
  set(mode: ToolMode) {
    toolMode = mode;
    pendingAnchor = null;
    notifyMode();
  },
  /**
   * Process a chart click — drops a horizontal, starts a trendline anchor,
   * or completes a trendline depending on the active tool.
   */
  handleChartClick(symbol: string, time: string, price: number): void {
    if (toolMode === "horizontal") {
      drawingStore.addHorizontal(symbol, price);
      toolMode = "none";
      pendingAnchor = null;
      notifyMode();
      return;
    }
    if (toolMode === "trendline") {
      if (!pendingAnchor) {
        pendingAnchor = { time, price };
        notifyMode();
      } else {
        drawingStore.addTrendline(symbol, pendingAnchor.time, pendingAnchor.price, time, price);
        pendingAnchor = null;
        toolMode = "none";
        notifyMode();
      }
      return;
    }
  },
  cancel() {
    toolMode = "none";
    pendingAnchor = null;
    notifyMode();
  },
  subscribe(fn: (mode: ToolMode, anchor: typeof pendingAnchor) => void): () => void {
    modeListeners.push(fn);
    fn(toolMode, pendingAnchor);
    return () => {
      const i = modeListeners.indexOf(fn);
      if (i >= 0) modeListeners.splice(i, 1);
    };
  },
};

export const drawingStore = {
  list(symbol: string): Drawing[] {
    return read(symbol);
  },

  addHorizontal(symbol: string, price: number, color = "#1E3A8A"): HorizontalDrawing {
    const d: HorizontalDrawing = {
      id: crypto.randomUUID(),
      kind: "horizontal",
      price,
      color,
    };
    write(symbol, [...read(symbol), d]);
    return d;
  },

  addTrendline(
    symbol: string,
    fromTime: string,
    fromPrice: number,
    toTime: string,
    toPrice: number,
    color = "#1E3A8A"
  ): TrendlineDrawing {
    const d: TrendlineDrawing = {
      id: crypto.randomUUID(),
      kind: "trendline",
      fromTime,
      fromPrice,
      toTime,
      toPrice,
      color,
    };
    write(symbol, [...read(symbol), d]);
    return d;
  },

  remove(symbol: string, id: string): void {
    write(symbol, read(symbol).filter((d) => d.id !== id));
  },

  clear(symbol: string): void {
    write(symbol, []);
  },

  subscribe(fn: (symbol: string, drawings: Drawing[]) => void): () => void {
    listeners.push(fn);
    return () => {
      const i = listeners.indexOf(fn);
      if (i >= 0) listeners.splice(i, 1);
    };
  },
};
