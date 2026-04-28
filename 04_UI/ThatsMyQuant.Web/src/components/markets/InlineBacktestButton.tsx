import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { quantLabApi, type SingleBacktestResult } from "@/api/quantLab";
import { LineChart } from "@/components/charts/LineChart";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";

interface Props {
  symbol: string;
  timeframe: string;
}

/**
 * "Run a quick backtest right here" button. Sits in the Trade Mode toolbar.
 * Click → opens a small modal with knobs (days/capital/leverage) → fires
 * /api/quant-lab/single-backtest → renders the result inline (sharpe, PF,
 * WR, equity curve) so the operator can sanity-check a strategy without
 * navigating to Quant Lab.
 *
 * Defaults to 90 days, 10k capital, 3x leverage (mirrors paper config).
 */
export function InlineBacktestButton({ symbol, timeframe }: Props) {
  const [open, setOpen] = useState(false);
  const [days, setDays] = useState(90);
  const [capital, setCapital] = useState(10_000);
  const [leverage, setLeverage] = useState(3);

  const m = useMutation({
    mutationFn: () => quantLabApi.singleBacktest({ symbol, timeframe, days, startingCapital: capital, leverage }),
  });

  return (
    <>
      <button
        onClick={() => setOpen(true)}
        className="px-2 py-0.5 rounded border border-violet-400/30 bg-violet-500/10 text-violet-300 text-[10px] font-mono uppercase tracking-widest hover:bg-violet-500/20"
        title="Quick-backtest the current symbol with default strategy stack"
      >
        🧪 Backtest
      </button>

      {open && (
        <div
          className="fixed inset-0 z-[300] flex items-center justify-center bg-black/80 backdrop-blur-sm p-6"
          onClick={() => setOpen(false)}
          role="dialog"
          aria-modal="true"
        >
          <div
            onClick={(e) => e.stopPropagation()}
            className="w-full max-w-2xl rounded-2xl border border-white/10 bg-[#0D1117]/95 p-5 shadow-2xl"
          >
            <div className="flex items-baseline justify-between mb-4">
              <div>
                <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
                  🧪 Inline Backtest · {symbol} {timeframe}
                </p>
                <p className="text-[11px] font-serif italic text-muted-foreground/65 mt-0.5">
                  Quick check before you commit to a position. Same engine as Quant Lab.
                </p>
              </div>
              <button onClick={() => setOpen(false)} className="text-muted-foreground/55 hover:text-foreground text-xl leading-none">×</button>
            </div>

            {/* Inputs */}
            <div className="grid grid-cols-3 gap-2 mb-3">
              <Field label="Days">
                <input type="number" min={30} max={1095} step={30} value={days} onChange={(e) => setDays(parseInt(e.target.value, 10) || 90)} className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1 text-[11px] font-mono" />
              </Field>
              <Field label="Capital">
                <input type="number" min={1000} step={1000} value={capital} onChange={(e) => setCapital(parseInt(e.target.value, 10) || 10_000)} className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1 text-[11px] font-mono" />
              </Field>
              <Field label="Leverage">
                <input type="number" min={1} max={15} step={0.5} value={leverage} onChange={(e) => setLeverage(parseFloat(e.target.value) || 1)} className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1 text-[11px] font-mono" />
              </Field>
            </div>

            <button
              onClick={() => m.mutate()}
              disabled={m.isPending}
              className="w-full py-1.5 rounded-md bg-violet-500/20 text-violet-200 border border-violet-400/40 text-[11px] font-black uppercase tracking-widest hover:bg-violet-500/30 disabled:opacity-40 mb-3"
            >
              {m.isPending ? "Running…" : "▶ Run Backtest"}
            </button>

            {m.isError && (
              <p className="text-xs italic font-serif text-rose-400">Failed: {(m.error as Error)?.message ?? "unknown"}</p>
            )}

            {m.data && <Result r={m.data} />}
          </div>
        </div>
      )}
    </>
  );
}

function Result({ r }: { r: SingleBacktestResult }) {
  return (
    <div className="space-y-3">
      <div className="grid grid-cols-3 sm:grid-cols-6 gap-2">
        <StatBox label="Trades" value={r.totalTrades.toString()} />
        <StatBox label="Win Rate" value={`${(r.winRate * 100).toFixed(1)}%`} tone={r.winRate >= 0.5 ? "pos" : "neg"} />
        <StatBox label="Sharpe" value={r.sharpe.toFixed(2)} tone={r.sharpe >= 1 ? "pos" : r.sharpe >= 0 ? "neutral" : "neg"} />
        <StatBox label="PF" value={r.profitFactor.toFixed(2)} tone={r.profitFactor >= 1.2 ? "pos" : r.profitFactor >= 1 ? "neutral" : "neg"} />
        <StatBox label="P&L" value={formatUsd(r.totalPnL)} tone={r.totalPnL >= 0 ? "pos" : "neg"} />
        <StatBox label="DD %" value={`${r.maxDrawdownPercent.toFixed(2)}%`} tone={r.maxDrawdownPercent < 5 ? "pos" : r.maxDrawdownPercent < 10 ? "warn" : "neg"} />
      </div>
      {r.equityCurve.length > 1 && (
        <div className="border-t border-white/5 pt-3">
          <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/55 mb-1.5">Equity curve</p>
          <LineChart values={r.equityCurve} width={520} height={140} gradient />
        </div>
      )}
      {r.reasoning && (
        <p className="text-[11px] font-serif italic text-muted-foreground/75 leading-relaxed border-t border-white/5 pt-2">
          {r.reasoning}
        </p>
      )}
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <span className="text-[8px] font-black uppercase tracking-widest text-muted-foreground/45 mb-0.5 block">{label}</span>
      {children}
    </label>
  );
}

function StatBox({ label, value, tone = "neutral" }: { label: string; value: string; tone?: "pos" | "neg" | "warn" | "neutral" }) {
  return (
    <div className="rounded-lg bg-white/[0.02] border border-white/5 px-2.5 py-1.5">
      <p className="text-[8px] uppercase tracking-widest text-muted-foreground/45">{label}</p>
      <p
        className={cn(
          "font-mono font-black tabular-nums text-[12px] mt-0.5",
          tone === "pos" && "text-emerald-300",
          tone === "warn" && "text-amber-300",
          tone === "neg" && "text-rose-300"
        )}
      >
        {value}
      </p>
    </div>
  );
}
