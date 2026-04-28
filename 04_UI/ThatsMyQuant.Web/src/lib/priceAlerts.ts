// Client-side price alert store. Persisted in localStorage, evaluated on each
// price tick (via watchlist 15s refetch + main chart 30s refetch).
// When triggered → fires a notification via the shared bell/toast bus.

import { pushNotification } from "./notifications";

export interface PriceAlert {
  id: string;
  symbol: string;
  direction: "above" | "below";
  price: number;
  createdUtc: string;
  triggeredUtc: string | null;
}

const KEY = "tmq.priceAlerts";

function read(): PriceAlert[] {
  try {
    const raw = localStorage.getItem(KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw) as PriceAlert[];
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

function write(alerts: PriceAlert[]): void {
  try {
    localStorage.setItem(KEY, JSON.stringify(alerts));
    listeners.forEach((fn) => fn(alerts));
  } catch {
    /* ignore */
  }
}

const listeners: Array<(alerts: PriceAlert[]) => void> = [];

export const priceAlertStore = {
  list(): PriceAlert[] {
    return read();
  },

  forSymbol(symbol: string): PriceAlert[] {
    return read().filter((a) => a.symbol === symbol);
  },

  add(symbol: string, direction: "above" | "below", price: number): PriceAlert {
    const alert: PriceAlert = {
      id: crypto.randomUUID(),
      symbol: symbol.toUpperCase(),
      direction,
      price,
      createdUtc: new Date().toISOString(),
      triggeredUtc: null,
    };
    write([...read(), alert]);
    return alert;
  },

  remove(id: string): void {
    write(read().filter((a) => a.id !== id));
  },

  clearTriggered(): void {
    write(read().filter((a) => !a.triggeredUtc));
  },

  /** Subscribe to mutations. Returns an unsubscribe fn. */
  subscribe(fn: (alerts: PriceAlert[]) => void): () => void {
    listeners.push(fn);
    return () => {
      const i = listeners.indexOf(fn);
      if (i >= 0) listeners.splice(i, 1);
    };
  },

  /**
   * Evaluate all active alerts against a fresh price for a symbol.
   * Triggered alerts fire a notification and stay in the list with a
   * triggeredUtc timestamp so the user can review.
   */
  evaluate(symbol: string, price: number): void {
    const all = read();
    let mutated = false;
    for (const a of all) {
      if (a.symbol !== symbol.toUpperCase() || a.triggeredUtc) continue;
      const hit =
        (a.direction === "above" && price >= a.price) ||
        (a.direction === "below" && price <= a.price);
      if (hit) {
        a.triggeredUtc = new Date().toISOString();
        mutated = true;
        pushNotification({
          type: "warning",
          title: `🔔 ${a.symbol} ${a.direction} ${a.price}`,
          body: `Last price ${formatPrice(price)} crossed your ${a.direction} alert`,
        });
      }
    }
    if (mutated) write(all);
  },
};

function formatPrice(v: number): string {
  if (v >= 1000) return v.toLocaleString(undefined, { maximumFractionDigits: 2 });
  if (v >= 1) return v.toFixed(2);
  return v.toFixed(5);
}
