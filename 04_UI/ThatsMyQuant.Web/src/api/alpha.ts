import { api } from "./client";

export interface ValidatedPosition {
  name: string;
  type: "PairsArbitrage" | "FundingAlpha" | string;
  symbolA: string;
  symbolB: string | null;
  timeframe: string;
  weight: number;
  expectedSharpe: number;
  expectedProfitFactor: number;
  expectedWinRate: number;
}

export interface LegResult {
  name: string;
  type: string;
  capital: number;
  trades: number;
  winRate: number;
  netPnl: number;
  sharpe: number;
  maxDrawdownPct: number;
  profitFactor: number;
}

export interface CorrelationCell {
  rowStrategy: string;
  colStrategy: string;
  value: number;
}

export interface BacktestResponse {
  totalCapital: number;
  finalEquity: number;
  totalPnl: number;
  totalPnlPct: number;
  portfolioSharpe: number;
  maxDrawdown: number;
  averageCorrelation: number;
  tradingDays: number;
  strategyNames: string[];
  equityCurve: number[];
  dates: string[];
  legs: LegResult[];
  correlationMatrix: CorrelationCell[];
}

export interface BacktestRequest {
  positions?: ValidatedPosition[];
  totalCapital?: number;
  backtestDays?: number;
  leverage?: number;
  weightingMode?: "Fixed" | "InverseVolatility";
}

export const alphaApi = {
  validatedPositions: () => api.get<ValidatedPosition[]>("/api/alpha/positions"),
  runBacktest: (req: BacktestRequest = {}) => api.post<BacktestResponse>("/api/alpha/backtest", req),
};
