import { api } from "./client";

/**
 * Wire types for the Backtest Lab analyze endpoint. Mirrors C# DTOs in
 * BacktestLabAnalysis.cs / BacktestLabEndpoints.cs — keep in sync if either
 * side changes.
 */

export interface MonteCarloFanResult {
  dates: string[];
  percentile05: number[];
  percentile25: number[];
  percentile50: number[];
  percentile75: number[];
  percentile95: number[];
  realizedEquity: number[];
  iterations: number;
  randomSeed: number | null;
}

export interface LinearRegressionResult {
  slopeUsdPerBar: number;
  interceptUsd: number;
  rSquared: number;
  residualStdUsd: number;
  trendUsd: number[];
  residualsUsd: number[];
  nObservations: number;
}

export interface ShannonEntropyResult {
  dates: string[];
  entropy: number[];        // NaN for the first window-1 entries
  windowBars: number;
  bins: number;
  maxEntropyNats: number;
}

export interface AnalyzeResponse {
  monteCarloFan: MonteCarloFanResult;
  regression: LinearRegressionResult;
  entropy: ShannonEntropyResult;
}

export interface AnalyzeRequest {
  dates: string[];
  equityCurve: number[];
  monteCarloIterations?: number;
  monteCarloSeed?: number;
  entropyWindow?: number;
  entropyBins?: number;
}

/** Numeric summary fed to the LLM interpreter. Mirrors the C# DTO. */
export interface BacktestInterpretationRequest {
  label?: string | null;
  nObservations: number;
  startEquity: number;
  endEquity: number;
  regressionSlopeUsdPerBar: number;
  regressionRSquared: number;
  regressionResidualStdUsd: number;
  mcIterations: number;
  mcMedianEnd: number;
  mcP05End: number;
  mcP95End: number;
  entropyWindow: number;
  entropyBins: number;
  entropyMaxNats: number;
  entropyLastNats: number;
}

export interface BacktestInterpretation {
  markdown: string;
  provider: string;
  error?: string | null;
}

export const backtestLabApi = {
  /** POST /api/backtest-lab/analyze — single round trip, three views back. */
  analyze: (req: AnalyzeRequest) =>
    api.post<AnalyzeResponse>("/api/backtest-lab/analyze", req),

  /** POST /api/backtest-lab/interpret — LLM-generated narrative over the analysis. */
  interpret: (req: BacktestInterpretationRequest) =>
    api.post<BacktestInterpretation>("/api/backtest-lab/interpret", req),
};

/** Helper: build the interpret request from an AnalyzeResponse. Last finite
 *  entropy used (skips the leading window-1 NaN values). */
export function buildInterpretRequest(
  data: AnalyzeResponse,
  label?: string,
): BacktestInterpretationRequest {
  const eq = data.monteCarloFan.realizedEquity;
  const lastIdx = eq.length - 1;
  const lastEntropy = lastFiniteValue(data.entropy.entropy);
  return {
    label: label ?? null,
    nObservations: eq.length,
    startEquity: eq[0],
    endEquity: eq[lastIdx],
    regressionSlopeUsdPerBar: data.regression.slopeUsdPerBar,
    regressionRSquared: data.regression.rSquared,
    regressionResidualStdUsd: data.regression.residualStdUsd,
    mcIterations: data.monteCarloFan.iterations,
    mcMedianEnd: data.monteCarloFan.percentile50[lastIdx],
    mcP05End: data.monteCarloFan.percentile05[lastIdx],
    mcP95End: data.monteCarloFan.percentile95[lastIdx],
    entropyWindow: data.entropy.windowBars,
    entropyBins: data.entropy.bins,
    entropyMaxNats: data.entropy.maxEntropyNats,
    entropyLastNats: lastEntropy ?? 0,
  };
}

function lastFiniteValue(arr: number[]): number | null {
  for (let i = arr.length - 1; i >= 0; i--) {
    if (Number.isFinite(arr[i])) return arr[i];
  }
  return null;
}
