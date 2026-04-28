// User-defined watchlist groups (tabs). Persisted in localStorage.
// "All" is implicit — uses the full default basket from the backend.

export interface WatchlistGroup {
  id: string;
  name: string;
  symbols: string[];
}

const KEY = "tmq.watchlist.groups";

const DEFAULTS: WatchlistGroup[] = [
  {
    id: "core",
    name: "Core",
    symbols: ["BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT"],
  },
  {
    id: "defi",
    name: "DeFi",
    symbols: ["AAVEUSDT", "LINKUSDT", "ATOMUSDT", "DOTUSDT"],
  },
  {
    id: "meme",
    name: "Meme",
    symbols: ["DOGEUSDT", "SHIBUSDT"],
  },
];

function read(): WatchlistGroup[] {
  try {
    const raw = localStorage.getItem(KEY);
    if (!raw) return DEFAULTS;
    const parsed = JSON.parse(raw) as WatchlistGroup[];
    return Array.isArray(parsed) && parsed.length > 0 ? parsed : DEFAULTS;
  } catch {
    return DEFAULTS;
  }
}

function write(groups: WatchlistGroup[]): void {
  try {
    localStorage.setItem(KEY, JSON.stringify(groups));
    listeners.forEach((fn) => fn(groups));
  } catch {
    /* ignore */
  }
}

const listeners: Array<(groups: WatchlistGroup[]) => void> = [];

export const watchlistGroupStore = {
  list(): WatchlistGroup[] {
    return read();
  },

  upsert(group: Omit<WatchlistGroup, "id"> & { id?: string }): WatchlistGroup {
    const all = read();
    const id = group.id ?? crypto.randomUUID();
    const ws: WatchlistGroup = {
      id,
      name: group.name.trim() || "Untitled",
      symbols: Array.from(new Set(group.symbols.map((s) => s.toUpperCase()))),
    };
    const idx = all.findIndex((g) => g.id === id);
    if (idx >= 0) all[idx] = ws;
    else all.push(ws);
    write(all);
    return ws;
  },

  rename(id: string, name: string): void {
    const all = read();
    const g = all.find((x) => x.id === id);
    if (!g) return;
    g.name = name.trim() || g.name;
    write(all);
  },

  toggleSymbol(id: string, symbol: string): void {
    const all = read();
    const g = all.find((x) => x.id === id);
    if (!g) return;
    const sym = symbol.toUpperCase();
    g.symbols = g.symbols.includes(sym)
      ? g.symbols.filter((s) => s !== sym)
      : [...g.symbols, sym];
    write(all);
  },

  remove(id: string): void {
    write(read().filter((g) => g.id !== id));
  },

  resetDefaults(): void {
    write(DEFAULTS);
  },

  subscribe(fn: (groups: WatchlistGroup[]) => void): () => void {
    listeners.push(fn);
    return () => {
      const i = listeners.indexOf(fn);
      if (i >= 0) listeners.splice(i, 1);
    };
  },
};
