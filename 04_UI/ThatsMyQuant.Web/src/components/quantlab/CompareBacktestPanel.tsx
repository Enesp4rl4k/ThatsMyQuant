import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { quantLabApi, type SingleBacktestResult } from "@/api/quantLab";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";

const TIMEFRAMES = ["5m", "15m", "1h", "4h", "1d"] as const;

interface ParamSet {
  symbol: string;
  timeframe: string;
  days: number;
  capital: number;
  leverage: number;
}

const DEFAULT_A: ParamSet = { symbol: "BTCUSDT", timeframe: "1h", days: 180, capital: 10_000, leverage: 3 };
const DEFAULT_B: ParamSet = { symbol: "BTCUSDT", timeframe: "4h", days: 180, capital: 10_000, leverage: 3 };

/**
 * A/B backtest comparison — run two parameter sets through the same engine and
 * see headline metrics + equity curves side by side. Wins are highlighted by
 * comparing each metric: better-is-good for sharpe/PF/WR/PnL, worse-is-good for DD.
 *
 * Calls the same `/api/quant-lab/single-backtest` endpoint twice in parallel.
 */
export function CompareBacktestPanel() {
  const [paramsA, setParamsA] = useState<ParamSet>(DEFAULT_A);
  const [paramsB, setParamsB] = useState<ParamSet>(DEFAULT_B);

  const runA = useMutation({
    mutationFn: (p: ParamSet) =>
      quantLabApi.singleBacktest({ symbol: p.symbol, timeframe: p.timeframe, days: p.days, startingCapital: p.capital, leverage: p.leverage }),
  });
  const runB = useMutation({
    mutationFn: (p: ParamSet) =>
      quantLabApi.singleBacktest({ symbol: p.symbol, timeframe: p.timeframe, days: p.days, startingCapital: p.capital, leverage: p.leverage }),
  });

  const isRunning = runA.isPending || runB.isPending;

  const runBoth = () => {
    runA.mutate(paramsA);
    runB.mutate(paramsB);
  };

  const a = runA.data;
  const b = runB.data;

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      <div className="px-5 py-3 border-b border-white/5 flex items-baseline justify-between">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
            A / B Backtest Comparison
          </p>
          <p className="text-[12px] font-serif italic text-muted-foreground/70">
            run two parameter sets in parallel — wins highlighted
          </p>
        </div>
        <button
          onClick={runBoth}
          disabled={isRunning}
          className="px-4 py-1.5 rounded-md bg-accent/15 text-accent border border-accent/30 hover:bg-accent/25 transition-colors text-[10px] font-black uppercase tracking-widest disabled:opacity-40"
        >
          {isRunning ? "Running…" : "▶ Run Both"}
        </button>
      </div>

      <div className="grid grid-cols-1 md:grid-cols-2 divide-y md:divide-y-0 md:divide-x divide-white/5">
        <ParamColumn label="A · Baseline" badgeColor="bg-cyan-500/15 text-cyan-400 border-cyan-400/30" params={paramsA} setParams={setParamsA} />
        <ParamColumn label="B · Variant"  badgeColor="bg-amber-500/15 text-amber-400 border-amber-400/30" params={paramsB} setParams={setParamsB} />
      </div>

      {(runA.isError || runB.isError) && (
        <div className="px-5 py-3 border-t border-white/5">
          {runA.isError && <p className="text-xs italic font-serif text-rose-400">A failed: {(runA.error as Error)?.message}</p>}
          {runB.isError && <p className="text-xs italic font-serif text-rose-400">B failed: {(runB.error as Error)?.message}</p>}
        </div>
      )}

      {a && b && <ComparisonResults a={a} b={b} />}
    </div>
  );
}

function ParamColumn({
  label, badgeColor, params, setParams,
}: {
  label: string;
  badgeColor: string;
  params: ParamSet;
  setParams: React.Dispatch<React.SetStateAction<ParamSet>>;
}) {
  const set = <K extends keyof ParamSet>(k: K, v: ParamSet[K]) => setParams((p) => ({ ...p, [k]: v }));
  return (
    <div className="p-5 space-y-3">
      <span className={cn("inline-block px-2 py-0.5 rounded text-[9px] font-black uppercase tracking-widest border", badgeColor)}>
        {label}
      </span>
      <div className="grid grid-cols-2 gap-2">
        <Field label="Symbol">
          <input
            value={params.symbol}
            onChange={(e) => set("symbol", e.target.value.toUpperCase())}
            className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
          />
        </Field>
        <Field label="Timeframe">
          <select
            value={params.timeframe}
            onChange={(e) => set("timeframe", e.target.value)}
            className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
          >
            {TIMEFRAMES.map((tf) => (<option key={tf} value={tf}>{tf}</option>))}
          </select>
        </Field>
        <Field label="Days">
          <input
            type="number" min={30} max={1095} step={30}
            value={params.days}
            onChange={(e) => set("days", parseInt(e.target.value, 10) || 180)}
            className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
          />
        </Field>
        <Field label="Capital">
          <input
            type="number" min={1000} step={1000}
            value={params.capital}
            onChange={(e) => set("capital", parseInt(e.target.value, 10) || 10_000)}
            className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
          />
        </Field>
        <Field label="Leverage">
          <input
            type="number" min={1} max={15} step={0.5}
            value={params.leverage}
            onChange={(e) => set("leverage", parseFloat(e.target.value) || 1)}
            className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
          />
        </Field>
      </div>
    </div>
  );
}

function ComparisonResults({ a, b }: { a: SingleBacktestResult; b: SingleBacktestResult }) {
  // Compare: higher-is-better for most, lower-is-better for maxDrawdown
  const cmp = (av: number, bv: number, higherBetter: boolean) => {
    if (av === bv) return { aTone: "neutral", bTone: "neutral", delta: 0 } as const;
    const aWins = higherBetter ? av > bv : av < bv;
    return aWins
      ? { aTone: "pos" as const, bTone: "neg" as const, delta: av - bv }
      : { aTone: "neg" as const, bTone: "pos" as const, delta: av - bv };
  };

  const rows: { label: string; aVal: number; bVal: number; format: "usd" | "pct" | "num"; higherBetter: boolean }[] = [
    { label: "Trades",     aVal: a.totalTrades,        bVal: b.totalTrades,        format: "num", higherBetter: true },
    { label: "Win Rate",   aVal: a.winRate * 100,      bVal: b.winRate * 100,      format: "pct", higherBetter: true },
    { label: "Net P&L",    aVal: a.totalPnL,           bVal: b.totalPnL,           format: "usd", higherBetter: true },
    { label: "Sharpe",     aVal: a.sharpe,             bVal: b.sharpe,             format: "num", higherBetter: true },
    { label: "Profit Factor", aVal: a.profitFactor,    bVal: b.profitFactor,       format: "num", higherBetter: true },
    { label: "Max DD %",   aVal: a.maxDrawdownPercent, bVal: b.maxDrawdownPercent, format: "pct", higherBetter: false },
  ];

  return (
    <div className="border-t border-white/5 p-5 space-y-5">
      {/* Comparison table */}
      <div className="grid grid-cols-[1fr_minmax(0,1.2fr)_minmax(0,1.2fr)_minmax(0,1fr)] gap-x-3 gap-y-1.5 text-[11px] font-mono items-baseline">
        <span className="text-[9px] uppercase tracking-widest text-muted-foreground/40">Metric</span>
        <span className="text-cyan-400 text-[9px] uppercase tracking-widest font-black">A</span>
        <span className="text-amber-400 text-[9px] uppercase tracking-widest font-black">B</span>
        <span className="text-[9px] uppercase tracking-widest text-muted-foreground/40 text-right">Δ (A − B)</span>

        {rows.map((r) => {
          const c = cmp(r.aVal, r.bVal, r.higherBetter);
          return (
            <Row key={r.label} {...r} aTone={c.aTone} bTone={c.bTone} delta={c.delta} />
          );
        })}
      </div>

      {/* Overlaid equity curves */}
      <OverlaidEquityCurves a={a.equityCurve} b={b.equityCurve} />
    </div>
  );
}

function Row({
  label, aVal, bVal, format, aTone, bTone, delta,
}: {
  label: string;
  aVal: number;
  bVal: number;
  format: "usd" | "pct" | "num";
  aTone: "pos" | "neg" | "neutral";
  bTone: "pos" | "neg" | "neutral";
  delta: number;
}) {
  const fmt = (v: number) =>
    format === "usd" ? formatUsd(v)
    : format === "pct" ? `${v.toFixed(2)}%`
    : v.toFixed(2);
  const tone = (t: "pos" | "neg" | "neutral") =>
    t === "pos" ? "text-emerald-400 font-bold" :
    t === "neg" ? "text-rose-400" :
    "text-foreground/80";
  return (
    <>
      <span className="text-muted-foreground/70">{label}</span>
      <span className={cn("tabular-nums", tone(aTone))}>{fmt(aVal)}</span>
      <span className={cn("tabular-nums", tone(bTone))}>{fmt(bVal)}</span>
      <span className={cn("tabular-nums text-right text-[10px]",
        Math.abs(delta) < 1e-9 ? "text-muted-foreground/40" :
        delta > 0 ? "text-cyan-400" : "text-amber-400")}>
        {delta > 0 ? "+" : ""}{fmt(delta)}
      </span>
    </>
  );
}

function OverlaidEquityCurves({ a, b }: { a: number[]; b: number[] }) {
  if (a.length < 2 && b.length < 2) return null;

  // Normalise both to start at 100% so curves are comparable across capital levels
  const normalise = (curve: number[]) => {
    if (curve.length === 0) return curve;
    const start = curve[0];
    if (start === 0) return curve;
    return curve.map((v) => (v / start) * 100);
  };
  const aN = normalise(a);
  const bN = normalise(b);

  const W = 920;
  const H = 220;
  const PAD = { t: 12, r: 16, b: 24, l: 44 };
  const iW = W - PAD.l - PAD.r;
  const iH = H - PAD.t - PAD.b;

  const allVals = [...aN, ...bN];
  const yMin = Math.min(...allVals, 100);
  const yMax = Math.max(...allVals, 100);
  const yRange = yMax - yMin || 1;

  const scX = (i: number, n: number) => PAD.l + (i / Math.max(1, n - 1)) * iW;
  const scY = (v: number) => PAD.t + iH * (1 - (v - yMin) / yRange);

  const path = (curve: number[]) => {
    let d = "";
    for (let i = 0; i < curve.length; i++) {
      const x = scX(i, curve.length);
      const y = scY(curve[i]);
      d += d ? ` L${x.toFixed(1)},${y.toFixed(1)}` : `M${x.toFixed(1)},${y.toFixed(1)}`;
    }
    return d || null;
  };

  const baseline = scY(100);

  return (
    <div className="border-t border-white/5 pt-4">
      <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/50 mb-2">
        Equity Curves (normalised to 100% start)
      </p>
      <svg width={W} height={H} style={{ display: "block", maxWidth: "100%" }}>
        {/* Grid */}
        {[0, 0.25, 0.5, 0.75, 1.0].map((t) => {
          const y = PAD.t + iH * t;
          const v = yMax - t * yRange;
          return (
            <g key={t}>
              <line x1={PAD.l} x2={PAD.l + iW} y1={y} y2={y} stroke="rgba(255,255,255,0.04)" strokeWidth={0.5} />
              <text x={PAD.l - 4} y={y + 3} fontSize={9} textAnchor="end" fill="rgba(255,255,255,0.35)" fontFamily="ui-monospace">
                {v.toFixed(0)}
              </text>
            </g>
          );
        })}
        {/* 100% baseline */}
        <line x1={PAD.l} x2={PAD.l + iW} y1={baseline} y2={baseline} stroke="rgba(255,255,255,0.18)" strokeDasharray="4 4" strokeWidth={1} />

        {/* B curve (amber, behind) */}
        {path(bN) && <path d={path(bN)!} fill="none" stroke="#F0883E" strokeWidth={1.5} strokeOpacity={0.85} />}
        {/* A curve (cyan, front) */}
        {path(aN) && <path d={path(aN)!} fill="none" stroke="#0EA5E9" strokeWidth={1.7} />}
      </svg>
      <div className="flex items-center gap-4 mt-1 text-[10px] font-mono text-muted-foreground/60">
        <span><span className="inline-block w-6 h-0.5 bg-[#0EA5E9] mr-1.5 align-middle" />A</span>
        <span><span className="inline-block w-6 h-0.5 bg-[#F0883E] mr-1.5 align-middle" />B</span>
        <span><span className="inline-block w-6 border-t border-white/30 border-dashed mr-1.5 align-middle" />100% start</span>
      </div>
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
