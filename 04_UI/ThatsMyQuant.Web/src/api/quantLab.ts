import { api } from "./client";

export interface SummaryStats {
  tradeCount: number;
  totalPnL: number;
  meanReturn: number;
  stdDevReturn: number;
  sharpeAllTime: number;
  maxDrawdown: number;
  var95: number;
  cVaR95: number;
}

export interface RollingSharpePoint {
  tradeIndex: number;
  sharpe: number;
}

export interface DistributionBin {
  binStart: number;
  binEnd: number;
  count: number;
}

export interface DrawdownPoint {
  tradeIndex: number;
  cumulativePnL: number;
  drawdown: number;
}

export interface MfeMaeDot {
  mfe: number;
  mae: number;
  pnl: number;
  symbol: string;
  exitTime: string;
}

export interface QuantLabMetrics {
  strategyTag: string;
  summary: SummaryStats;
  rollingSharpe: RollingSharpePoint[];
  returnDistribution: DistributionBin[];
  drawdownCurve: DrawdownPoint[];
  mfeMaeScatter: MfeMaeDot[];
}

export interface StrategyOption {
  strategyTag: string;
  tradeCount: number;
}

// === Single-strategy backtest ===

export interface SingleBacktestRequest {
  symbol: string;
  timeframe?: string;
  days: number;
  startingCapital: number;
  leverage: number;
}

export interface SingleBacktestResult {
  symbol: string;
  timeframe: string;
  days: number;
  candleCount: number;
  capital: number;
  leverage: number;
  totalTrades: number;
  winningTrades: number;
  losingTrades: number;
  winRate: number;
  totalPnL: number;
  maxDrawdownPercent: number;
  sharpe: number;
  sortino: number;
  profitFactor: number;
  averageRR: number;
  finalEquity: number;
  startDate: string;
  endDate: string;
  equityCurve: number[];
  reasoning: string;
  entryRejectReasons: Record<string, number>;
}

// === Walk-forward optimization ===

export interface WalkForwardRequest {
  symbol: string;
  timeframe?: string;
  days: number;
  numWindows: number;
  capital: number;
}

export interface WalkForwardWindow {
  windowIndex: number;
  trainStartDate: string;
  trainEndDate: string;
  oosStartDate: string;
  oosEndDate: string;
  trainSharpe: number;
  oosSharpe: number;
  trainPnL: number;
  oosPnL: number;
  trainTrades: number;
  oosTrades: number;
  trainWinRate: number;
  oosWinRate: number;
  trainProfitFactor: number;
  oosProfitFactor: number;
  trainMaxDD: number;
  oosMaxDD: number;
  trainRegime: string;
  oosRegime: string;
}

export interface WalkForwardResult {
  symbol: string;
  timeframe: string;
  numWindows: number;
  completedWindows: number;
  totalWindows: number;
  candleCount: number;
  walkForwardEfficiency: number;
  aggregateTrainSharpe: number;
  aggregateOosSharpe: number;
  aggregateOosPnL: number;
  aggregateOosWinRate: number;
  oosConsistency: number;
  regimeStabilityScore: number;
  profitFactorDegradation: number;
  isRobust: boolean;
  windows: WalkForwardWindow[];
}

export const quantLabApi = {
  metrics: (strategyTag?: string, limit = 500) => {
    const params = new URLSearchParams();
    if (strategyTag) params.set("strategyTag", strategyTag);
    params.set("limit", String(limit));
    return api.get<QuantLabMetrics>(`/api/quant-lab/metrics?${params.toString()}`);
  },
  strategies: () => api.get<StrategyOption[]>("/api/quant-lab/strategies"),
  singleBacktest: (req: SingleBacktestRequest) =>
    api.post<SingleBacktestResult>("/api/quant-lab/single-backtest", req),
  walkForward: (req: WalkForwardRequest) =>
    api.post<WalkForwardResult>("/api/quant-lab/walk-forward", req),
};
