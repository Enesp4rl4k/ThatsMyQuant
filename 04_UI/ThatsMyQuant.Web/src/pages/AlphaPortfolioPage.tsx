import { useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { alphaApi } from "@/api/alpha";
import type { BacktestResponse, ValidatedPosition } from "@/api/alpha";
import { LineChart } from "@/components/charts/LineChart";
import { MonteCarloFanChart } from "@/components/charts/MonteCarloFanChart";
import { ReplayChart } from "@/components/charts/ReplayChart";
import { PromotionRadarCard } from "@/components/alpha/PromotionRadarCard";
import { MlStatusCard } from "@/components/alpha/MlStatusCard";
import { PortfolioTreemap } from "@/components/alpha/PortfolioTreemap";
import { PnlAttributionCard } from "@/components/analytics/PnlAttributionCard";
import { EdgeDecayChart } from "@/components/analytics/EdgeDecayChart";
import { TcaLiveCard } from "@/components/analytics/TcaLiveCard";
import { ShadowAuditCard } from "@/components/analytics/ShadowAuditCard";
import { DailyEdgeAttributionCard } from "@/components/analytics/DailyEdgeAttributionCard";
import { PromotionLadderCard } from "@/components/alpha/PromotionLadderCard";
import { PnlCalendarCard } from "@/components/analytics/PnlCalendarCard";
import { StrategyComparisonCard } from "@/components/analytics/StrategyComparisonCard";
import { ExportReportButton } from "@/components/analytics/ExportReportButton";
import { cn } from "@/lib/utils";
import { formatDecimal, formatPercent, formatPercentLoose, formatUsd, pnlColor } from "@/lib/format";

const RISK_PROFILES = [
  { id: "Low", label: "Low (Safe)", leverage: 1, color: "text-emerald-400" },
  { id: "Moderate", label: "Moderate", leverage: 3, color: "text-amber-400" },
  { id: "Aggressive", label: "Aggressive", leverage: 7, color: "text-rose-400" },
];

export function AlphaPortfolioPage() {
  const positionsQ = useQuery({
    queryKey: ["alpha-positions"],
    queryFn: alphaApi.validatedPositions,
    staleTime: 5 * 60_000,
  });

  const [days, setDays] = useState(365);
  const [capital, setCapital] = useState(100_000);
  const [profile, setProfile] = useState<string>("Moderate");
  const [leverage, setLeverage] = useState(3);
  const [weightingMode, setWeightingMode] = useState<"Fixed" | "InverseVolatility">("Fixed");

  const onProfileChange = (id: string) => {
    setProfile(id);
    const p = RISK_PROFILES.find((x) => x.id === id);
    if (p) setLeverage(p.leverage);
  };

  const backtestM = useMutation({
    mutationFn: () =>
      alphaApi.runBacktest({
        positions: positionsQ.data,
        backtestDays: days,
        totalCapital: capital,
        leverage,
        weightingMode,
      }),
  });

  return (
    <div className="space-y-8">
      <div className="relative flex items-start justify-between gap-4">
        <div className="relative">
          <div className="absolute -left-4 top-0 bottom-0 w-1 bg-accent shadow-[0_0_15px_rgba(0,191,255,0.5)]" />
          <h2 className="text-[10px] font-black uppercase tracking-[0.4em] text-accent mb-1 ml-2">
            Validated Edges
          </h2>
          <p className="text-2xl font-black tracking-tight ml-2">
            Alpha Portfolio{" "}
            <span className="text-muted-foreground/30 font-light">
              — 6-position Kelly portfolio (3 pairs + 3 funding alpha)
            </span>
          </p>
        </div>
        <ExportReportButton />
      </div>

      <div data-print="hide">
        <ConfigCard
          days={days}
          setDays={setDays}
          capital={capital}
          setCapital={setCapital}
          profile={profile}
          onProfileChange={onProfileChange}
          leverage={leverage}
          setLeverage={setLeverage}
          weightingMode={weightingMode}
          setWeightingMode={setWeightingMode}
          onRun={() => backtestM.mutate()}
          running={backtestM.isPending}
        />
      </div>

      <PositionsCard positions={positionsQ.data ?? []} />

      <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl">
        <PortfolioTreemap width={1100} height={360} />
      </div>

      <PromotionRadarCard />

      <PnlCalendarCard />

      <StrategyComparisonCard />

      <MlStatusCard />

      {/* PnL Attribution + Edge Decay side by side */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        <PnlAttributionCard />
        <EdgeDecayChart />
      </div>
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        <TcaLiveCard />
        <ShadowAuditCard />
      </div>

      <DailyEdgeAttributionCard />

      <PromotionLadderCard />

      {backtestM.isError && (
        <p className="text-sm text-rose-400">
          Backtest failed: {(backtestM.error as Error)?.message ?? "unknown error"}
        </p>
      )}

      {backtestM.data && (
        <>
          <SummaryStats result={backtestM.data} />
          <EquityCurveCard result={backtestM.data} />
          <ReplayCard result={backtestM.data} />
          <MonteCarloCard result={backtestM.data} />
          <LegResultsTable result={backtestM.data} />
          <CorrelationMatrix result={backtestM.data} />
        </>
      )}
    </div>
  );
}

function ConfigCard({
  days,
  setDays,
  capital,
  setCapital,
  profile,
  onProfileChange,
  leverage,
  setLeverage,
  weightingMode,
  setWeightingMode,
  onRun,
  running,
}: {
  days: number;
  setDays: (n: number) => void;
  capital: number;
  setCapital: (n: number) => void;
  profile: string;
  onProfileChange: (id: string) => void;
  leverage: number;
  setLeverage: (n: number) => void;
  weightingMode: "Fixed" | "InverseVolatility";
  setWeightingMode: (m: "Fixed" | "InverseVolatility") => void;
  onRun: () => void;
  running: boolean;
}) {
  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5 space-y-5">
      <div className="grid grid-cols-2 md:grid-cols-4 gap-3">
        <Field label="Backtest Days">
          <input
            type="number"
            value={days}
            onChange={(e) => setDays(Number(e.target.value))}
            className="w-full px-2 py-1.5 rounded bg-black/40 border border-white/10 text-xs font-mono"
          />
        </Field>
        <Field label="Total Capital">
          <input
            type="number"
            value={capital}
            onChange={(e) => setCapital(Number(e.target.value))}
            className="w-full px-2 py-1.5 rounded bg-black/40 border border-white/10 text-xs font-mono"
          />
        </Field>
        <Field label="Leverage">
          <div className="flex items-center gap-2">
            <input
              type="range"
              min={1}
              max={15}
              step={0.5}
              value={leverage}
              onChange={(e) => setLeverage(Number(e.target.value))}
              className="flex-1 accent-[#1E3A8A]"
            />
            <span className="font-mono text-xs w-10 text-right">{leverage.toFixed(1)}x</span>
          </div>
        </Field>
        <Field label="Weighting">
          <select
            value={weightingMode}
            onChange={(e) => setWeightingMode(e.target.value as "Fixed" | "InverseVolatility")}
            className="w-full px-2 py-1.5 rounded bg-black/40 border border-white/10 text-xs font-mono"
          >
            <option value="Fixed">Fixed</option>
            <option value="InverseVolatility">Inverse Volatility</option>
          </select>
        </Field>
      </div>

      <div className="flex items-center gap-3 flex-wrap">
        <span className="text-[10px] font-black uppercase tracking-widest text-muted-foreground/60">
          Risk Profile:
        </span>
        {RISK_PROFILES.map((p) => (
          <button
            key={p.id}
            onClick={() => onProfileChange(p.id)}
            className={cn(
              "px-3 py-1.5 rounded text-[11px] font-bold uppercase tracking-wider border",
              profile === p.id
                ? "bg-accent/15 border-accent/40 text-accent"
                : "bg-white/[0.02] border-white/5 text-muted-foreground hover:text-foreground"
            )}
          >
            {p.label} <span className={cn("ml-1 opacity-70", p.color)}>{p.leverage}x</span>
          </button>
        ))}
      </div>

      <button
        onClick={onRun}
        disabled={running}
        className="w-full px-4 py-3 rounded bg-accent/15 text-accent border border-accent/40 hover:bg-accent/25 text-[12px] font-black uppercase tracking-widest disabled:opacity-40"
      >
        {running ? "RUNNING BACKTEST…" : "▶ RUN PORTFOLIO BACKTEST"}
      </button>
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <span className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/60">{label}</span>
      <div className="mt-1">{children}</div>
    </label>
  );
}

function PositionsCard({ positions }: { positions: ValidatedPosition[] }) {
  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      <div className="px-5 py-3 border-b border-white/5">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-muted-foreground/50">Validated Portfolio</p>
        <p className="text-sm font-bold tracking-tight">Default 6-position Kelly weights</p>
      </div>
      <table className="w-full text-sm">
        <thead>
          <tr className="text-[9px] uppercase tracking-[0.2em] text-muted-foreground/60 border-b border-white/5">
            <th className="text-left px-5 py-3 font-black">Name</th>
            <th className="text-left px-3 py-3 font-black">Type</th>
            <th className="text-left px-3 py-3 font-black">Symbol(s)</th>
            <th className="text-left px-3 py-3 font-black">TF</th>
            <th className="text-right px-3 py-3 font-black">Weight</th>
            <th className="text-right px-3 py-3 font-black">Exp. Sharpe</th>
            <th className="text-right px-3 py-3 font-black">Exp. PF</th>
            <th className="text-right px-5 py-3 font-black">Exp. WR</th>
          </tr>
        </thead>
        <tbody>
          {positions.map((p) => (
            <tr key={p.name} className="border-b border-white/[0.02]">
              <td className="px-5 py-2 font-bold">{p.name}</td>
              <td className="px-3 py-2 text-[10px] uppercase tracking-wider text-accent/80">{p.type}</td>
              <td className="px-3 py-2 font-mono text-xs">
                {p.symbolA}
                {p.symbolB && <span className="text-muted-foreground"> / {p.symbolB}</span>}
              </td>
              <td className="px-3 py-2 font-mono text-xs">{p.timeframe}</td>
              <td className="px-3 py-2 text-right font-mono">{formatPercent(p.weight, 1)}</td>
              <td className="px-3 py-2 text-right font-mono">{p.expectedSharpe.toFixed(2)}</td>
              <td className="px-3 py-2 text-right font-mono">{Number(p.expectedProfitFactor).toFixed(2)}</td>
              <td className="px-5 py-2 text-right font-mono">{formatPercent(p.expectedWinRate, 1)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function SummaryStats({ result }: { result: BacktestResponse }) {
  return (
    <div className="grid grid-cols-2 md:grid-cols-4 lg:grid-cols-7 gap-3">
      <Stat label="Final Equity" value={formatUsd(result.finalEquity)} />
      <Stat label="Total PnL" value={formatUsd(result.totalPnl)} valueClass={pnlColor(result.totalPnl)} />
      <Stat label="Return %" value={formatPercentLoose(Number(result.totalPnlPct) / 100)} valueClass={pnlColor(result.totalPnl)} />
      <Stat label="Sharpe" value={result.portfolioSharpe.toFixed(2)} valueClass="text-accent" />
      <Stat label="Max DD" value={formatPercentLoose(Number(result.maxDrawdown))} valueClass="text-rose-400" />
      <Stat label="Avg Corr" value={result.averageCorrelation.toFixed(3)} />
      <Stat label="Trading Days" value={String(result.tradingDays)} />
    </div>
  );
}

function Stat({ label, value, valueClass = "" }: { label: string; value: string; valueClass?: string }) {
  return (
    <div className="rounded-lg border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl px-4 py-3">
      <p className="text-[9px] font-black uppercase tracking-[0.25em] text-muted-foreground/50 mb-1">{label}</p>
      <p className={cn("font-mono text-lg font-bold leading-none", valueClass)}>{value}</p>
    </div>
  );
}

function EquityCurveCard({ result }: { result: BacktestResponse }) {
  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5">
      <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/80 mb-1">Portfolio Equity</p>
      <p className="text-sm font-bold mb-4">
        Combined daily equity · Final {formatUsd(result.finalEquity)}
      </p>
      <LineChart
        values={result.equityCurve.map(Number)}
        width={1000}
        height={280}
        stroke="rgba(30, 58, 138, 0.95)"
        fill="rgba(30, 58, 138, 0.06)"
        zeroLine={false}
        yLabelFormatter={(v) => `$${(v / 1000).toFixed(0)}k`}
      />
    </div>
  );
}

function ReplayCard({ result }: { result: BacktestResponse }) {
  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5">
      <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/80 mb-1">
        Backtest Replay
      </p>
      <p className="text-sm font-bold mb-4">
        Scrub through the equity curve · variable speed playback
      </p>
      <ReplayChart
        equityCurve={result.equityCurve.map(Number)}
        dates={result.dates}
        width={1000}
        height={260}
      />
    </div>
  );
}

function MonteCarloCard({ result }: { result: BacktestResponse }) {
  const [days, setDays] = useState(120);
  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5">
      <div className="flex items-start justify-between mb-4 flex-wrap gap-3">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/80 mb-1">
            Forward Projection
          </p>
          <p className="text-sm font-bold">
            Monte Carlo Fan · 1000 paths bootstrapped from realized returns
          </p>
        </div>
        <div className="flex items-center gap-3">
          <span className="text-[10px] uppercase tracking-widest text-muted-foreground/60">
            Horizon
          </span>
          {[30, 60, 120, 250].map((d) => (
            <button
              key={d}
              onClick={() => setDays(d)}
              className={cn(
                "px-3 py-1 rounded text-[10px] font-bold uppercase tracking-wider border",
                days === d
                  ? "bg-accent/15 text-accent border-accent/40"
                  : "bg-white/[0.02] border-white/5 text-muted-foreground hover:text-foreground"
              )}
            >
              {d}d
            </button>
          ))}
        </div>
      </div>
      <MonteCarloFanChart
        equityCurve={result.equityCurve.map(Number)}
        days={days}
        paths={1000}
        width={1000}
        height={280}
      />
      <div className="mt-4 grid grid-cols-2 sm:grid-cols-4 gap-3 text-[10px]">
        <Legend color="rgba(30,58,138,0.95)" label="Median (p50)" />
        <Legend color="rgba(30,58,138,0.18)" label="p25–p75 band" />
        <Legend color="rgba(30,58,138,0.08)" label="p5–p95 band" />
        <Legend color="rgba(250,204,21,0.95)" label="Today" />
      </div>
    </div>
  );
}

function Legend({ color, label }: { color: string; label: string }) {
  return (
    <div className="flex items-center gap-2 text-muted-foreground/70">
      <span
        className="w-3 h-3 rounded-sm shrink-0"
        style={{ backgroundColor: color }}
      />
      <span>{label}</span>
    </div>
  );
}

function LegResultsTable({ result }: { result: BacktestResponse }) {
  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      <div className="px-5 py-3 border-b border-white/5">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-muted-foreground/50">Per-leg Performance</p>
        <p className="text-sm font-bold tracking-tight">Realized metrics from this run</p>
      </div>
      <table className="w-full text-sm">
        <thead>
          <tr className="text-[9px] uppercase tracking-[0.2em] text-muted-foreground/60 border-b border-white/5">
            <th className="text-left px-5 py-3 font-black">Leg</th>
            <th className="text-left px-3 py-3 font-black">Type</th>
            <th className="text-right px-3 py-3 font-black">Capital</th>
            <th className="text-right px-3 py-3 font-black">Trades</th>
            <th className="text-right px-3 py-3 font-black">WR</th>
            <th className="text-right px-3 py-3 font-black">Net PnL</th>
            <th className="text-right px-3 py-3 font-black">Sharpe</th>
            <th className="text-right px-3 py-3 font-black">DD</th>
            <th className="text-right px-5 py-3 font-black">PF</th>
          </tr>
        </thead>
        <tbody>
          {result.legs.map((leg) => (
            <tr key={leg.name} className="border-b border-white/[0.02]">
              <td className="px-5 py-2 font-bold">{leg.name}</td>
              <td className="px-3 py-2 text-[10px] uppercase text-accent/80">{leg.type}</td>
              <td className="px-3 py-2 text-right font-mono">{formatUsd(leg.capital)}</td>
              <td className="px-3 py-2 text-right font-mono">{leg.trades}</td>
              <td className="px-3 py-2 text-right font-mono">{formatPercent(leg.winRate, 1)}</td>
              <td className={cn("px-3 py-2 text-right font-mono font-bold", pnlColor(leg.netPnl))}>
                {formatUsd(leg.netPnl)}
              </td>
              <td className="px-3 py-2 text-right font-mono">{leg.sharpe.toFixed(2)}</td>
              <td className="px-3 py-2 text-right font-mono text-rose-400">
                {formatPercentLoose(Number(leg.maxDrawdownPct))}
              </td>
              <td className="px-5 py-2 text-right font-mono">{formatDecimal(Number(leg.profitFactor))}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function CorrelationMatrix({ result }: { result: BacktestResponse }) {
  const names = result.strategyNames;
  const lookup = new Map<string, number>();
  for (const cell of result.correlationMatrix) {
    lookup.set(`${cell.rowStrategy}|${cell.colStrategy}`, cell.value);
  }

  const colorOf = (v: number) => {
    const a = Math.min(1, Math.abs(v));
    if (v > 0) return `rgba(30, 58, 138, ${0.15 + a * 0.5})`;
    if (v < 0) return `rgba(244, 63, 94, ${0.15 + a * 0.5})`;
    return "rgba(255,255,255,0.05)";
  };

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5">
      <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/80 mb-1">Diversification</p>
      <p className="text-sm font-bold mb-4">Correlation Heatmap · Avg off-diagonal {result.averageCorrelation.toFixed(3)}</p>
      <div className="overflow-x-auto">
        <table className="text-[10px] font-mono">
          <thead>
            <tr>
              <th></th>
              {names.map((n) => (
                <th key={n} className="px-1 py-1 text-muted-foreground rotate-[-45deg] origin-bottom-left h-12 whitespace-nowrap">
                  {n}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {names.map((row) => (
              <tr key={row}>
                <td className="px-2 py-1 text-muted-foreground whitespace-nowrap">{row}</td>
                {names.map((col) => {
                  const v = lookup.get(`${row}|${col}`) ?? 0;
                  return (
                    <td
                      key={col}
                      title={`${row} vs ${col}: ${v.toFixed(3)}`}
                      style={{ backgroundColor: colorOf(v) }}
                      className="text-center w-14 h-7 border border-white/[0.04]"
                    >
                      {v.toFixed(2)}
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
