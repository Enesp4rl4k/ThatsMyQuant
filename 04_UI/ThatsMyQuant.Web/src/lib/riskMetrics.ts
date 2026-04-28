// Client-side risk math from equity timeline + open positions.
// VaR, CVaR, drawdown, concentration — derived from existing dashboard data.

import type { EquityPoint } from "@/api/dashboard";
import type { Position } from "@/api/types";

export interface RiskSummary {
  /** 1-day historical VaR at 95% — the 5th-percentile daily loss. */
  var95: number;
  /** 1-day historical VaR at 99%. */
  var99: number;
  /** Conditional VaR at 95% — average of the worst 5% daily losses. */
  cvar95: number;
  /** Maximum historical drawdown (% from peak). */
  maxDrawdownPct: number;
  /** Current drawdown vs all-time peak. */
  currentDrawdownPct: number;
  /** Days since the last new equity high. */
  daysUnderwater: number;
  /** % of total open notional held in the largest position's symbol. */
  topConcentrationPct: number;
  /** Symbol of the largest position. */
  topConcentrationSymbol: string | null;
  /** Average daily return (used as Sharpe input). */
  avgDailyReturn: number;
  /** Stdev of daily returns. */
  dailyVol: number;
  /** Annualised Sharpe ratio. */
  sharpeAnnualised: number;
}

export function computeRiskSummary(
  equity: EquityPoint[],
  openPositions: Position[]
): RiskSummary | null {
  if (equity.length < 5) return null;

  // Daily returns (P&L / cumulative equity at start of day)
  const returns: number[] = [];
  for (let i = 1; i < equity.length; i++) {
    const prev = equity[i - 1].cumulativeEquity;
    const curr = equity[i].cumulativeEquity;
    if (prev !== 0) {
      returns.push((curr - prev) / Math.abs(prev));
    }
  }

  if (returns.length < 3) {
    return zeroRiskSummary(openPositions);
  }

  // Sort ascending for percentiles (VaR uses left tail)
  const sorted = [...returns].sort((a, b) => a - b);
  const idx95 = Math.max(0, Math.floor(sorted.length * 0.05));
  const idx99 = Math.max(0, Math.floor(sorted.length * 0.01));
  const var95 = -sorted[idx95]; // positive number = expected loss
  const var99 = -sorted[idx99];
  // CVaR — mean of all returns ≤ var95 cutoff
  const tail = sorted.slice(0, idx95 + 1);
  const cvar95 = tail.length > 0 ? -tail.reduce((s, r) => s + r, 0) / tail.length : var95;

  // Drawdown
  let peak = equity[0].cumulativeEquity;
  let peakIdx = 0;
  let maxDd = 0;
  for (let i = 0; i < equity.length; i++) {
    if (equity[i].cumulativeEquity > peak) {
      peak = equity[i].cumulativeEquity;
      peakIdx = i;
    }
    const dd = peak > 0 ? (peak - equity[i].cumulativeEquity) / peak : 0;
    if (dd > maxDd) maxDd = dd;
  }
  const last = equity[equity.length - 1].cumulativeEquity;
  const currentDd = peak > 0 ? Math.max(0, (peak - last) / peak) : 0;
  const daysUnderwater = equity.length - 1 - peakIdx;

  // Volatility
  const mean = returns.reduce((s, r) => s + r, 0) / returns.length;
  const variance = returns.reduce((s, r) => s + (r - mean) ** 2, 0) / Math.max(1, returns.length - 1);
  const stdev = Math.sqrt(variance);
  const sharpe = stdev > 0 ? (mean / stdev) * Math.sqrt(252) : 0;

  // Concentration
  const conc = computeConcentration(openPositions);

  return {
    var95: var95 * 100,
    var99: var99 * 100,
    cvar95: cvar95 * 100,
    maxDrawdownPct: maxDd * 100,
    currentDrawdownPct: currentDd * 100,
    daysUnderwater,
    topConcentrationPct: conc.pct,
    topConcentrationSymbol: conc.symbol,
    avgDailyReturn: mean * 100,
    dailyVol: stdev * 100,
    sharpeAnnualised: sharpe,
  };
}

function zeroRiskSummary(open: Position[]): RiskSummary {
  const conc = computeConcentration(open);
  return {
    var95: 0,
    var99: 0,
    cvar95: 0,
    maxDrawdownPct: 0,
    currentDrawdownPct: 0,
    daysUnderwater: 0,
    topConcentrationPct: conc.pct,
    topConcentrationSymbol: conc.symbol,
    avgDailyReturn: 0,
    dailyVol: 0,
    sharpeAnnualised: 0,
  };
}

function computeConcentration(positions: Position[]): { pct: number; symbol: string | null } {
  if (positions.length === 0) return { pct: 0, symbol: null };
  const notionals = positions.map((p) => ({
    symbol: p.symbol,
    notional: Math.abs(p.quantity * p.markPrice),
  }));
  const total = notionals.reduce((s, x) => s + x.notional, 0);
  if (total === 0) return { pct: 0, symbol: null };
  const top = notionals.reduce((b, x) => (x.notional > b.notional ? x : b));
  return { pct: (top.notional / total) * 100, symbol: top.symbol };
}

/** Multi-period aggregation from equity timeline. */
export interface PeriodReturn {
  label: string;
  days: number;
  pnl: number;
  pct: number;
  trades: number;
  bestDay: number | null;
  worstDay: number | null;
}

export function computeMultiPeriod(equity: EquityPoint[]): PeriodReturn[] {
  if (equity.length === 0) return [];

  const periods = [
    { label: "1D", days: 1 },
    { label: "7D", days: 7 },
    { label: "30D", days: 30 },
    { label: "90D", days: 90 },
    { label: "YTD", days: yearToDateDays() },
  ];

  return periods.map((p) => {
    const slice = equity.slice(-p.days);
    if (slice.length === 0) {
      return { label: p.label, days: p.days, pnl: 0, pct: 0, trades: 0, bestDay: null, worstDay: null };
    }
    const pnl = slice.reduce((s, x) => s + x.dailyPnL, 0);
    const startEq = slice[0].cumulativeEquity - slice[0].dailyPnL;
    const pct = startEq !== 0 ? (pnl / Math.abs(startEq)) * 100 : 0;
    const trades = slice.reduce((s, x) => s + x.tradeCount, 0);
    const days = slice.map((x) => x.dailyPnL);
    const bestDay = days.length > 0 ? Math.max(...days) : null;
    const worstDay = days.length > 0 ? Math.min(...days) : null;
    return { label: p.label, days: p.days, pnl, pct, trades, bestDay, worstDay };
  });
}

function yearToDateDays(): number {
  const now = new Date();
  const start = new Date(now.getFullYear(), 0, 1);
  return Math.max(1, Math.floor((now.getTime() - start.getTime()) / (1000 * 60 * 60 * 24)));
}
