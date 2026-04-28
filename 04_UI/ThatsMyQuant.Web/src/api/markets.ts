import { api } from "./client";

export interface MarketSymbol {
  symbol: string;
  lastPrice: number;
  changePct24h: number;
  sparkline: number[];
}

export interface MarketCandle {
  time: string;
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
}

export type Timeframe = "5m" | "15m" | "30m" | "1h" | "4h" | "1d" | "1w";

export const TIMEFRAMES: { value: Timeframe; label: string }[] = [
  { value: "5m", label: "5m" },
  { value: "15m", label: "15m" },
  { value: "30m", label: "30m" },
  { value: "1h", label: "1H" },
  { value: "4h", label: "4H" },
  { value: "1d", label: "1D" },
  { value: "1w", label: "1W" },
];

export interface OrderBookLevel {
  price: number;
  size: number;
  cumulative: number;
}

export interface OrderBook {
  symbol: string;
  midPrice: number;
  spreadBps: number;
  bids: OrderBookLevel[];
  asks: OrderBookLevel[];
  isSynthetic: boolean;
}

export interface SymbolAnalysis {
  symbol: string;
  timeframe: string;
  lastPrice: number;
  rsi14: number;
  ma20: number;
  ma50: number;
  atr14: number;
  atrPct: number;
  regime: string;
  bias: string;
  volatility24h: number;
  notes: string[];
}

export const marketsApi = {
  symbols: (include?: string[]) => {
    const q = include && include.length > 0 ? `?include=${include.join(",")}` : "";
    return api.get<MarketSymbol[]>(`/api/markets/symbols${q}`);
  },
  candles: (symbol: string, timeframe: Timeframe = "1h", limit = 200) =>
    api.get<MarketCandle[]>(
      `/api/markets/candles/${encodeURIComponent(symbol)}?timeframe=${timeframe}&limit=${limit}`
    ),
  orderbook: (symbol: string) =>
    api.get<OrderBook>(`/api/markets/orderbook/${encodeURIComponent(symbol)}`),
  analysis: (symbol: string, timeframe: Timeframe = "1h") =>
    api.get<SymbolAnalysis>(
      `/api/markets/analysis/${encodeURIComponent(symbol)}?timeframe=${timeframe}`
    ),
  narrative: (symbol: string, timeframe: Timeframe = "1h") =>
    api.get<MarketNarrative>(
      `/api/markets/narrative/${encodeURIComponent(symbol)}?timeframe=${timeframe}`
    ),
  submitOrder: (req: ManualOrderRequest) =>
    api.post<ManualOrderResponse>(`/api/markets/orders`, req),
};

export interface ManualOrderRequest {
  symbol: string;
  side: "long" | "short";
  orderType: "market" | "limit" | "stop";
  quantity: number;
  entry: number;
  stopLoss?: number;
  takeProfit?: number;
  leverage?: number;
  riskPct: number;
}

export interface ManualOrderResponse {
  signalId: string;
  accepted: boolean;
  status: string;
  reasoning: string;
}

export interface MarketNarrative {
  symbol: string;
  timeframe: string;
  generatedUtc: string;
  body: string;
  bias: string;
  bullets: string[];
}
