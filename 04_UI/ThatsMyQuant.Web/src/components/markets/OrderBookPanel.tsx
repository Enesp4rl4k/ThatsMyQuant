import { useEffect, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { marketsApi, type OrderBook } from "@/api/markets";
import { cn } from "@/lib/utils";
import { SyntheticBadge } from "@/components/ui/SyntheticBadge";
import { getHubConnection, whenConnected } from "@/lib/signalr";

interface Props {
  symbol: string;
}

/**
 * Real-time L2 order book panel.
 *
 * Connection model:
 *   1. On mount, invoke hub method `JoinOrderBook(symbol)` which subscribes the
 *      server to the OKX public books5 WS channel and adds this connection to
 *      the SignalR group "ob:{symbol}".
 *   2. The server fans out `OrderBookSnapshot` messages at ~10 Hz.
 *   3. We throttle rendering to ≤5 fps (200 ms gate) to keep the DOM calm.
 *   4. On symbol change / unmount we invoke `LeaveOrderBook(symbol)` and fall
 *      back to the REST seed (synthetic data) while the new WS channel warms up.
 *
 * If the hub is unreachable, the component stays on the REST seed (5-s polling
 * via useQuery staleTime=30_000 initial load) and shows the SyntheticBadge.
 */
export function OrderBookPanel({ symbol }: Props) {
  // ── REST seed – used as placeholder while the WS channel connects ──────────
  const seedQ = useQuery({
    queryKey: ["markets", "orderbook", symbol],
    queryFn:  () => marketsApi.orderbook(symbol),
    staleTime: 60_000,       // don't re-fetch while WS is live
    refetchInterval: false,  // WS takes over; no polling
    enabled: !!symbol,
  });

  // ── Live state (SignalR push) ──────────────────────────────────────────────
  const [liveOb, setLiveOb] = useState<OrderBook | null>(null);
  const lastRenderMs = useRef<number>(0);

  useEffect(() => {
    if (!symbol) return;
    const conn = getHubConnection();
    let active = true;

    // Handler — filter by symbol (safety) + 5 fps throttle
    const onSnapshot = (snapshot: OrderBook) => {
      if (!active) return;
      if (snapshot.symbol?.toUpperCase() !== symbol.toUpperCase()) return;
      const now = Date.now();
      if (now - lastRenderMs.current < 200) return; // 5 fps cap
      lastRenderMs.current = now;
      setLiveOb(snapshot);
    };

    conn.on("OrderBookSnapshot", onSnapshot);

    // Join the server-side group
    whenConnected()
      .then(() => {
        if (!active) return;
        return conn.invoke("JoinOrderBook", symbol);
      })
      .catch((err) => console.warn("[OrderBook] JoinOrderBook failed:", err));

    return () => {
      active = false;
      conn.off("OrderBookSnapshot", onSnapshot);
      setLiveOb(null);
      // Best-effort leave — ignore errors (connection may already be gone)
      conn.invoke("LeaveOrderBook", symbol).catch(() => {});
    };
  }, [symbol]);

  // ── Render ────────────────────────────────────────────────────────────────
  const ob    = liveOb ?? seedQ.data ?? null;
  const isLive = liveOb !== null && liveOb.isSynthetic === false;
  const maxCum = ob
    ? Math.max(...ob.bids.map((l) => l.cumulative), ...ob.asks.map((l) => l.cumulative), 1)
    : 1;

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden h-full flex flex-col">
      {/* Header */}
      <div className="px-4 py-2.5 border-b border-white/5 flex items-baseline justify-between">
        <div>
          <div className="flex items-center gap-1.5">
            <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
              Order Book
            </p>
            <SyntheticBadge source={isLive ? "live" : "synthetic"} />
          </div>
          <p className="text-[11px] font-serif italic text-muted-foreground/60 mt-0.5">
            {isLive ? "live L2 depth · OKX books5" : "synthetic ladder · connecting…"}
          </p>
        </div>
        {ob && ob.midPrice > 0 && (
          <div className="text-right">
            <p className="text-[9px] uppercase tracking-widest text-muted-foreground/50">Spread</p>
            <p className="font-mono text-[11px] font-bold tabular-nums text-accent">
              {ob.spreadBps.toFixed(2)} bps
            </p>
          </div>
        )}
      </div>

      {/* Book */}
      <div className="flex-1 overflow-hidden flex flex-col text-[11px] font-mono">
        {!ob ? (
          <p className="p-6 text-center italic font-serif text-muted-foreground/60">
            — loading book —
          </p>
        ) : ob.bids.length === 0 ? (
          <p className="p-6 text-center italic font-serif text-muted-foreground/60">
            — no depth available —
          </p>
        ) : (
          <>
            {/* Column headers */}
            <div className="grid grid-cols-3 px-3 py-1.5 border-b border-white/[0.04] text-[8px] uppercase tracking-widest text-muted-foreground/50">
              <span>Price</span>
              <span className="text-right">Size</span>
              <span className="text-right">Total</span>
            </div>

            {/* Asks — rendered bottom-up so best ask is closest to mid */}
            <div className="flex-1 overflow-y-auto flex flex-col-reverse">
              {ob.asks.map((lvl, i) => (
                <Level key={`a-${i}`} level={lvl} maxCum={maxCum} side="ask" />
              ))}
            </div>

            {/* Mid price ribbon */}
            <div className="px-3 py-1.5 bg-accent/[0.08] border-y border-accent/20 flex items-baseline justify-between">
              <span className="text-[9px] uppercase tracking-widest text-accent/80 font-medium">Mid</span>
              <span className="font-mono text-[13px] font-bold text-accent tabular-nums">
                {fmt(ob.midPrice)}
              </span>
            </div>

            {/* Bids — best bid at top */}
            <div className="flex-1 overflow-y-auto">
              {ob.bids.map((lvl, i) => (
                <Level key={`b-${i}`} level={lvl} maxCum={maxCum} side="bid" />
              ))}
            </div>
          </>
        )}
      </div>
    </div>
  );
}

// ── Level row ─────────────────────────────────────────────────────────────────

function Level({
  level,
  maxCum,
  side,
}: {
  level: { price: number; size: number; cumulative: number };
  maxCum: number;
  side: "bid" | "ask";
}) {
  const pct = (level.cumulative / maxCum) * 100;
  // Click → dispatch prefill-order event with this level's price.
  // Bid click → suggests LONG (buy at bid). Ask click → suggests SHORT (sell at ask).
  // OrderEntryPanel + TradePlanCard listen for this event globally.
  const onClick = () => {
    document.dispatchEvent(new CustomEvent("tmq:prefill-order", {
      detail: {
        side: side === "bid" ? "long" : "short",
        entry: level.price,
      },
    }));
  };
  return (
    <button
      onClick={onClick}
      title={`Click → pre-fill order entry at ${fmt(level.price)} (${side === "bid" ? "LONG" : "SHORT"})`}
      className="w-full relative px-3 py-1 grid grid-cols-3 hover:bg-white/[0.04] active:bg-white/[0.06] transition-colors text-left"
    >
      {/* Depth background bar */}
      <div
        className={cn(
          "absolute inset-y-0 right-0 pointer-events-none",
          side === "bid" ? "bg-emerald-500/[0.07]" : "bg-rose-500/[0.07]"
        )}
        style={{ width: `${pct}%` }}
      />
      <span className={cn("relative tabular-nums", side === "bid" ? "text-emerald-400" : "text-rose-400")}>
        {fmt(level.price)}
      </span>
      <span className="relative text-right tabular-nums text-foreground/80">
        {level.size.toFixed(3)}
      </span>
      <span className="relative text-right tabular-nums text-muted-foreground/60">
        {level.cumulative.toFixed(2)}
      </span>
    </button>
  );
}

// ── Helpers ───────────────────────────────────────────────────────────────────

function fmt(v: number): string {
  if (v >= 1_000) return v.toLocaleString(undefined, { maximumFractionDigits: 2 });
  if (v >= 1)     return v.toFixed(2);
  return v.toFixed(5);
}
