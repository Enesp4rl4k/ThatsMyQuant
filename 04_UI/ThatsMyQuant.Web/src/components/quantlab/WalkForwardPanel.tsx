import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { quantLabApi, type WalkForwardResult, type WalkForwardWindow } from "@/api/quantLab";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";

const TIMEFRAMES = ["15m", "1h", "4h", "1d"] as const;

/**
 * Walk-Forward Optimizer — runs N rolling train/test windows on historical
 * data and reports overfitting risk via WFE (Walk-Forward Efficiency).
 *
 *   WFE > 0.5  → robust strategy
 *   WFE 0.3-0.5 → marginal — extra validation needed
 *   WFE < 0.3  → likely overfit, do NOT promote to live
 */
export function WalkForwardPanel() {
  const [symbol, setSymbol] = useState("BTCUSDT");
  const [timeframe, setTimeframe] = useState<string>("1h");
  const [days, setDays] = useState(365);
  const [windows, setWindows] = useState(6);
  const [capital, setCapital] = useState(10_000);

  const m = useMutation({
    mutationFn: () =>
      quantLabApi.walkForward({ symbol, timeframe, days, numWindows: windows, capital }),
  });

  const r = m.data;

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      <div className="px-5 py-3 border-b border-white/5">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
          Walk-Forward Optimizer
        </p>
        <p className="text-[12px] font-serif italic text-muted-foreground/70">
          rolling 70/30 train-test splits · validates overfitting · WFE &gt; 0.5 = robust
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
              min={60}
              max={1095}
              step={30}
              value={days}
              onChange={(e) => setDays(parseInt(e.target.value, 10) || 365)}
              className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
            />
          </Field>
          <Field label="Windows">
            <input
              type="number"
              min={3}
              max={12}
              value={windows}
              onChange={(e) => setWindows(parseInt(e.target.value, 10) || 6)}
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
        </div>

        <button
          onClick={() => m.mutate()}
          disabled={m.isPending}
          className="w-full py-2.5 rounded-md bg-accent/15 text-accent border border-accent/30 hover:bg-accent/25 transition-colors text-[11px] font-black uppercase tracking-widest disabled:opacity-40"
        >
          {m.isPending ? "Running walk-forward (may take 30-60s)…" : "▶ Run Walk-Forward Analysis"}
        </button>

        {m.isError && (
          <p className="text-xs italic font-serif text-rose-400">
            Walk-forward failed: {(m.error as Error)?.message ?? "unknown error"}
          </p>
        )}
      </div>

      {/* Results */}
      {r && <Results result={r} />}
    </div>
  );
}

function Results({ result: r }: { result: WalkForwardResult }) {
  const wfeClass =
    r.walkForwardEfficiency >= 0.5
      ? "border-emerald-500/40 bg-emerald-500/[0.06]"
      : r.walkForwardEfficiency >= 0.3
        ? "border-amber-500/40 bg-amber-500/[0.06]"
        : "border-rose-500/40 bg-rose-500/[0.06]";

  const verdict =
    r.walkForwardEfficiency >= 0.5
      ? { label: "ROBUST", tone: "text-emerald-400" }
      : r.walkForwardEfficiency >= 0.3
        ? { label: "MARGINAL", tone: "text-amber-400" }
        : { label: "OVERFIT RISK", tone: "text-rose-400" };

  return (
    <div className="border-t border-white/5 p-5 space-y-4">
      {/* Headline verdict */}
      <div className={cn("rounded-lg border p-4 flex items-center justify-between gap-4", wfeClass)}>
        <div>
          <p className="text-[10px] uppercase tracking-[0.3em] text-muted-foreground/60 mb-1">
            Walk-Forward Efficiency
          </p>
          <p className={cn("font-mono text-[40px] font-black tabular-nums leading-none", verdict.tone)}>
            {(r.walkForwardEfficiency * 100).toFixed(0)}%
          </p>
          <p className={cn("text-[11px] font-black uppercase tracking-widest mt-1", verdict.tone)}>
            ● {verdict.label}
          </p>
        </div>
        <div className="text-right text-[10px] font-mono">
          <p className="text-muted-foreground/60">Train Sharpe</p>
          <p className="font-bold text-[14px]">{r.aggregateTrainSharpe.toFixed(2)}</p>
          <p className="text-muted-foreground/60 mt-1.5">OOS Sharpe</p>
          <p className={cn(
            "font-bold text-[14px]",
            r.aggregateOosSharpe >= 1 ? "text-emerald-400" : r.aggregateOosSharpe >= 0 ? "text-amber-400" : "text-rose-400"
          )}>
            {r.aggregateOosSharpe.toFixed(2)}
          </p>
        </div>
      </div>

      {/* Aggregate stats */}
      <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
        <Stat label="OOS P&L" value={formatUsd(r.aggregateOosPnL)} tone={r.aggregateOosPnL >= 0 ? "pos" : "neg"} />
        <Stat label="OOS WR" value={`${(r.aggregateOosWinRate * 100).toFixed(1)}%`} tone={r.aggregateOosWinRate >= 0.5 ? "pos" : "neg"} />
        <Stat label="Consistency" value={`${(r.oosConsistency * 100).toFixed(0)}%`} tone={r.oosConsistency >= 0.6 ? "pos" : r.oosConsistency >= 0.4 ? "warn" : "neg"} />
        <Stat
          label="PF Drift"
          value={`${(r.profitFactorDegradation * 100).toFixed(0)}%`}
          tone={r.profitFactorDegradation < 0.3 ? "pos" : r.profitFactorDegradation < 0.5 ? "warn" : "neg"}
        />
      </div>

      {/* Per-window table */}
      <div className="pt-3 border-t border-white/5">
        <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/50 mb-2">
          {r.completedWindows} / {r.totalWindows} windows completed
        </p>
        <div className="overflow-x-auto">
          <table className="w-full text-[10px] font-mono">
            <thead className="text-muted-foreground/60">
              <tr className="border-b border-white/5">
                <th className="text-left py-1.5 px-2">#</th>
                <th className="text-left py-1.5 px-2">OOS Period</th>
                <th className="text-right py-1.5 px-2">Train Sh</th>
                <th className="text-right py-1.5 px-2">OOS Sh</th>
                <th className="text-right py-1.5 px-2">OOS P&L</th>
                <th className="text-right py-1.5 px-2">OOS WR</th>
                <th className="text-right py-1.5 px-2">OOS DD</th>
                <th className="text-left py-1.5 px-2">Regime</th>
              </tr>
            </thead>
            <tbody>
              {r.windows.map((w) => (
                <WindowRow key={w.windowIndex} window={w} />
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}

function WindowRow({ window: w }: { window: WalkForwardWindow }) {
  const period = `${w.oosStartDate.slice(0, 10)} → ${w.oosEndDate.slice(0, 10)}`;
  return (
    <tr className="border-b border-white/[0.04] hover:bg-white/[0.02]">
      <td className="py-1.5 px-2 font-bold">{w.windowIndex + 1}</td>
      <td className="py-1.5 px-2 text-muted-foreground/70">{period}</td>
      <td className="py-1.5 px-2 text-right tabular-nums">{w.trainSharpe.toFixed(2)}</td>
      <td className={cn(
        "py-1.5 px-2 text-right tabular-nums font-bold",
        w.oosSharpe >= 1 ? "text-emerald-400" : w.oosSharpe >= 0 ? "text-amber-400" : "text-rose-400"
      )}>{w.oosSharpe.toFixed(2)}</td>
      <td className={cn("py-1.5 px-2 text-right tabular-nums", w.oosPnL >= 0 ? "text-emerald-400" : "text-rose-400")}>
        {formatUsd(w.oosPnL)}
      </td>
      <td className="py-1.5 px-2 text-right tabular-nums">{(w.oosWinRate * 100).toFixed(0)}%</td>
      <td className="py-1.5 px-2 text-right tabular-nums text-rose-400">{(w.oosMaxDD * 100).toFixed(2)}%</td>
      <td className="py-1.5 px-2 text-muted-foreground/70 text-[9px] uppercase tracking-widest">
        {w.oosRegime}
      </td>
    </tr>
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
      <p className={cn(
        "font-mono font-black tabular-nums text-[14px] mt-0.5",
        tone === "pos" && "text-emerald-400",
        tone === "warn" && "text-amber-400",
        tone === "neg" && "text-rose-400"
      )}>
        {value}
      </p>
    </div>
  );
}
