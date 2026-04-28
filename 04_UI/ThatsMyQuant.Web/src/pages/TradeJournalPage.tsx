import { useMemo, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useVirtualizer } from "@tanstack/react-virtual";
import { dashboardApi } from "@/api/dashboard";
import { cn } from "@/lib/utils";
import { formatDateTime, formatPercentLoose, formatUsd, pnlColor } from "@/lib/format";
import { StrategyBreakdownCard } from "@/components/trades/StrategyBreakdownCard";
import { Skeleton } from "@/components/ui/Skeleton";
import { StrategyPill, StatusPill } from "@/components/ui/StrategyPill";
import { useVenue } from "@/hooks/useVenue";
import type { TradeJournal } from "@/api/types";

function exportCsv(trades: TradeJournal[]) {
  const header = [
    "Exit Time", "Symbol", "Side", "Entry Price", "Exit Price", "Qty",
    "Realized PnL", "PnL%", "Strategy", "Exit Reason", "Entry Time",
  ].join(",");

  const rows = trades.map((t) =>
    [
      t.exitTime ? new Date(t.exitTime).toISOString() : "",
      t.symbol,
      t.side,
      t.entryPrice,
      t.exitPrice,
      t.quantity,
      t.realizedPnL.toFixed(4),
      t.pnlPercent.toFixed(4),
      t.strategyTag,
      `"${(t.exitReason || "").replace(/"/g, '""')}"`,
      new Date(t.entryTime).toISOString(),
    ].join(",")
  );

  const csv = [header, ...rows].join("\n");
  const blob = new Blob([csv], { type: "text/csv;charset=utf-8;" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = `trades_${new Date().toISOString().slice(0, 10)}.csv`;
  a.click();
  URL.revokeObjectURL(url);
}

type StatusFilter = "" | "open" | "closed";

// 9-column grid template — matches header layout below
const GRID_COLS = "minmax(140px,1.4fr) minmax(80px,0.9fr) 70px minmax(90px,1fr) minmax(90px,1fr) minmax(90px,1fr) 80px minmax(110px,1fr) minmax(140px,1.6fr)";

export function TradeJournalPage() {
  const [strategy, setStrategy] = useState("");
  const [status, setStatus] = useState<StatusFilter>("closed");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [limit, setLimit] = useState(100);

  const breakdownQ = useQuery({
    queryKey: ["trades", "by-strategy"],
    queryFn: dashboardApi.strategyBreakdown,
    refetchInterval: 30_000,
  });

  const { executionModeParam } = useVenue();
  const tradesQ = useQuery({
    queryKey: ["trades", "filtered", { strategy, status, from, to, limit, executionModeParam }],
    queryFn: () =>
      dashboardApi.filteredTrades({
        limit,
        strategy: strategy || undefined,
        status: status || undefined,
        from: from || undefined,
        to: to || undefined,
        executionMode: executionModeParam,
      }),
    refetchInterval: 10_000,
  });

  const trades = tradesQ.data ?? [];

  const strategyOptions = useMemo(
    () => (breakdownQ.data ?? []).map((s) => s.strategyTag),
    [breakdownQ.data]
  );

  const reset = () => {
    setStrategy("");
    setStatus("closed");
    setFrom("");
    setTo("");
    setLimit(100);
  };

  return (
    <div className="space-y-8">
      <div className="flex items-start justify-between">
        <div className="relative">
          <div className="absolute -left-4 top-0 bottom-0 w-1 bg-accent shadow-[0_0_15px_rgba(0,191,255,0.5)]" />
          <h2 className="text-[10px] font-black uppercase tracking-[0.4em] text-accent mb-1 ml-2">
            Closed Trades
          </h2>
          <p className="text-2xl font-black tracking-tight ml-2">
            Trade Journal{" "}
            <span className="text-muted-foreground/30 font-light">
              — {trades.length} of {limit} matching filters
            </span>
          </p>
        </div>
        {trades.length > 0 && (
          <button
            onClick={() => exportCsv(trades)}
            className="mt-1 px-4 py-2 rounded-lg border border-white/10 bg-white/[0.02] text-[10px] font-black uppercase tracking-wider text-muted-foreground hover:text-foreground hover:bg-white/[0.05] transition-colors flex items-center gap-2"
            aria-label="Export filtered trades as CSV"
          >
            <span aria-hidden>⬇</span> Export CSV
          </button>
        )}
      </div>

      <StrategyBreakdownCard />

      {/* Filters */}
      <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4 flex flex-wrap items-end gap-4">
        <Field label="Strategy">
          <select
            value={strategy}
            onChange={(e) => setStrategy(e.target.value)}
            aria-label="Filter by strategy"
            className="bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono w-44"
          >
            <option value="">All</option>
            {strategyOptions.map((s) => (
              <option key={s} value={s}>{s}</option>
            ))}
          </select>
        </Field>
        <Field label="Status">
          <select
            value={status}
            onChange={(e) => setStatus(e.target.value as StatusFilter)}
            aria-label="Filter by trade status"
            className="bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono w-32"
          >
            <option value="">All</option>
            <option value="open">Open</option>
            <option value="closed">Closed</option>
          </select>
        </Field>
        <Field label="From">
          <input
            type="date"
            value={from}
            onChange={(e) => setFrom(e.target.value)}
            aria-label="Filter trades from date"
            className="bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
          />
        </Field>
        <Field label="To">
          <input
            type="date"
            value={to}
            onChange={(e) => setTo(e.target.value)}
            aria-label="Filter trades to date"
            className="bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
          />
        </Field>
        <Field label="Limit">
          <input
            type="number"
            min={10}
            max={500}
            step={10}
            value={limit}
            onChange={(e) => setLimit(parseInt(e.target.value, 10) || 100)}
            aria-label="Maximum number of trades to load"
            className="bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono w-20 text-right"
          />
        </Field>
        <button
          onClick={reset}
          className="px-3 py-1.5 rounded-md text-[10px] font-black uppercase tracking-widest text-muted-foreground hover:text-foreground hover:bg-white/[0.05] transition-colors"
        >
          Reset
        </button>
      </div>

      {tradesQ.isLoading ? (
        <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-3 space-y-2">
          {Array.from({ length: 8 }).map((_, i) => (
            <Skeleton key={i} className="h-9 w-full" />
          ))}
        </div>
      ) : trades.length === 0 ? (
        <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-12 text-center text-sm text-muted-foreground">
          No trades match the current filters.
        </div>
      ) : (
        <VirtualizedTradesTable trades={trades} />
      )}
    </div>
  );
}

function VirtualizedTradesTable({ trades }: { trades: TradeJournal[] }) {
  const parentRef = useRef<HTMLDivElement>(null);

  const virtualizer = useVirtualizer({
    count: trades.length,
    getScrollElement: () => parentRef.current,
    estimateSize: () => 44,
    overscan: 10,
  });

  const items = virtualizer.getVirtualItems();

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      {/* Header */}
      <div
        role="row"
        className="grid border-b border-white/5 text-[9px] uppercase tracking-[0.2em] text-muted-foreground/60 bg-[#0D1117]/80 sticky top-0 z-10"
        style={{ gridTemplateColumns: GRID_COLS }}
      >
        <Hcell>Exit Time</Hcell>
        <Hcell>Symbol</Hcell>
        <Hcell>Side</Hcell>
        <Hcell align="right">Entry</Hcell>
        <Hcell align="right">Exit</Hcell>
        <Hcell align="right">PnL</Hcell>
        <Hcell align="right">PnL%</Hcell>
        <Hcell>Strategy</Hcell>
        <Hcell>Reason</Hcell>
      </div>

      {/* Virtualized body */}
      <div
        ref={parentRef}
        role="rowgroup"
        aria-label={`${trades.length} closed trades`}
        className="overflow-auto"
        style={{ maxHeight: "70vh" }}
      >
        <div
          style={{
            height: `${virtualizer.getTotalSize()}px`,
            position: "relative",
            width: "100%",
          }}
        >
          {items.map((vi) => {
            const t = trades[vi.index];
            return (
              <div
                key={vi.key}
                data-index={vi.index}
                ref={virtualizer.measureElement}
                role="row"
                className="grid absolute left-0 right-0 border-b border-white/[0.02] hover:bg-white/[0.02] transition-colors text-sm"
                style={{
                  transform: `translateY(${vi.start}px)`,
                  gridTemplateColumns: GRID_COLS,
                }}
              >
                <Cell className="font-mono text-xs text-muted-foreground">
                  {formatDateTime(t.exitTime)}
                </Cell>
                <Cell className="gap-2 min-w-0">
                  <span className="font-bold truncate">{t.symbol}</span>
                  <StatusPill status={t.status} pnl={t.realizedPnL} />
                </Cell>
                <Cell>
                  <span
                    className={cn(
                      "inline-flex items-center rounded-full border px-2 py-0.5 text-[10px] font-mono font-black uppercase tracking-widest",
                      t.side === "LONG"
                        ? "bg-emerald-400/10 text-emerald-300 border-emerald-400/25"
                        : "bg-rose-400/10 text-rose-300 border-rose-400/25"
                    )}
                  >
                    {t.side === "LONG" ? "↑ Long" : "↓ Short"}
                  </span>
                </Cell>
                <Cell align="right" className="font-mono">{formatUsd(t.entryPrice)}</Cell>
                <Cell align="right" className="font-mono">{formatUsd(t.exitPrice)}</Cell>
                <Cell align="right" className={cn("font-mono font-bold", pnlColor(t.realizedPnL))}>
                  {formatUsd(t.realizedPnL)}
                </Cell>
                <Cell align="right" className={cn("font-mono", pnlColor(t.pnlPercent))}>
                  {formatPercentLoose(t.pnlPercent)}
                </Cell>
                <Cell>
                  <StrategyPill tag={t.strategyTag} />
                </Cell>
                <Cell className="text-[10px] text-muted-foreground/80 truncate">
                  {t.exitReason || "—"}
                </Cell>
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
}

function Hcell({ children, align = "left" }: { children: React.ReactNode; align?: "left" | "right" }) {
  return (
    <div
      role="columnheader"
      className={cn("px-3 py-3 font-black", align === "right" ? "text-right" : "text-left")}
    >
      {children}
    </div>
  );
}

function Cell({
  children,
  align = "left",
  className,
}: {
  children: React.ReactNode;
  align?: "left" | "right";
  className?: string;
}) {
  return (
    <div
      role="cell"
      className={cn(
        "px-3 py-3 flex items-center min-w-0",
        align === "right" && "justify-end",
        className
      )}
    >
      {children}
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-1">
      <span className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/50">
        {label}
      </span>
      {children}
    </div>
  );
}
