import { api } from "./client";

export interface FundingRateRow {
  symbol: string;
  currentRate: number;
  rateBps: number;
  signal: string;
  sparklineBps: number[];
  sparklinePointCount: number;
  lastUpdatedUtc: string;
}

export interface FundingHistoryPoint {
  timestamp: string;
  rate: number;
  rateBps: number;
}

export const fundingApi = {
  rates: (symbols?: string[]) => {
    const q = symbols && symbols.length > 0 ? `?symbols=${encodeURIComponent(symbols.join(","))}` : "";
    return api.get<FundingRateRow[]>(`/api/funding/rates${q}`);
  },
  history: (symbol: string, fromUtc?: string, toUtc?: string) => {
    const params = new URLSearchParams();
    if (fromUtc) params.set("from", fromUtc);
    if (toUtc) params.set("to", toUtc);
    const qs = params.toString();
    return api.get<FundingHistoryPoint[]>(`/api/funding/history/${encodeURIComponent(symbol)}${qs ? `?${qs}` : ""}`);
  },
};
