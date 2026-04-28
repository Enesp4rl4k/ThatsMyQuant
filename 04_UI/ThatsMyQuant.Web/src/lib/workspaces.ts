// Markets workspace snapshots — save/load named layouts of:
//   - selected primary symbol
//   - pinned mini-chart symbols
//   - chart layout mode (single / split-2 / quad)
//   - per-pane symbol + timeframe assignments
//   - active indicator chips on the main chart
// Persisted in localStorage. Lightweight; no server.

export type LayoutMode = "single" | "split-2" | "quad";

export interface ChartPane {
  symbol: string;
  timeframe: string;
}

export interface MarketsWorkspace {
  id: string;
  name: string;
  createdUtc: string;
  selected: string;
  pinned: string[];
  layoutMode: LayoutMode;
  panes: ChartPane[];
  indicators: string[];
}

const KEY = "tmq.markets.workspaces";

function read(): MarketsWorkspace[] {
  try {
    const raw = localStorage.getItem(KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw) as MarketsWorkspace[];
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

function write(ws: MarketsWorkspace[]): void {
  try {
    localStorage.setItem(KEY, JSON.stringify(ws));
    listeners.forEach((fn) => fn(ws));
  } catch {
    /* ignore quota / private mode */
  }
}

const listeners: Array<(ws: MarketsWorkspace[]) => void> = [];

export const workspaceStore = {
  list(): MarketsWorkspace[] {
    return read();
  },

  save(name: string, snapshot: Omit<MarketsWorkspace, "id" | "name" | "createdUtc">): MarketsWorkspace {
    const trimmed = name.trim() || `Workspace ${new Date().toLocaleString()}`;
    const all = read();
    const existing = all.find((w) => w.name.toLowerCase() === trimmed.toLowerCase());

    const ws: MarketsWorkspace = {
      id: existing?.id ?? crypto.randomUUID(),
      name: trimmed,
      createdUtc: new Date().toISOString(),
      ...snapshot,
    };

    if (existing) {
      write(all.map((w) => (w.id === ws.id ? ws : w)));
    } else {
      write([...all, ws]);
    }
    return ws;
  },

  remove(id: string): void {
    write(read().filter((w) => w.id !== id));
  },

  subscribe(fn: (ws: MarketsWorkspace[]) => void): () => void {
    listeners.push(fn);
    return () => {
      const i = listeners.indexOf(fn);
      if (i >= 0) listeners.splice(i, 1);
    };
  },
};
