import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { quantLabApi } from "@/api/quantLab";
import { LineChart } from "@/components/charts/LineChart";
import { BarChart } from "@/components/charts/BarChart";
import { Scatter } from "@/components/charts/Scatter";
import { SingleBacktestPanel } from "@/components/quantlab/SingleBacktestPanel";
import { WalkForwardPanel } from "@/components/quantlab/WalkForwardPanel";
import { CompareBacktestPanel } from "@/components/quantlab/CompareBacktestPanel";
import { cn } from "@/lib/utils";
import { formatPercentLoose, formatUsd, pnlColor } from "@/lib/format";

export function QuantLabPage() {
  const [strategy, setStrategy] = useState<string | undefined>(undefined);

  const stratsQ = useQuery({
    queryKey: ["quant-lab-strategies"],
    queryFn: quantLabApi.strategies,
    staleTime: 60_000,
  });

  const metricsQ = useQuery({
    queryKey: ["quant-lab-metrics", strategy],
    queryFn: () => quantLabApi.metrics(strategy),
    refetchInterval: 30_000,
  });

  const m = metricsQ.data;

  return (
    <div className="space-y-8">
      <div className="relative">
        <div className="absolute -left-4 top-0 bottom-0 w-1 bg-accent shadow-[0_0_15px_rgba(0,191,255,0.5)]" />
        <h2 className="text-[10px] font-black uppercase tracking-[0.4em] text-accent mb-1 ml-2">
          Performance Diagnostics
        </h2>
        <p className="text-2xl font-black tracking-tight ml-2">
          Quant Lab{" "}
          <span className="text-muted-foreground/30 font-light">
            — Rolling Sharpe · Return distribution · Drawdown · MFE/MAE
          </span>
        </p>
      </div>

      <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4 flex flex-wrap items-center gap-3">
        <span className="text-[10px] font-black uppercase tracking-widest text-muted-foreground/60">
          Strategy:
        </span>
        <button
          onClick={() => setStrategy(undefined)}
          className={cn(
            "px-3 py-1.5 rounded text-[11px] font-bold uppercase tracking-wider border",
            !strategy ? "bg-accent/15 text-accent border-accent/40" : "bg-white/[0.02] text-muted-foreground border-white/5 hover:text-foreground"
          )}
        >
          ALL
        </button>
        {(stratsQ.data ?? []).map((s) => (
          <button
            key={s.strategyTag}
            onClick={() => setStrategy(s.strategyTag)}
            className={cn(
              "px-3 py-1.5 rounded text-[11px] font-bold uppercase tracking-wider border",
              strategy === s.strategyTag
                ? "bg-accent/15 text-accent border-accent/40"
                : "bg-white/[0.02] text-muted-foreground border-white/5 hover:text-foreground"
            )}
          >
            {s.strategyTag} <span className="opacity-60">({s.tradeCount})</span>
          </button>
        ))}
      </div>

      {m && m.summary.tradeCount > 0 && (
        <div className="grid grid-cols-2 md:grid-cols-4 lg:grid-cols-7 gap-3">
          <SummaryCard label="Trades" value={m.summary.tradeCount.toString()} />
          <SummaryCard
            label="Total PnL"
            value={formatUsd(m.summary.totalPnL)}
            valueClass={pnlColor(m.summary.totalPnL)}
          />
          <SummaryCard label="Sharpe" value={m.summary.sharpeAllTime.toFixed(2)} valueClass="text-accent" />
          <SummaryCard label="Max DD" value={formatPercentLoose(m.summary.maxDrawdown)} valueClass="text-rose-400" />
          <SummaryCard label="VaR 95" value={formatPercentLoose(m.summary.var95)} valueClass="text-rose-400" />
          <SummaryCard label="CVaR 95" value={formatPercentLoose(m.summary.cVaR95)} valueClass="text-rose-400" />
          <SummaryCard label="Mean Ret" value={formatPercentLoose(m.summary.meanReturn)} />
        </div>
      )}

      {!m || m.summary.tradeCount === 0 ? (
        <p className="text-center text-sm text-muted-foreground py-12">
          No closed trades available for the selected scope.
        </p>
      ) : (
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
          <ChartCard title="Rolling Sharpe (20-trade window)" eyebrow="Risk-adjusted Edge">
            <LineChart
              values={m.rollingSharpe.map((p) => p.sharpe)}
              width={520}
              height={240}
              stroke="rgba(30, 58, 138, 0.95)"
              fill="rgba(30, 58, 138, 0.06)"
              zeroLine
              yLabelFormatter={(v) => v.toFixed(2)}
            />
          </ChartCard>

          <ChartCard title="Return Distribution" eyebrow={`VaR95 ${formatPercentLoose(m.summary.var95)} · CVaR95 ${formatPercentLoose(m.summary.cVaR95)}`}>
            <BarChart
              bins={m.returnDistribution.map((b) => ({
                binStart: b.binStart,
                binEnd: b.binEnd,
                count: b.count,
              }))}
              width={520}
              height={240}
              highlightBelow={m.summary.var95}
            />
          </ChartCard>

          <ChartCard title="Drawdown Curve" eyebrow="Cumulative PnL underwater">
            <LineChart
              values={m.drawdownCurve.map((p) => p.drawdown * 100)}
              width={520}
              height={240}
              stroke="rgba(244, 63, 94, 0.95)"
              fill="rgba(244, 63, 94, 0.07)"
              zeroLine
              yLabelFormatter={(v) => `${v.toFixed(1)}%`}
            />
          </ChartCard>

          <ChartCard title="MFE / MAE Scatter" eyebrow="Per-trade excursion">
            <Scatter
              points={m.mfeMaeScatter.map((d) => ({ x: d.mfe, y: d.mae, pnl: d.pnl, label: d.symbol }))}
              width={520}
              height={280}
              xLabel="Max Favorable Excursion %"
              yLabel="Max Adverse Excursion %"
            />
          </ChartCard>
        </div>
      )}

      {/* Research panels — single-strategy backtest + walk-forward optimization */}
      <div className="pt-4 border-t border-white/5 space-y-4">
        <div className="flex items-baseline justify-between gap-4 flex-wrap">
          <div>
            <p className="text-[10px] font-black uppercase tracking-[0.4em] text-accent/80 mb-1">
              Strategy Research
            </p>
            <p className="font-serif text-[18px] tracking-tight">
              Backtest & Walk-Forward
            </p>
          </div>
          <p className="text-[11px] font-serif italic text-muted-foreground/60 max-w-md">
            Same engine the WPF Backtest Lab uses — full per-bar event-driven
            simulation. Run a single backtest to validate, walk-forward to detect
            overfitting before promoting to paper.
          </p>
        </div>
        <SingleBacktestPanel />
        <CompareBacktestPanel />
        <WalkForwardPanel />
      </div>
    </div>
  );
}

function SummaryCard({ label, value, valueClass = "" }: { label: string; value: string; valueClass?: string }) {
  return (
    <div className="rounded-lg border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl px-4 py-3">
      <p className="text-[9px] font-black uppercase tracking-[0.25em] text-muted-foreground/50 mb-1">{label}</p>
      <p className={cn("font-mono text-lg font-bold leading-none", valueClass)}>{value}</p>
    </div>
  );
}

function ChartCard({ title, eyebrow, children }: { title: string; eyebrow: string; children: React.ReactNode }) {
  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5">
      <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/80 mb-1">{eyebrow}</p>
      <p className="text-sm font-bold mb-4">{title}</p>
      {children}
    </div>
  );
}
