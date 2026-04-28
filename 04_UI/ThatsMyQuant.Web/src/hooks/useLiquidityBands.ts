import { useEffect, useState } from "react";
import { getHubConnection, whenConnected } from "@/lib/signalr";
import type { LiquidityBand } from "@/components/charts/CandleChart";
import type { OrderBook, OrderBookLevel } from "@/api/markets";

/**
 * Subscribes to the OKX order book SignalR group `ob:{symbol}` (already wired
 * by OrderBookPanel — joining a second time is fine; the hub handles
 * reference-counting via _subscriptions internally).  Converts incoming
 * book snapshots into a flat list of price-level liquidity bands suitable
 * for {@link CandleChart}'s `liquidityBands` prop.
 *
 * The intensity field is normalised against the largest size in either side
 * of the snapshot, so bands fade in/out as orders grow/shrink.
 */
export function useLiquidityBands(symbol: string | undefined): LiquidityBand[] {
  const [bands, setBands] = useState<LiquidityBand[]>([]);

  useEffect(() => {
    if (!symbol) return;
    const conn = getHubConnection();
    let active = true;

    const onSnapshot = (snapshot: OrderBook) => {
      if (!active) return;
      if (snapshot.symbol?.toUpperCase() !== symbol.toUpperCase()) return;
      setBands(toBands(snapshot));
    };
    conn.on("OrderBookSnapshot", onSnapshot);

    whenConnected()
      .then(() => active && conn.invoke("JoinOrderBook", symbol))
      .catch(() => { /* ignore */ });

    return () => {
      active = false;
      conn.off("OrderBookSnapshot", onSnapshot);
      conn.invoke("LeaveOrderBook", symbol).catch(() => {});
    };
  }, [symbol]);

  return bands;
}

function toBands(snap: OrderBook): LiquidityBand[] {
  const all = [...snap.bids, ...snap.asks];
  const maxSize = Math.max(...all.map((l: OrderBookLevel) => l.size), 1);
  const out: LiquidityBand[] = [];
  for (const lvl of snap.bids) {
    out.push({ price: lvl.price, intensity: lvl.size / maxSize, side: "bid" });
  }
  for (const lvl of snap.asks) {
    out.push({ price: lvl.price, intensity: lvl.size / maxSize, side: "ask" });
  }
  return out;
}
