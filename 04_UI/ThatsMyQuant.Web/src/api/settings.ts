import { api } from "./client";

export interface ExchangeSettings {
  mode: string;
  hasApiCredentials: boolean;
  isDemo: boolean;
}

export interface TelegramSettings {
  configured: boolean;
  hasChatId: boolean;
}

export interface RiskSettings {
  maxDrawdownPercent: number;
  defaultLeverage: number;
  maxRequestedLeverage: number;
}

export interface PaperSettings {
  timeframe: string;
  defaultLeverage: number;
  maxLeverage: number;
  maxConcurrentPositions: number;
  maxTotalExposurePercent: number;
  syncWithExchange: boolean;
}

export interface PromotionSettings {
  backtestMinTrades: number;
  backtestMinProfitFactor: number;
  backtestMinWinRate: number;
  paperMinTrades: number;
  paperMinProfitFactor: number;
  paperMinWinRate: number;
}

export interface Settings {
  exchange: ExchangeSettings;
  telegram: TelegramSettings;
  risk: RiskSettings;
  paper: PaperSettings;
  promotion: PromotionSettings;
}

export interface WritableKeySpec {
  key: string;
  kind: "bool" | "int" | "number" | "string";
  min?: number | null;
  max?: number | null;
}

export interface WritableSchema {
  keys: WritableKeySpec[];
}

export interface RuntimeOverrides {
  path: string;
  overrides: Record<string, unknown>;
}

export interface PatchResult {
  applied: number;
  accepted: Record<string, unknown>;
  rejected: Record<string, string>;
}

export interface ExchangeCredentialsStatus {
  path: string;
  okxLiveConfigured: boolean;
  okxDemoConfigured: boolean;
  binanceConfigured: boolean;
  os: string;
  note: string;
}

export interface ExchangeCredentialsRequest {
  okxApiKey?: string;
  okxSecretKey?: string;
  okxPassphrase?: string;
  okxDemoApiKey?: string;
  okxDemoSecretKey?: string;
  okxDemoPassphrase?: string;
  binanceApiKey?: string;
  binanceApiSecret?: string;
  setDemoMode?: boolean;
  clearOkxLive?: boolean;
  clearOkxDemo?: boolean;
  clearBinance?: boolean;
}

export const settingsApi = {
  get: () => api.get<Settings>("/api/settings"),
  writable: () => api.get<WritableSchema>("/api/settings/writable"),
  runtime: () => api.get<RuntimeOverrides>("/api/settings/runtime"),
  patch: (body: Record<string, unknown>) =>
    api.patch<PatchResult>("/api/settings/runtime", body),
  credentialsStatus: () =>
    api.get<ExchangeCredentialsStatus>("/api/settings/exchange-credentials"),
  saveCredentials: (req: ExchangeCredentialsRequest) =>
    api.post<ExchangeCredentialsStatus>("/api/settings/exchange-credentials", req),
  setExchangeMode: (mode: string, isDemo: boolean) =>
    api.post<{ mode: string; isDemo: boolean; path: string; note: string }>(
      "/api/settings/exchange-mode",
      { mode, isDemo }
    ),
};
