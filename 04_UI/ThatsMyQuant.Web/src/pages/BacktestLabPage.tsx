import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import {
  backtestLabApi,
  type AnalyzeResponse,
} from "@/api/backtestLab";
import { EquityFanChart } from "@/components/backtestlab/EquityFanChart";
import { RegressionDecompositionChart } from "@/components/backtestlab/RegressionDecompositionChart";
import { EntropyTrajectoryChart } from "@/components/backtestlab/EntropyTrajectoryChart";

/**
 * Backtest Lab — pure-math diagnostic views over an equity curve.
 *
 * MVP scope (G3): operator pastes (or loads sample) equity curve as JSON
 * `{dates, equityCurve}`, hits Analyze, and sees the Monte Carlo fan chart
 * + headline regression / entropy stats. Subsequent commits add dedicated
 * regression + entropy chart components and an LLM interpretation panel.
 *
 * Why a paste box instead of a strategy picker dropdown: this keeps the page
 * decoupled from the live alpha portfolio in MVP — works just as well for
 * archived backtest results, externally-imported curves (CSV → paste), or
 * synthetic stress tests. A "load from running strategy" shortcut lands in
 * G6 once the basic flow is validated.
 */
export function BacktestLabPage() {
  const [jsonInput, setJsonInput] = useState<string>("");
  const [parseError, setParseError] = useState<string | null>(null);

  const mutation = useMutation({
    mutationFn: backtestLabApi.analyze,
  });

  const handleAnalyze = () => {
    setParseError(null);
    let parsed: { dates?: unknown; equityCurve?: unknown };
    try {
      parsed = JSON.parse(jsonInput);
    } catch (e) {
      setParseError(`JSON parse error: ${(e as Error).message}`);
      return;
    }
    if (!Array.isArray(parsed.dates) || !Array.isArray(parsed.equityCurve)) {
      setParseError("payload must be {dates: string[], equityCurve: number[]}");
      return;
    }
    mutation.mutate({
      dates: parsed.dates as string[],
      equityCurve: parsed.equityCurve as number[],
      monteCarloIterations: 1000,
      monteCarloSeed: 42,
    });
  };

  const handleLoadSample = () => {
    setJsonInput(JSON.stringify(buildSamplePayload(), null, 2));
    setParseError(null);
  };

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="relative">
        <div className="absolute -left-4 top-0 bottom-0 w-1 bg-accent shadow-[0_0_15px_rgba(0,191,255,0.5)]" />
        <h2 className="text-[10px] font-black uppercase tracking-[0.4em] text-accent mb-1 ml-2">
          Quant Analysis
        </h2>
        <p className="text-2xl font-black tracking-tight ml-2">
          Backtest Lab{" "}
          <span className="text-muted-foreground/30 font-light">
            — MC fan, regression decomposition, entropy trajectory
          </span>
        </p>
      </div>

      {/* Input */}
      <section className="rounded-lg border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4 space-y-3">
        <div className="flex items-center justify-between">
          <label
            htmlFor="bt-input"
            className="text-[10px] font-black uppercase tracking-[0.18em] text-muted-foreground/70"
          >
            Equity curve payload (JSON)
          </label>
          <div className="flex items-center gap-2">
            <button
              onClick={handleLoadSample}
              className="text-[11px] px-3 py-1 rounded border border-white/10 hover:border-accent/40 text-muted-foreground hover:text-foreground transition"
            >
              Load sample
            </button>
            <button
              onClick={handleAnalyze}
              disabled={mutation.isPending || jsonInput.trim().length === 0}
              className="text-[11px] px-3 py-1 rounded bg-accent/15 border border-accent/40 text-accent hover:bg-accent/25 disabled:opacity-30 transition"
            >
              {mutation.isPending ? "Analyzing…" : "Analyze →"}
            </button>
          </div>
        </div>
        <textarea
          id="bt-input"
          value={jsonInput}
          onChange={(e) => setJsonInput(e.target.value)}
          placeholder='{"dates": ["2026-01-01T00:00:00Z", ...], "equityCurve": [100000, 100500, ...]}'
          className="w-full h-32 bg-[#070A0F] border border-white/10 rounded p-3 text-xs font-mono text-foreground placeholder:text-muted-foreground/30 focus:outline-none focus:border-accent resize-none"
        />
        {parseError && (
          <div className="text-xs text-rose-400 font-mono">{parseError}</div>
        )}
        {mutation.isError && (
          <div className="text-xs text-rose-400 font-mono">
            Server error: {(mutation.error as Error).message}
          </div>
        )}
      </section>

      {/* Results */}
      {mutation.data && <ResultsPanel data={mutation.data} />}
    </div>
  );
}

function ResultsPanel({ data }: { data: AnalyzeResponse }) {
  const { monteCarloFan, regression, entropy } = data;
  return (
    <div className="space-y-6">
      {/* MC Fan chart */}
      <section className="rounded-lg border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
        <div className="flex items-center justify-between mb-3">
          <h3 className="text-xs font-black uppercase tracking-[0.18em] text-muted-foreground/70">
            Monte Carlo equity fan
          </h3>
          <span className="text-[10px] text-muted-foreground/50 font-mono">
            iterations={monteCarloFan.iterations}
            {monteCarloFan.randomSeed !== null
              ? ` · seed=${monteCarloFan.randomSeed}`
              : ""}
          </span>
        </div>
        <EquityFanChart fan={monteCarloFan} />
      </section>

      {/* Regression decomposition: chart + headline stats side-by-side */}
      <section className="grid grid-cols-1 lg:grid-cols-3 gap-4">
        <div className="lg:col-span-2 rounded-lg border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
          <h3 className="text-xs font-black uppercase tracking-[0.18em] text-muted-foreground/70 mb-3">
            Linear regression decomposition
          </h3>
          <RegressionDecompositionChart
            regression={regression}
            realizedEquity={monteCarloFan.realizedEquity}
            dates={monteCarloFan.dates}
          />
        </div>
        <StatCard
          title="OLS stats"
          rows={[
            ["Slope (USD/bar)", regression.slopeUsdPerBar.toFixed(4)],
            ["Intercept (USD)", regression.interceptUsd.toFixed(2)],
            ["R²", regression.rSquared.toFixed(4)],
            ["Residual σ (USD)", regression.residualStdUsd.toFixed(2)],
            ["N observations", String(regression.nObservations)],
          ]}
          hint={rSquaredHint(regression.rSquared)}
        />
      </section>

      {/* Entropy trajectory: chart + headline stats side-by-side */}
      <section className="grid grid-cols-1 lg:grid-cols-3 gap-4">
        <div className="lg:col-span-2 rounded-lg border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
          <h3 className="text-xs font-black uppercase tracking-[0.18em] text-muted-foreground/70 mb-3">
            Shannon entropy trajectory
          </h3>
          <EntropyTrajectoryChart entropy={entropy} />
        </div>
        <StatCard
          title="Entropy stats"
          rows={[
            ["Window (bars)", String(entropy.windowBars)],
            ["Bins", String(entropy.bins)],
            ["Max entropy (nats)", entropy.maxEntropyNats.toFixed(3)],
            ["Last entropy (nats)", lastDefined(entropy.entropy)?.toFixed(3) ?? "—"],
            [
              "Last / max",
              percentOfMax(lastDefined(entropy.entropy), entropy.maxEntropyNats),
            ],
          ]}
          hint={entropyHint(lastDefined(entropy.entropy), entropy.maxEntropyNats)}
        />
      </section>
    </div>
  );
}

function entropyHint(last: number | null, max: number): string {
  if (last === null || max === 0) return "Window not yet full or degenerate input.";
  const ratio = last / max;
  if (ratio < 0.3) return "⚠ Returns concentrating — possible regime shift, drawdown often follows for mean-reversion strategies.";
  if (ratio < 0.6) return "Below-normal entropy — distribution narrowing.";
  if (ratio < 0.85) return "Healthy regime — distribution well-spread.";
  return "Near maximum entropy — fat-tailed regime, higher noise-fitting risk.";
}

function StatCard({
  title,
  rows,
  hint,
}: {
  title: string;
  rows: [string, string][];
  hint?: string;
}) {
  return (
    <div className="rounded-lg border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
      <h3 className="text-xs font-black uppercase tracking-[0.18em] text-muted-foreground/70 mb-3">
        {title}
      </h3>
      <table className="w-full text-xs">
        <tbody>
          {rows.map(([k, v]) => (
            <tr key={k} className="border-b border-white/[0.04] last:border-0">
              <td className="py-1.5 text-muted-foreground/60">{k}</td>
              <td className="py-1.5 text-right font-mono text-foreground">{v}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {hint && (
        <p className="text-[10px] text-muted-foreground/50 mt-3 italic">{hint}</p>
      )}
    </div>
  );
}

// ---------- helpers ----------

function lastDefined(entropy: number[]): number | null {
  for (let i = entropy.length - 1; i >= 0; i--) {
    if (Number.isFinite(entropy[i])) return entropy[i];
  }
  return null;
}

function percentOfMax(value: number | null, max: number): string {
  if (value === null || max === 0) return "—";
  return `${((value / max) * 100).toFixed(1)}%`;
}

function rSquaredHint(r2: number): string {
  if (r2 > 0.95) return "Near-monotonic compounding — sus on short samples.";
  if (r2 > 0.7)  return "Healthy edge with some path noise.";
  if (r2 > 0.3)  return "Noisy realised curve with directional drift.";
  return "Mostly random walk — slope might be illusory.";
}

/** Build a deterministic synthetic equity curve so the operator can dry-run
 *  the page without wiring a real backtest. ~200 bars of geometric drift +
 *  Gaussian noise; numerically the same shape they'd see from the funding
 *  alpha tier on a moderate-vol cycle. */
function buildSamplePayload(): { dates: string[]; equityCurve: number[] } {
  const n = 200;
  const dates: string[] = [];
  const equityCurve: number[] = [];
  let eq = 100_000;
  // Tiny LCG so the sample is byte-identical across reloads (browsers' Math.random
  // has no seed). Constants from Numerical Recipes.
  let s = 1234567;
  const rand = () => ((s = (s * 1103515245 + 12345) & 0x7fffffff) / 0x80000000);
  const t0 = Date.UTC(2026, 0, 1);
  const drift = 0.0008;  // ~+8% in 100 bars
  const vol   = 0.012;
  for (let i = 0; i < n; i++) {
    if (i > 0) {
      // Box-Muller for Gaussian shock
      const u1 = 1 - rand();
      const u2 = 1 - rand();
      const z = Math.sqrt(-2 * Math.log(u1)) * Math.cos(2 * Math.PI * u2);
      eq = eq * (1 + drift + vol * z);
    }
    equityCurve.push(Math.round(eq * 100) / 100);
    dates.push(new Date(t0 + i * 4 * 3600_000).toISOString());
  }
  return { dates, equityCurve };
}
