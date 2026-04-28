export interface PortfolioSummary {
  balance: number;
  realizedPnL: number;
  unrealizedPnL: number;
  totalEquity: number;
  totalFeesPaid: number;
  netFunding: number;
  openPositionCount: number;
  closedTradeCount: number;
  winningTradeCount: number;
  losingTradeCount: number;
  winRate: number;
  profitFactor: number;
  averageRiskReward: number;
  averageTradePnL: number;
}

export interface Position {
  id: string;
  symbol: string;
  side: "LONG" | "SHORT";
  quantity: number;
  remainingQuantity: number;
  entryPrice: number;
  markPrice: number;
  unrealizedPnL: number;
  roe: number;
  leverage: number;
  lifecycleStatus: string;
  exitPolicyTag: string;
  strategyTag: string;
  stopLoss: number;
  takeProfit: number;
  entryTime: string;
}

export interface TradeJournal {
  id: string;
  symbol: string;
  side: "LONG" | "SHORT";
  entryPrice: number;
  exitPrice: number;
  quantity: number;
  realizedPnL: number;
  pnlPercent: number;
  status: string;
  lifecycleStatus: string;
  executionMode: string;
  exitReason: string;
  strategyTag: string;
  entryTime: string;
  exitTime: string | null;
}

export interface PositionDetail {
  position: Position;
  trade: TradeJournal;
  reasoning: string;
  maxFavorableExcursionPercent: number;
  maxAdverseExcursionPercent: number;
  signalToFillDriftBps: number;
  totalFeesPaid: number;
  netFunding: number;
  mlModelVersion: string;
  promotionStage: string;
}

export interface ExchangeHealth {
  venue: string;
  credentialsConfigured: boolean;
  isConnected: boolean;
  isDemoMode: boolean;
  balance: number | null;
  lastSuccessUtc: string | null;
  lastAttemptUtc: string | null;
  lastError: string | null;
  consecutiveFailures: number;
}
