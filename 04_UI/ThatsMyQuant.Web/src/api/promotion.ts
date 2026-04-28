import { api } from "./client";

export interface PromotionThresholds {
  minTrades: number;
  minSharpe: number;
  minProfitFactor: number;
  maxDrawdownPct: number;
  minWinRate: number;
  minAvgRR: number;
  maxCostLeakagePct: number;
}

export interface Criterion {
  name: string;
  value: number;
  threshold: number;
  passed: boolean;
  direction: "min" | "max";
}

export interface StrategyReadiness {
  strategyTag: string;
  stage: string;
  approved: boolean;
  passCount: number;
  totalCount: number;
  readinessScore: number;
  trades: number;
  sharpe: number;
  profitFactor: number;
  winRate: number;
  maxDrawdownPercent: number;
  averageRR: number;
  costLeakagePercent: number;
  netPnL: number;
  criteria: Criterion[];
}

export interface ReadinessResponse {
  thresholds: PromotionThresholds;
  strategies: StrategyReadiness[];
}

export interface MlStatus {
  hasActiveModel: boolean;
  artifactPath: string;
  totalSamples: number;
  positiveSamples: number;
  negativeSamples: number;
  classBalance: number;
  isTrainable: boolean;
  uniqueSymbols: number;
  earliestSampleUtc: string;
  latestSampleUtc: string;
  directionalStrategyTags: string[];
}

export const promotionApi = {
  readiness: () => api.get<ReadinessResponse>("/api/promotion/readiness"),
  mlStatus: () => api.get<MlStatus>("/api/ml/status"),
};
