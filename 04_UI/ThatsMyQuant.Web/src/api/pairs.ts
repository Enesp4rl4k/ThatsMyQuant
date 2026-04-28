import { api } from "./client";

export interface CointegrationDiagnostics {
  isCointegrated: boolean;
  hedgeRatio: number;
  spreadAverage: number;
  spreadStdDev: number;
  adfTestStatistic: number;
  adfTStatistic: number;
  halfLife: number;
  correlation: number;
  hurstExponent: number;
  ouKappa: number;
  ouTheta: number;
  ouSigma: number;
}

export interface PairCandidate {
  symbolY: string;
  symbolX: string;
  qualityScore: number;
  diagnostics: CointegrationDiagnostics;
}

export interface DiscoverResponse {
  universeSize: number;
  bars: number;
  timeframe: string;
  candidates: PairCandidate[];
}

export interface PairTestResponse {
  symbolY: string;
  symbolX: string;
  timeframe: string;
  bars: number;
  diagnostics: CointegrationDiagnostics;
  qualityScore: number;
  spread: number[];
  zScore: number[];
  timestamps: string[];
}

export interface DiscoverRequest {
  symbols?: string[];
  timeframe?: string;
  days?: number;
  topN?: number;
}

export interface TestPairRequest {
  symbolY: string;
  symbolX: string;
  timeframe?: string;
  days?: number;
}

export interface ActivePairSnapshot {
  symbolY: string;
  symbolX: string;
  isCointegrated: boolean;
  hedgeRatio: number;
  halfLife: number;
  hurstExponent: number;
  correlation: number;
  adfTStat: number;
  currentZ: number;
  state: "Neutral" | "AtMean" | "LongSpread" | "ShortSpread" | "Stop" | "Error";
  spreadAverage: number;
  spreadStdDev: number;
  zTail: number[];
  error: string | null;
}

export interface ActivePairsResponse {
  timeframe: string;
  entryZ: number;
  exitZ: number;
  stopZ: number;
  pairs: ActivePairSnapshot[];
}

export interface DiscoveryCandidate {
  pair: string;          // "SOLUSDT:AVAXUSDT"
  qualityScore: number;
  beta: number;
  adfTStat: number;
  halfLifeBars: number;
  correlation: number;
  hurstExponent: number;
}

export interface DiscoveriesResponse {
  generatedUtc: string | null;
  timeframe: string | null;
  lookbackBars: number;
  universeCount: number;
  candidates: DiscoveryCandidate[];
  note?: string;
}

export const pairsApi = {
  discover: (req: DiscoverRequest = {}) => api.post<DiscoverResponse>("/api/pairs/discover", req),
  test: (req: TestPairRequest) => api.post<PairTestResponse>("/api/pairs/test", req),
  active: () => api.get<ActivePairsResponse>("/api/pairs/active"),
  discoveries: () => api.get<DiscoveriesResponse>("/api/pairs/discoveries"),
};
