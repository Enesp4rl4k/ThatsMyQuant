import { api } from "./client";
import type {
  ExchangeHealth,
  PortfolioSummary,
  Position,
  PositionDetail,
  TradeJournal,
} from "./types";

export interface TradeFilters {
  limit?: number;
  strategy?: string;
  status?: "open" | "closed" | "";
  from?: string;
  to?: string;
}

export interface StrategyBreakdown {
  strategyTag: string;
  tradeCount: number;
  winCount: number;
  lossCount: number;
  winRate: number;
  profitFactor: number;
  netPnl: number;
  averagePnl: number;
  sharpe: number;
  maxWin: number;
  maxLoss: number;
  lastTradeUtc: string;
}

export interface SignalSnapshot {
  id: string;
  symbol: string;
  side: string;
  price: number;
  quantity: number;
  confidence: number;
  reasoning: string;
  status: string;
  strategyTag: string;
  regimeTag: string;
  source: string;
  exchange: string;
  timestamp: string;
  reasonFlags: string;
}

export interface EquityPoint {
  date: string;
  dailyPnL: number;
  cumulativeEquity: number;
  tradeCount: number;
}

function buildTradeQuery(f: TradeFilters & { executionMode?: string }): string {
  const p = new URLSearchParams();
  p.set("limit", String(f.limit ?? 100));
  if (f.strategy) p.set("strategy", f.strategy);
  if (f.status) p.set("status", f.status);
  if (f.from) p.set("from", f.from);
  if (f.to) p.set("to", f.to);
  if (f.executionMode) p.set("executionMode", f.executionMode);
  return p.toString();
}

export const dashboardApi = {
  portfolio: (executionMode?: string) =>
    api.get<PortfolioSummary>(`/api/portfolio${executionMode ? `?executionMode=${encodeURIComponent(executionMode)}` : ""}`),
  positions: (executionMode?: string) =>
    api.get<Position[]>(`/api/positions${executionMode ? `?executionMode=${encodeURIComponent(executionMode)}` : ""}`),
  positionDetail: (id: string) => api.get<PositionDetail>(`/api/positions/${id}`),
  recentTrades: (limit = 50, executionMode?: string) =>
    api.get<TradeJournal[]>(`/api/trades?limit=${limit}${executionMode ? `&executionMode=${encodeURIComponent(executionMode)}` : ""}`),
  filteredTrades: (filters: TradeFilters & { executionMode?: string }) =>
    api.get<TradeJournal[]>(`/api/trades?${buildTradeQuery(filters)}`),
  strategyBreakdown: () => api.get<StrategyBreakdown[]>("/api/trades/by-strategy"),
  recentSignals: (limit = 25) => api.get<SignalSnapshot[]>(`/api/signals/recent?limit=${limit}`),
  equityTimeline: () => api.get<EquityPoint[]>("/api/portfolio/equity-timeline"),
  exchangeHealth: () => api.get<ExchangeHealth>("/api/exchange/health"),
  closePosition: (req: { symbol: string; side?: "buy" | "sell"; reason?: string }) =>
    api.post<ClosePositionResponse>("/api/positions/close", req),
  flattenAll: (reason?: string) =>
    api.post<ClosePositionResponse>("/api/positions/flatten-all", { reason }),
  paperStatePositions: () => api.get<PaperStatePositionsResponse>("/api/paper/state-positions"),
  exchangePositions:   () => api.get<ExchangePositionsResponse>("/api/paper/exchange-positions"),
  paperPortfolio:      () => api.get<PaperPortfolioSnapshot>("/api/paper/portfolio"),
};

export interface PaperPortfolioSnapshot {
  found: boolean;
  filePath: string;
  lastWriteUtc: string | null;
  initialBalance: number;
  cash: number;
  totalEquity: number;
  totalPnL: number;
  totalPnLPercent: number;
  dailyPnL: number;
  openCount: number;
  closedCount: number;
  winningTrades: number;
  winRate: number;
  note: string | null;
}

export interface PaperStatePosition {
  tier: string;                     // "Funding Long" | "Funding Short" | "Pairs Long" | "Pairs Short"
  symbol: string;
  side: "LONG" | "SHORT";
  quantity: number;
  entryPrice: number;
  entryFunding?: number | null;
  maxFavorablePrice?: number | null;
  entryTimeUtc: string;
  executionMode: string;            // "PAPER" | "PAPER_DRYRUN" | "OKX_DEMO" | ...
  clientOrderId?: string | null;
}

export interface PaperStateSource {
  tier: string;
  fileName: string;
  found: boolean;
  count: number;
  lastWriteUtc: string | null;
}

export interface PaperStatePositionsResponse {
  sources: PaperStateSource[];
  positions: PaperStatePosition[];
  count: number;
  fetchedUtc: string;
}

export interface ExchangePosition {
  symbol: string;
  quantity: number;
  averageEntryPrice: number;
  unrealizedPnL: number;
  side: "LONG" | "SHORT";
}

export interface ExchangePositionsResponse {
  provider: string;
  accountId: string;
  balance: number;
  equity: number;
  positions: ExchangePosition[];
  fetchedUtc: string;
}

export interface ClosePositionResponse {
  id: number;
  status: string;
  symbol: string;
}
