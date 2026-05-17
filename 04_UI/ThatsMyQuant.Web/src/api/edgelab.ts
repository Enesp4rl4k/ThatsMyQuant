import { api } from "./client";

export interface CalibrationRecord {
  strategy: string;
  lookback_days: number;
  n_realized_days: number;
  realized: {
    sharpe_ann: number;
    total_return: number;
    max_drawdown: number;
    hit_rate: number;
  };
  promotion_gate: {
    pass_n_days: boolean;
    pass_n_trades: boolean;
    "pass_sharpe_0.4": boolean;
    pass_dd_25: boolean;
    live_canary_eligible: boolean;
  };
  calibration: string;
}

export interface EdgeLabStrategyRow {
  name: string;
  weight: number | null;
  promotion_status: string | null;
  stale_alert_hours: number | null;
  generated_at: string | null;
  as_of_bar: string | null;
  signal_age_sec: number | null;
  state: string | null;
  expected_sharpe_ann: number | null;
  target_weights: Record<string, number> | null;
  stale: boolean;
  calibration: CalibrationRecord | null;
}

export interface MakerFillRatioState {
  ComputedAtUtc: string;
  LookbackDays: number;
  Threshold: number;
  TotalFills: number;
  MakerFills: number;
  TakerFills: number;
  AggregateRatio: number | null;
  EntryFills: number;
  EntryMakerFills: number;
  EntryTakerFills: number;
  EntryRatio: number | null;
  MarketExitFills: number;
  Symbols: Array<{
    Symbol: string;
    EntryFills: number;
    EntryMakerFills: number;
    EntryTakerFills: number;
    EntryRatio: number | null;
    BelowThreshold: boolean;
  }>;
}

export interface HrpAllocatorState {
  ComputedAtUtc: string;
  LookbackDays: number;
  Strategies: Array<{ Tag: string; TradeCount: number; HrpWeight: number }>;
  FellBackToEqualWeight: boolean;
  Verdict: string;
}

export interface EdgeKillSwitchState {
  GeneratedUtc: string;
  Strategies: Record<string, unknown>;
}

export interface EdgeLabLiveSnapshot {
  computed_at_utc: string;
  state_dir: string;
  strategies: EdgeLabStrategyRow[];
  maker_fill_ratio: MakerFillRatioState | null;
  funding_fee_ratio: unknown;
  pairwise_correlation: unknown;
  hrp_allocator: HrpAllocatorState | null;
  edge_kill_switch: EdgeKillSwitchState | null;
}

export const edgeLabApi = {
  live: () => api.get<EdgeLabLiveSnapshot>("/api/edgelab/live"),
};
