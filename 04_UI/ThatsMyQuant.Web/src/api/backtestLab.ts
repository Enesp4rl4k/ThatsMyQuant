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

export const backtestLabApi = {
  /** POST /api/backtest-lab/analyze — single round trip, three views back. */
  analyze: (req: AnalyzeRequest) =>
    api.post<AnalyzeResponse>("/api/backtest-lab/analyze", req),
};
