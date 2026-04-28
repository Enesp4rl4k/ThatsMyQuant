import { useEffect } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { getHubConnection } from "@/lib/signalr";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";

/**
 * Live paper-portfolio summary card. Reads `/api/paper/portfolio` (which
 * surfaces the JSON snapshot at `paper_portfolio.json`) and force-refreshes
 * when SignalR pushes `PositionOpened` / `PositionClosed` so the dashboard
 * reflects each trade open/close within ~1 second.
 *
 * Bridges the architectural gap where hybrid executors don't write to the
 * Trade table: this is the Worker's authoritative paper-portfolio snapshot.
 */
export function PaperPortfolioCard() {
  const qc = useQueryClient();
  const q = useQuery({
    queryKey: ["paper-portfolio"],
    queryFn:  dashboardApi.paperPortfolio,
    refetchInterval: 10_000,
  });

  // Listen for live position events → invalidate immediately
  useEffect(() => {
    const conn = getHubConnection();
    const refresh = () => {
      qc.invalidateQueries({ queryKey: ["paper-portfolio"] });
      qc.invalidateQueries({ queryKey: ["paper-state-positions"] });
      qc.invalidateQueries({ queryKey: ["exchange-positions"] });
    };
    conn.on("PositionOpened", refresh);
    conn.on("PositionClosed", refresh);
    return () => {
      conn.off("PositionOpened", refresh);
      conn.off("PositionClosed", refresh);
    };
  }, [qc]);

  const p = q.data;
  if (!p) {
    return (
      <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-2">
          💼 Paper Portfolio
        </p>
        <p className="text-[11px] italic font-serif text-muted-foreground/55 text-center py-3">
          — loading snapshot —
        </p>
      </div>
    );
  }

  if (!p.found) {
    return (
      <div className="rounded-2xl border border-amber-400/20 bg-amber-500/[0.03] backdrop-blur-xl p-4">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-amber-400/80 mb-2">
          💼 Paper Portfolio · snapshot pending
        </p>
        <p className="text-[10px] font-mono text-muted-foreground/65 leading-relaxed">
          {p.note ?? "Worker hasn't written paper_portfolio.json yet."}
        </p>
        <p className="text-[9px] font-mono text-muted-foreground/40 mt-2">
          Path: <code>{shortPath(p.filePath)}</code>
        </p>
      </div>
    );
  }

  const pnlTone = p.totalPnL >= 0 ? "text-emerald-300" : "text-rose-300";
  const dailyTone = p.dailyPnL > 0 ? "text-emerald-300" : p.dailyPnL < 0 ? "text-rose-300" : "text-muted-foreground/65";
  const wrTone = p.winRate >= 55 ? "text-emerald-300" : p.winRate >= 45 ? "text-amber-300" : "text-rose-300";

  return (
    <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
      <div className="flex items-baseline justify-between mb-3 flex-wrap gap-2">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
            💼 Paper Portfolio · live
          </p>
          <p className="text-[10px] font-mono text-muted-foreground/55 mt-0.5">
            Worker snapshot · {p.lastWriteUtc ? `updated ${fmtRel(p.lastWriteUtc)}` : "unknown age"}
          </p>
        </div>
        <span className="text-[9px] font-mono text-muted-foreground/45">
          SignalR live · auto-refresh on Position events
        </span>
      </div>

      {/* Big metrics */}
      <div className="grid grid-cols-2 sm:grid-cols-4 gap-3 mb-3">
        <Big label="Total Equity"  value={formatUsd(p.totalEquity)} tone="text-foreground/90" />
        <Big label="Total P&L"     value={`${p.totalPnL >= 0 ? "+" : ""}${formatUsd(p.totalPnL)}`} sub={`${p.totalPnLPercent >= 0 ? "+" : ""}${p.totalPnLPercent.toFixed(2)}%`} tone={pnlTone} />
        <Big label="Today's P&L"   value={`${p.dailyPnL >= 0 ? "+" : ""}${formatUsd(p.dailyPnL)}`} tone={dailyTone} />
        <Big label="Cash"          value={formatUsd(p.cash)} sub={`init ${formatUsd(p.initialBalance)}`} tone="text-foreground/85" />
      </div>

      {/* Trade-stats row */}
      <div className="grid grid-cols-3 gap-2 text-[10px] font-mono pt-3 border-t border-white/[0.04]">
        <Stat label="Open"   value={p.openCount.toString()} />
        <Stat label="Closed" value={p.closedCount.toString()} sub={`${p.winningTrades} wins`} />
        <Stat label="WR"     value={p.closedCount > 0 ? `${p.winRate.toFixed(1)}%` : "—"} tone={wrTone} />
      </div>
    </div>
  );
}

function Big({ label, value, sub, tone }: { label: string; value: string; sub?: string; tone: string }) {
  return (
    <div>
      <p className="text-[8px] uppercase tracking-widest text-muted-foreground/45 mb-0.5">{label}</p>
      <p className={cn("font-mono font-black tabular-nums text-[18px] leading-none", tone)}>{value}</p>
      {sub && <p className="text-[9px] font-mono text-muted-foreground/55 mt-1">{sub}</p>}
    </div>
  );
}

function Stat({ label, value, sub, tone = "text-foreground/85" }: { label: string; value: string; sub?: string; tone?: string }) {
  return (
    <div className="text-center">
      <p className="text-[8px] uppercase tracking-widest text-muted-foreground/45 mb-0.5">{label}</p>
      <p className={cn("font-bold tabular-nums", tone)}>{value}</p>
      {sub && <p className="text-[8px] font-mono text-muted-foreground/45 mt-0.5">{sub}</p>}
    </div>
  );
}

function fmtRel(iso: string): string {
  try {
    const d = new Date(iso);
    const ago = (Date.now() - d.getTime()) / 1000;
    if (ago < 30) return "now";
    if (ago < 60) return `${Math.floor(ago)}s ago`;
    if (ago < 3600) return `${Math.floor(ago / 60)}m ago`;
    return d.toLocaleString();
  } catch { return ""; }
}

function shortPath(p: string): string {
  const parts = p.split(/[\\/]/);
  return parts.length > 3 ? `…/${parts.slice(-2).join("/")}` : p;
}
