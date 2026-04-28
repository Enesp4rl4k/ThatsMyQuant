import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { quantLabApi, type SingleBacktestResult } from "@/api/quantLab";
import { LineChart } from "@/components/charts/LineChart";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";

const TIMEFRAMES = ["5m", "15m", "1h", "4h", "1d"] as const;

/**
 * Single-symbol backtest with default strategy stack. Calls the same
 * BacktestEngineApp the WPF Backtest Lab uses — full per-bar event-driven
 * simulation across the configured period.
 */
export function SingleBacktestPanel() {
  const [symbol, setSymbol] = useState("BTCUSDT");
  const [timeframe, setTimeframe] = useState<string>("1h");
  const [days, setDays] = useState(180);
  const [capital, setCapital] = useState(10_000);
  const [leverage, setLeverage] = useState(3);

  const m = useMutation({
    mutationFn: () =>
      quantLabApi.singleBacktest({ symbol, timeframe, days, startingCapital: capital, leverage }),
  });

  const result = m.data;

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      <div className="px-5 py-3 border-b border-white/5">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
          Single-strategy backtest
        </p>
        <p className="text-[12px] font-serif italic text-muted-foreground/70">
          per-bar event-driven simulation · same engine as WPF Backtest Lab
        </p>
      </div>

      {/* Inputs */}
      <div className="p-5 space-y-4">
        <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-5 gap-3">
          <Field label="Symbol">
            <input
              value={symbol}
              onChange={(e) => setSymbol(e.target.value.toUpperCase())}
              className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
            />
          </Field>
          <Field label="Timeframe">
            <select
              value={timeframe}
              onChange={(e) => setTimeframe(e.target.value)}
              className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
            >
              {TIMEFRAMES.map((tf) => (
                <option key={tf} value={tf}>
                  {tf}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Days">
            <input
              type="number"
              min={30}
              max={1095}
              step={30}
              value={days}
              onChange={(e) => setDays(parseInt(e.target.value, 10) || 180)}
              className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
            />
          </Field>
          <Field label="Capital">
            <input
              type="number"
              min={1000}
              step={1000}
              value={capital}
              onChange={(e) => setCapital(parseInt(e.target.value, 10) || 10_000)}
              className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
            />
          </Field>
          <Field label="Leverage">
            <input
              type="number"
              min={1}
              max={15}
              step={0.5}
              value={leverage}
              onChange={(e) => setLeverage(parseFloat(e.target.value) || 1)}
              className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
            />
          </Field>
        </div>

        <button
          onClick={() => m.mutate()}
          disabled={m.isPending}
          className="w-full py-2.5 rounded-md bg-accent/15 text-accent border border-accent/30 hover:bg-accent/25 transition-colors text-[11px] font-black uppercase tracking-widest disabled:opacity-40"
        >
          {m.isPending ? "Running backtest…" : "▶ Run Backtest"}
        </button>

        {m.isError && (
          <p className="text-xs italic font-serif text-rose-400">
            Backtest failed: {(m.error as Error)?.message ?? "unknown error"}
          </p>
        )}
      </div>

      {/* Results */}
      {result && <Results result={result} />}
    </div>
  );
}

function Results({ result: r }: { result: SingleBacktestResult }) {
  return (
    <div className="border-t border-white/5 p-5 space-y-4">
      <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-6 gap-3">
        <Stat label="Trades" value={r.totalTrades.toString()} />
        <Stat label="Win Rate" value={`${(r.winRate * 100).toFixed(1)}%`} tone={r.winRate >= 0.5 ? "pos" : "neg"} />
        <Stat label="P&L" value={formatUsd(r.totalPnL)} tone={r.totalPnL >= 0 ? "pos" : "neg"} />
        <Stat
          label="Sharpe"
          value={r.sharpe.toFixed(2)}
          tone={r.sharpe >= 1 ? "pos" : r.sharpe >= 0 ? "neutral" : "neg"}
        />
        <Stat
          label="PF"
          value={r.profitFactor.toFixed(2)}
          tone={r.profitFactor >= 1.2 ? "pos" : r.profitFactor >= 1 ? "neutral" : "neg"}
        />
        <Stat
          label="Max DD"
          value={`${r.maxDrawdownPercent.toFixed(2)}%`}
          tone={r.maxDrawdownPercent < 5 ? "pos" : r.maxDrawdownPercent < 10 ? "warn" : "neg"}
        />
      </div>

      {r.equityCurve.length > 1 && (
        <div className="pt-3 border-t border-white/5">
          <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/50 mb-2">
            Equity Curve
          </p>
          <LineChart values={r.equityCurve} width={920} height={200} gradient />
        </div>
      )}

      {r.reasoning && (
        <div className="pt-3 border-t border-white/5">
          <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/50 mb-1">
            Engine Notes
          </p>
          <p className="text-[12px] font-serif italic text-muted-foreground/80 leading-relaxed">
            {r.reasoning}
          </p>
        </div>
      )}

      {Object.keys(r.entryRejectReasons).length > 0 && (
        <div className="pt-3 border-t border-white/5">
          <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/50 mb-1.5">
            Entry rejection breakdown
          </p>
          <div className="flex flex-wrap gap-2">
            {Object.entries(r.entryRejectReasons)
              .sort(([, a], [, b]) => b - a)
              .slice(0, 8)
              .map(([reason, count]) => (
                <span
                  key={reason}
                  className="text-[10px] font-mono px-2 py-1 rounded border border-white/10 bg-white/[0.02]"
                >
                  <span className="text-muted-foreground/60">{reason}:</span>{" "}
                  <span className="font-bold">{count}</span>
                </span>
              ))}
          </div>
        </div>
      )}
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <span className="text-[8px] font-black uppercase tracking-widest text-muted-foreground/50 mb-0.5 block">
        {label}
      </span>
      {children}
    </label>
  );
}

function Stat({
  label,
  value,
  tone = "neutral",
}: {
  label: string;
  value: string;
  tone?: "pos" | "neg" | "warn" | "neutral";
}) {
  return (
    <div className="rounded-lg bg-white/[0.02] border border-white/5 px-3 py-2">
      <p className="text-[8px] font-black uppercase tracking-widest text-muted-foreground/45">{label}</p>
      <p
        className={cn(
          "font-mono font-black tabular-nums text-[14px] mt-0.5",
          tone === "pos" && "text-emerald-400",
          tone === "warn" && "text-amber-400",
          tone === "neg" && "text-rose-400"
        )}
      >
        {value}
      </p>
    </div>
  );
}
