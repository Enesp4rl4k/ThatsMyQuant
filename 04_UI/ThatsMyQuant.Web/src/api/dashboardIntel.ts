import { api } from "./client";

export interface DailyBrief {
  generatedUtc: string;
  overnight: string;
  outlook: string;
  recommendation: string;
  closedTrades24h: number;
  wins24h: number;
  pnl24h: number;
  openPositions: number;
  unrealisedPnl: number;
  totalEquity: number;
  actions: string[];
}

export interface PreflightCheck {
  id: string;
  title: string;
  status: "ok" | "warn" | "fail";
  detail: string;
}

export interface PreflightResponse {
  generatedUtc: string;
  greenLight: boolean;
  failedCount: number;
  warnCount: number;
  checks: PreflightCheck[];
}

export interface StressScenario {
  name: string;
  description: string;
  lossUsd: number;
  lossPct: number;
  postEquity: number;
  status: "ok" | "warn" | "fail";
}

export interface StressTestResponse {
  generatedUtc: string;
  currentEquity: number;
  openNotional: number;
  currentUnrealisedPnl: number;
  scenarios: StressScenario[];
}

export const dashboardIntelApi = {
  dailyBrief: () => api.get<DailyBrief>("/api/dashboard/daily-brief"),
  preflight: () => api.get<PreflightResponse>("/api/dashboard/preflight"),
  stressTest: () => api.get<StressTestResponse>("/api/dashboard/stress-test"),
};
