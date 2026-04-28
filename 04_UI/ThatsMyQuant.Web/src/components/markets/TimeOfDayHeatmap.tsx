import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import type { TradeJournal } from "@/api/types";
import { cn } from "@/lib/utils";

interface Props {
  symbol: string;
}

type Mode = "winrate" | "pnl" | "trades";

const DAYS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

interface Cell {
  trades: number;
  wins: number;
  pnl: number;
}

/**
 * 7×24 heatmap — rows = day of week, cols = UTC hour. Cell color encodes
 * either win-rate, total P&L, or trade count for trades on this symbol.
 * Uses the trade exit time as the "when" anchor.
 */
export function TimeOfDayHeatmap({ symbol }: Props) {
  const [mode, setMode] = useState<Mode>("winrate");

  const q = useQuery({
    queryKey: ["trades", "filtered", { limit: 500 }],
    queryFn: () => dashboardApi.filteredTrades({ limit: 500 }),
    refetchInterval: 60_000,
  });

  const grid = useMemo(() => {
    const cells: Cell[][] = Array.from({ length: 7 }, () =>
      Array.from({ length: 24 }, () => ({ trades: 0, wins: 0, pnl: 0 }))
    );

    const list = (q.data ?? []).filter(
      (t: TradeJournal) => t.symbol === symbol && t.exitTime != null
    );

    for (const t of list) {
      const d = new Date(t.exitTime!);
      if (Number.isNaN(d.getTime())) continue;
      const day = d.getUTCDay();
      const hour = d.getUTCHours();
      cells[day][hour].trades += 1;
      if (t.realizedPnL > 0) cells[day][hour].wins += 1;
      cells[day][hour].pnl += t.realizedPnL;
    }

    return cells;
  }, [q.data, symbol]);

  const maxAbsValue = useMemo(() => {
    let max = 0;
    for (const row of grid) {
      for (const c of row) {
        const v =
          mode === "winrate"
            ? c.trades > 0
              ? c.wins / c.trades
              : 0
            : mode === "pnl"
              ? c.pnl
              : c.trades;
        if (Math.abs(v) > max) max = Math.abs(v);
      }
    }
    return max || 1;
  }, [grid, mode]);

  const totalTrades = useMemo(
    () => grid.reduce((s, row) => s + row.reduce((s2, c) => s2 + c.trades, 0), 0),
    [grid]
  );

  const colorFor = (cell: Cell): string => {
    if (cell.trades === 0) return "rgba(31, 30, 27, 0.04)";
    if (mode === "trades") {
      const intensity = cell.trades / maxAbsValue;
      return `rgba(30, 58, 138, ${0.18 + intensity * 0.6})`;
    }
    if (mode === "winrate") {
      const wr = cell.wins / cell.trades;
      const intensity = Math.abs(wr - 0.5) * 2;
      const color = wr >= 0.5 ? "45, 106, 79" : "181, 54, 75";
      return `rgba(${color}, ${0.18 + intensity * 0.6})`;
    }
    // pnl
    const intensity = Math.min(1, Math.abs(cell.pnl) / maxAbsValue);
    const color = cell.pnl >= 0 ? "45, 106, 79" : "181, 54, 75";
    return `rgba(${color}, ${0.18 + intensity * 0.65})`;
  };

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      <div className="px-4 py-2.5 border-b border-white/5 flex items-baseline justify-between gap-3">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
            {symbol} Time-of-Day
          </p>
          <p className="text-[11px] font-serif italic text-muted-foreground/60">
            UTC hour × weekday · {totalTrades} trade{totalTrades !== 1 ? "s" : ""}
          </p>
        </div>
        <div className="flex items-center gap-1">
          {(["winrate", "pnl", "trades"] as Mode[]).map((m) => (
            <button
              key={m}
              onClick={() => setMode(m)}
              aria-pressed={mode === m}
              className={cn(
                "px-2 py-0.5 rounded text-[9px] font-mono uppercase tracking-widest border transition-colors",
                mode === m
                  ? "bg-accent/15 text-accent border-accent/30"
                  : "border-white/10 text-muted-foreground/60 hover:text-foreground"
              )}
            >
              {m === "winrate" ? "WR" : m === "pnl" ? "P&L" : "Trades"}
            </button>
          ))}
        </div>
      </div>

      <div className="p-3 overflow-x-auto">
        {totalTrades === 0 ? (
          <p className="p-6 text-center text-xs italic font-serif text-muted-foreground/60">
            — no closed trades yet on {symbol} —
          </p>
        ) : (
          <div className="inline-block">
            {/* Hour header */}
            <div className="flex pl-10">
              {Array.from({ length: 24 }).map((_, h) => (
                <div
                  key={h}
                  className="w-5 text-center text-[8px] font-mono text-muted-foreground/40"
                >
                  {h % 3 === 0 ? h : ""}
                </div>
              ))}
            </div>
            {/* Grid */}
            {grid.map((row, day) => (
              <div key={day} className="flex items-center mt-0.5">
                <span className="w-10 text-[9px] font-mono uppercase tracking-widest text-muted-foreground/50 mr-0">
                  {DAYS[day]}
                </span>
                {row.map((cell, hour) => (
                  <div
                    key={hour}
                    className="w-5 h-5 rounded-sm cursor-help transition-transform hover:scale-110"
                    style={{
                      backgroundColor: colorFor(cell),
                      border:
                        cell.trades > 0
                          ? "1px solid rgba(31,30,27,0.06)"
                          : "1px solid transparent",
                    }}
                    title={
                      cell.trades === 0
                        ? `${DAYS[day]} ${hour}:00 — no trades`
                        : `${DAYS[day]} ${hour}:00 UTC · ${cell.trades} trade${cell.trades !== 1 ? "s" : ""} · WR ${((cell.wins / cell.trades) * 100).toFixed(0)}% · P&L $${cell.pnl.toFixed(2)}`
                    }
                  />
                ))}
              </div>
            ))}

            {/* Legend */}
            <div className="mt-3 flex items-center gap-3 pl-10 text-[9px] font-mono text-muted-foreground/50">
              <span>cool</span>
              <div className="flex gap-px">
                {[0.2, 0.35, 0.5, 0.65, 0.8].map((i) => (
                  <div
                    key={i}
                    className="w-3 h-3 rounded-sm"
                    style={{
                      backgroundColor:
                        mode === "trades"
                          ? `rgba(30, 58, 138, ${i})`
                          : `rgba(45, 106, 79, ${i})`,
                    }}
                  />
                ))}
              </div>
              <span>hot</span>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
