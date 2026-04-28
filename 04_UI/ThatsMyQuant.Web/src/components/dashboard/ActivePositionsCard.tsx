import { useEffect, useMemo } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { getHubConnection } from "@/lib/signalr";
import { pushNotification } from "@/lib/notifications";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";

/**
 * Unified "active paper trades" view that bridges three data sources:
 *
 *   1. **Bot internal state** (`/api/paper/state-positions`) — JSON state
 *      files that the FundingHybridExecutor + PairsHybridExecutor maintain
 *      with their open positions (entry price, qty, side, mode).
 *   2. **Real OKX demo account** (`/api/paper/exchange-positions`) — live
 *      positions actually open at the exchange (source of truth for
 *      OKX_DEMO mode).
 *   3. **DB Trade table** (`/api/positions`) — what the dashboard normally
 *      shows; currently empty because hybrid executors don't write here.
 *
 * Until the hybrid executors persist to ITradeRepository, this card is the
 * canonical view of what's actually trading.
 */
export function ActivePositionsCard() {
  const qc = useQueryClient();
  const stateQ = useQuery({
    queryKey: ["paper-state-positions"],
    queryFn:  dashboardApi.paperStatePositions,
    refetchInterval: 5_000,
  });
  const exQ = useQuery({
    queryKey: ["exchange-positions"],
    queryFn:  dashboardApi.exchangePositions,
    refetchInterval: 10_000,
  });

  // SignalR live: invalidate on PositionOpened / PositionClosed events so
  // the table updates within ~1s of the bot acting (vs. polling lag).
  useEffect(() => {
    const conn = getHubConnection();
    const onOpen = (e: { symbol?: string; side?: string; entryPrice?: number; quantity?: number; executionMode?: string }) => {
      qc.invalidateQueries({ queryKey: ["paper-state-positions"] });
      qc.invalidateQueries({ queryKey: ["exchange-positions"] });
      pushNotification({
        type: "success",
        title: `📈 Position OPENED · ${e.symbol ?? "?"}`,
        body:  `${e.side ?? "?"} qty ${(e.quantity ?? 0).toFixed(4)} @ ${(e.entryPrice ?? 0).toFixed(4)} (${e.executionMode ?? "?"})`,
      });
    };
    const onClose = () => {
      qc.invalidateQueries({ queryKey: ["paper-state-positions"] });
      qc.invalidateQueries({ queryKey: ["exchange-positions"] });
    };
    conn.on("PositionOpened", onOpen);
    conn.on("PositionClosed", onClose);
    return () => {
      conn.off("PositionOpened", onOpen);
      conn.off("PositionClosed", onClose);
    };
  }, [qc]);

  const merged = useMemo(() => {
    const fromState: NormalizedRow[] = (stateQ.data?.positions ?? []).map((p) => ({
      source:   "Bot state",
      symbol:   p.symbol,
      side:     p.side,
      quantity: p.quantity,
      entry:    p.entryPrice,
      mode:     p.executionMode,
      tier:     p.tier,
      entryTime: p.entryTimeUtc,
      uPnL:     null,    // not available in state file
      clientOrderId: p.clientOrderId ?? null,
    }));

    const fromExchange: NormalizedRow[] = (exQ.data?.positions ?? []).map((p) => ({
      source:   "Exchange",
      symbol:   p.symbol,
      side:     p.side,
      quantity: Math.abs(p.quantity),
      entry:    p.averageEntryPrice,
      mode:     "OKX_DEMO",
      tier:     "Live OKX",
      entryTime: "",
      uPnL:     p.unrealizedPnL,
      clientOrderId: null,
    }));

    return [...fromState, ...fromExchange];
  }, [stateQ.data, exQ.data]);

  const stateSources = stateQ.data?.sources ?? [];
  const stateCount = stateQ.data?.count ?? 0;
  const exCount    = exQ.data?.positions.length ?? 0;
  const exBalance  = exQ.data?.balance ?? 0;
  const exEquity   = exQ.data?.equity  ?? 0;

  return (
    <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
      <div className="flex items-baseline justify-between mb-3 flex-wrap gap-2">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
            🔴 Active Positions · live
          </p>
          <p className="text-[10px] font-mono text-muted-foreground/55 mt-0.5">
            Bot state files: {stateCount} · OKX demo: {exCount} · {merged.length} total rows
          </p>
        </div>
        {exQ.data && (
          <div className="text-right text-[10px] font-mono">
            <p className="text-muted-foreground/55">OKX Demo</p>
            <p className="text-foreground/85 font-bold tabular-nums">
              ${exBalance.toFixed(2)} bal · ${exEquity.toFixed(2)} eq
            </p>
          </div>
        )}
      </div>

      {/* Source health row */}
      <div className="flex items-center gap-1 flex-wrap text-[9px] font-mono mb-3">
        {stateSources.map((s) => (
          <span
            key={s.fileName}
            className={cn(
              "px-1.5 py-0.5 rounded-full border",
              s.found
                ? s.count > 0
                  ? "border-emerald-400/30 bg-emerald-500/10 text-emerald-300"
                  : "border-white/10 bg-white/[0.02] text-muted-foreground/50"
                : "border-amber-400/30 bg-amber-500/10 text-amber-300"
            )}
            title={s.fileName + (s.lastWriteUtc ? ` · ${new Date(s.lastWriteUtc).toLocaleTimeString()}` : "")}
          >
            {s.tier}: {s.count}
          </span>
        ))}
        <span className={cn(
          "px-1.5 py-0.5 rounded-full border",
          exCount > 0
            ? "border-cyan-400/30 bg-cyan-500/10 text-cyan-300"
            : "border-white/10 bg-white/[0.02] text-muted-foreground/50"
        )}>
          OKX live: {exCount}
        </span>
      </div>

      {merged.length === 0 ? (
        <p className="text-[11px] italic font-serif text-muted-foreground/50 text-center py-4">
          — no active positions detected (neither bot state files nor OKX exchange) —
        </p>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full text-[10px] font-mono">
            <thead>
              <tr className="text-[8px] uppercase tracking-widest text-muted-foreground/45 border-b border-white/[0.04]">
                <th className="text-left py-1 pr-2">Source</th>
                <th className="text-left pr-2">Symbol</th>
                <th className="text-left pr-2">Side</th>
                <th className="text-right pr-2">Qty</th>
                <th className="text-right pr-2">Entry</th>
                <th className="text-right pr-2">uPnL</th>
                <th className="text-left pr-2">Mode</th>
                <th className="text-left pr-2">Tier</th>
                <th className="text-left pr-2">Opened</th>
              </tr>
            </thead>
            <tbody>
              {merged.map((r, i) => (
                <tr key={`${r.symbol}-${r.tier}-${i}`} className="hover:bg-white/[0.02] border-b border-white/[0.02]">
                  <td className="py-1 pr-2">
                    <span className={cn(
                      "px-1.5 py-0 rounded-full text-[8px] uppercase tracking-widest font-black border",
                      r.source === "Exchange"
                        ? "border-cyan-400/30 bg-cyan-500/10 text-cyan-300"
                        : "border-violet-400/30 bg-violet-500/10 text-violet-300"
                    )}>
                      {r.source}
                    </span>
                  </td>
                  <td className="font-bold pr-2">{r.symbol}</td>
                  <td className="pr-2">
                    <span className={r.side === "LONG" ? "text-emerald-300" : "text-rose-300"}>
                      {r.side === "LONG" ? "↑ LONG" : "↓ SHORT"}
                    </span>
                  </td>
                  <td className="text-right tabular-nums pr-2">{r.quantity.toFixed(4)}</td>
                  <td className="text-right tabular-nums text-muted-foreground/75 pr-2">{r.entry.toFixed(4)}</td>
                  <td className={cn(
                    "text-right tabular-nums font-bold pr-2",
                    r.uPnL == null ? "text-muted-foreground/40" :
                    r.uPnL >= 0 ? "text-emerald-300" : "text-rose-300"
                  )}>
                    {r.uPnL == null ? "—" : formatUsd(r.uPnL)}
                  </td>
                  <td className="pr-2">
                    <span className={cn(
                      "px-1 py-0 rounded text-[8px] uppercase tracking-widest font-mono font-black",
                      r.mode === "OKX_DEMO" ? "bg-cyan-500/10 text-cyan-300" :
                      r.mode === "PAPER_DRYRUN" ? "bg-amber-500/10 text-amber-300" :
                      r.mode === "PAPER" ? "bg-violet-500/10 text-violet-300" :
                                            "bg-white/[0.04] text-muted-foreground"
                    )}>
                      {r.mode}
                    </span>
                  </td>
                  <td className="text-muted-foreground/65 text-[9px] pr-2">{r.tier}</td>
                  <td className="text-muted-foreground/55 text-[9px]">
                    {r.entryTime ? new Date(r.entryTime).toLocaleString([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" }) : "—"}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <p className="mt-3 pt-3 border-t border-white/[0.04] text-[9px] font-serif italic text-muted-foreground/50 leading-relaxed">
        Bot's hybrid executors track positions in JSON state files (not the SQLite Trade table) — this card unifies them with the live OKX demo account.
        Once executors persist via ITradeRepository, the regular dashboard tables will populate automatically.
      </p>
    </div>
  );
}

interface NormalizedRow {
  source: "Bot state" | "Exchange";
  symbol: string;
  side:   "LONG" | "SHORT";
  quantity: number;
  entry:    number;
  uPnL:     number | null;
  mode:     string;
  tier:     string;
  entryTime: string;
  clientOrderId: string | null;
}
