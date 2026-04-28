import { api } from "./client";

export type CircuitBreakerState = "Armed" | "Warning" | "Tripped";

export interface CircuitBreakerSnapshot {
  state: CircuitBreakerState | string;
  realizedDrawdown: number;
  peakEquity: number;
  currentEquity: number;
  lastTripReason: string | null;
  lastTripUtc: string | null;
}

export interface FundingOpportunity {
  symbol: string;
  fundingRate: number;
  fundingBps: number;
  conviction: string;
  direction: string;
  observedUtc: string;
}

export interface PromotionCandidate {
  symbol: string;
  strategyTag: string;
  firstSignalUtc: string;
  state: string;
  oosSharpe: number;
  oosProfitFactor: number;
  oosTrades: number;
  rejectionReason: string | null;
}

export interface ProModeSnapshot {
  isArmed: boolean;
  circuitBreaker: CircuitBreakerSnapshot;
  currentRegime: string;
  baseLeverage: number;
  effectiveLeverage: number;
  leverageExplanation: string;
  recentOpportunities: FundingOpportunity[];
  candidatePool: PromotionCandidate[];
}

export const proModeApi = {
  snapshot: () => api.get<ProModeSnapshot>("/api/pro-mode/snapshot"),
  arm: (baseLeverage: number) => api.post<ProModeSnapshot>("/api/pro-mode/arm", { baseLeverage }),
  disarm: (operatorNote: string) => api.post<ProModeSnapshot>("/api/pro-mode/disarm", { operatorNote }),
  kill: (operatorNote: string) => api.post<ProModeSnapshot>("/api/pro-mode/kill", { operatorNote }),
  scan: () =>
    api.post<{ opportunityCount: number; snapshot: ProModeSnapshot }>("/api/pro-mode/scan"),
  setRegime: (regime: string) => api.post<ProModeSnapshot>("/api/pro-mode/regime", { regime }),
};
