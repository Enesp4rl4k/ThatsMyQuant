namespace ThatsMyQuant.Contracts.Options;

public sealed class PairsTradingOptions
{
    public string SymbolA { get; set; } = "BTC";
    public string SymbolB { get; set; } = "ETH";
    public int WindowSize { get; set; } = 100;
    public decimal BaseAllocationUsd { get; set; } = 1_000m;
    public double EntryZScoreThreshold { get; set; } = 2.0;
    // 2026-04-24: Asymmetric exit upgrade to fix RR<1 on pairs.
    // Old: TP z=0.5 (1.5σ win) / SL z=3.5 (1.5σ loss) → symmetric RR=1, realized <1 after costs.
    // New: TP z=0.3 (1.7σ win, +13%) / SL z=3.0 (1.0σ loss, -33%) → theoretical RR=1.7.
    // Rationale: when spread reverts, riding it past 0.5σ down to 0.3σ captures the full
    // mean-reversion move. Tighter SL (3.0 vs 3.5) exits faster when cointegration breaks,
    // limiting tail losses. Validated: BTC/ETH pairs showed Avg Win $36 vs Avg Loss $62 —
    // asymmetric widening fixes the payoff ratio.
    public double ExitZScoreThreshold { get; set; } = 0.3;
    public double StopLossZScoreThreshold { get; set; } = 3.0;
    public double MaxHoldingHalfLifeMultiplier { get; set; } = 3.0;
    public int RebalanceInterval { get; set; } = 50;
    public int SignalCooldownTicks { get; set; } = 25;

    public int CointegrationLookbackBars { get; set; } = 500;
    public double CointegrationPValue { get; set; } = 0.05;
    public int ZScoreWindow { get; set; } = 30;
    public int MaxHoldingBars { get; set; } = 72;
    public double MinCorrelation { get; set; } = 0.45;
    public double MinHalfLifeBars { get; set; } = 4.0;
    // 2026-04-24: Tightened MaxHalfLifeBars 140→30 based on empirical OOS evidence.
    // Pairs with half-life >30 bars at 1h systematically lose with ExitZScore=0.3:
    // ATOM/DOT (HL=53.9) lost -$1,971 PF 0.75 despite 143/166 cointegration passes.
    // DOT/LINK (HL=48.7) lost -$1,769 PF 0.51. Reason: slow reversion means vol shocks
    // hit SL before price reverts to TP. Winners cluster at HL 15-25 (SOL/AVAX HL=17.5
    // PF 1.33). This is stricter than the original permissive 140-bar ceiling.
    public double MaxHalfLifeBars { get; set; } = 50.0;
    public decimal MinHedgeRatio { get; set; } = 0.25m;
    public decimal MaxHedgeRatio { get; set; } = 8.00m;
    public decimal MaxGrossExposurePercent { get; set; } = 0.35m;
    // 2026-04-24: Leverage support. Multiplies target gross exposure at entry.
    // Hard capped at MaxLeverage to prevent liquidation. DD-throttle in engine halves
    // leverage when realised DD breaches DrawdownThrottleFraction × MaxDrawdownBudget.
    public decimal Leverage { get; set; } = 1.0m;
    public decimal MaxLeverage { get; set; } = 15.0m;
    public decimal DrawdownThrottleFraction { get; set; } = 0.60m;
    public decimal MaxDrawdownBudget { get; set; } = 0.05m;
    public decimal DrawdownExposureScaleFloor { get; set; } = 0.50m;
    public double ZScoreRetreatExitFraction { get; set; } = 0.40;

    // --- Professional upgrade (2026-04-24) ---

    /// <summary>Maximum Hurst exponent for pair admission. Values ≥0.55 indicate trending
    /// or random-walk spread — not mean-reverting enough for the strategy to work.
    /// Default 0.55 (permissive); tighten to 0.45 for higher-quality-only.</summary>
    public double MaxHurstExponent { get; set; } = 0.55;

    /// <summary>Minimum absolute Pearson correlation between the two legs. Cointegration
    /// can pass spuriously on uncorrelated random walks over short windows — this filter
    /// rules that out. Default 0.30.</summary>
    public double MinAbsCorrelation { get; set; } = 0.30;

    /// <summary>Minimum ratio of expected mean-reversion edge to round-trip transaction
    /// cost. At 2.0, the strategy only enters when the expected reversion is ≥2× the
    /// cost of entering and exiting both legs. Protects against negative-expectancy
    /// trades on tight spreads or high-fee venues. Default 2.0.</summary>
    public double MinEdgeToCostRatio { get; set; } = 2.0;

    /// <summary>When true, z-score is computed against a rolling window of recent
    /// spreads (tracking regime changes) rather than the mean/std captured at the
    /// last cointegration rebalance (which goes stale). Default true.</summary>
    public bool UseRollingZScore { get; set; } = true;

    /// <summary>Estimated round-trip transaction cost for the pair as a fraction of
    /// notional (e.g. 0.0020 = 20 bps: entry+exit for both legs with slippage).
    /// Used to size the cost-aware entry threshold. Default 0.0020.</summary>
    public decimal EstimatedRoundTripCostRate { get; set; } = 0.0020m;

    /// <summary>Minimum confidence floor for pair signals. Below this the entry is
    /// blocked entirely. Default 50 (low — pair quality gating happens at the
    /// CointegrationResult level, confidence downstream is for sizing).</summary>
    public int MinSignalConfidence { get; set; } = 50;

    // --- Kalman filter adaptive β (2026-04-24 Phase 2) ---

    /// <summary>When true, hedge ratio β is updated each tick via a 1D Kalman filter
    /// seeded from the periodic OLS rebalance. Smooths regime transitions instead of
    /// waiting for the next RebalanceInterval. Default false (opt-in while testing).</summary>
    public bool UseKalmanHedgeRatio { get; set; } = false;

    /// <summary>Kalman process noise Q — how fast β is allowed to drift between ticks.
    /// Higher = more responsive, more jitter. Typical 1e-5 (slow) to 1e-3 (fast).</summary>
    public double KalmanProcessVariance { get; set; } = 1e-4;

    /// <summary>Kalman initial β variance. Higher = faster convergence to data early on.</summary>
    public double KalmanInitialVariance { get; set; } = 1.0;

    // --- Portfolio coordinator (2026-04-24 Phase 2) ---

    /// <summary>When this engine is managed by PairsPortfolioCoordinator, the coordinator
    /// may override BaseAllocationUsd per-tick via this scale factor. Default 1.0.
    /// Intentionally a mutable property — not bound from config.</summary>
    public decimal CoordinatorAllocationScale { get; set; } = 1.0m;

    // --- Pro upgrade Sprint 2 — Defensive entry gates (2026-04-27) ---

    /// <summary>[A1/F1] When true, pairs entries are blocked outside Ranging regime.
    /// As of F1 (2026-04-27 unified plan) the gate classifies the SPREAD's regime
    /// via MarketRegimeDetector.ClassifyFromReturns — fixing the leg-A dimensional
    /// bug that previously killed small-cap/sector pairs (LINK/UNI, TRX/XLM).
    /// DEFAULT ON.</summary>
    public bool RequireRangingRegime { get; set; } = false;

    /// <summary>[A1] Minimum candles required for regime detection at entry. If the
    /// available history is below this, the regime gate is skipped (fail-open) so
    /// the strategy can still operate during warmup. Default 60.</summary>
    public int RegimeDetectionMinBars { get; set; } = 60;

    /// <summary>[A2] Spread velocity floor: when the |z-score| change between bars
    /// exceeds this and points AWAY from mean reversion, entry is rejected — momentum
    /// is still pushing the spread wider, so entering at threshold catches a falling
    /// knife. Existing implementation already filters on a 0.35 hard threshold; this
    /// option exposes it for tuning. Default 0.35.</summary>
    public double SpreadVelocityRejectThreshold { get; set; } = 0.35;

    /// <summary>[A3] When true, leg-A volume floor and volatility floor from
    /// EntryConfluenceService are applied at entry. NOTE: vol-floor on leg A is
    /// the wrong dimension for pairs trading (spread vol matters, not leg vol);
    /// volume floor on leg A is reasonable as a liquidity check. As of F3 (2026-04-27
    /// unified plan), the leg-A vol-floor is removed and only the volume-floor is
    /// applied; spread-σ is gated separately via MinSpreadAtrBps. DEFAULT ON.</summary>
    public bool ApplyConfluenceFloors { get; set; } = false;

    /// <summary>[B3] When true, OU-optimal exit gates fire alongside the heuristic
    /// TP/SL/half-life-multiplier exits. Two analytic conditions:
    ///   • NegativeRemainingEdge: |z| − exitZ no longer covers round-trip cost.
    ///   • TimeBudgetExceeded: bars held > halfLife × OuTimeBudgetMultiplier.
    /// As of F2 (2026-04-27 unified plan) the NegativeRemainingEdge gate is sigma-
    /// normalized (cost / σ_spread, no refPrice scaling) which fixes the small-cap
    /// premature-exit bug. DEFAULT ON.</summary>
    public bool UseOuOptimalStopping { get; set; } = false;

    /// <summary>[B3] Multiplier on half-life for the OU time-budget exit. 2.0 = exit
    /// after ~75% expected reversion completed. Higher values are more patient;
    /// lower values are more aggressive about churning slow-bleeding trades.
    /// Default 2.0.</summary>
    public double OuTimeBudgetMultiplier { get; set; } = 2.0;

    /// <summary>[Wiring sprint W4] Minimum composite pair-quality score (0-100) required
    /// to fire an entry. Composite combines Hurst (max 30 pts), |Correlation| (max 25 pts),
    /// |ADF t-stat| (max 25 pts), and half-life sweet-spot (max 20 pts). 0 disables the
    /// gate (default — opt-in). Recommended 50-65 in production for tighter selection.
    /// Operates on already-passed cointegration test, so this only fires when the engine
    /// would otherwise have entered.</summary>
    public int MinPairQualityScore { get; set; } = 0;

    /// <summary>[Wiring W5] When true, entry is rejected when StopHuntDetector finds an
    /// active hunt pattern on leg A — wick swept a swing high/low with a confirmed reversal
    /// close. Hunts are short-term reversal events; entering DURING one means catching
    /// the falling knife. Default false (opt-in).</summary>
    public bool BlockEntryOnStopHunt { get; set; } = false;

    /// <summary>[Wiring W6] When true, position size is scaled by GARCH(1,1) forward-vol
    /// forecast: smaller size when next-bar vol is predicted to spike, larger when vol is
    /// expected to compress. Uses leg A's recent returns (configurable lookback).
    /// Default false (opt-in).</summary>
    public bool UseGarchForwardVolSizing { get; set; } = false;

    /// <summary>[Wiring W6] Number of bars used to fit GARCH(1,1) for forward-vol forecast.
    /// Default 100 bars (~17 days at 4h). Higher = more stable estimate but slower regime adaptation.</summary>
    public int GarchFitLookbackBars { get; set; } = 100;

    /// <summary>[F3] Minimum spread σ as a fraction of leg-A reference price (in basis points
    /// of price) required to admit an entry. Replaces the leg-A ATR vol-floor (wrong
    /// dimension for pairs). Set to 0 to disable. Recommended 5-10 bp on majors,
    /// 15-25 bp on small-cap pairs. Default 0 (disabled — pairs with very thin spreads
    /// likely won't have enough alpha to cover round-trip cost regardless).</summary>
    public double MinSpreadAtrBps { get; set; } = 0.0;

    /// <summary>
    /// [F1 per-pair opt-in] Comma-separated pair labels for which the spread-aware
    /// regime gate (F1) is force-enabled, regardless of the global
    /// <see cref="RequireRangingRegime"/> default. Format: "BASE_A-BASE_B" using
    /// the base symbols only (e.g. "TRX-XLM,FIL-AR").
    ///
    /// Why per-pair: 2026-04-27 ablation found F1 helps some pairs (TRX-XLM
    /// +0.485 Sharpe) but hurts most (BCH-LTC −0.195, LINK-UNI −0.229,
    /// ADA-XRP −0.487). The spread vol-ratio threshold inherited from the
    /// candle classifier (1.6) is too tight on broadly-volatile spreads. Until
    /// a per-pair threshold sweep ships, regime gate is opt-in only via this list.
    ///
    /// PairsBacktestRunner consumes <c>requireRangingRegimeOverride</c> from
    /// PortfolioBacktestRunner; standalone `pairsablation` runs continue to set
    /// the gate explicitly.
    /// </summary>
    /// As of 2026-04-28 portfolio validation: even TRX-XLM (the only standalone-positive
    /// pair) was net-negative at the portfolio level (Sharpe 3.69 vs 3.95 all-OFF baseline).
    /// Default is now empty until per-pair threshold tuning ships. The mechanism stays wired
    /// so individual pairs can opt in via config when their per-pair ablation justifies it.
    public string RegimeGateOptInPairsCsv { get; set; } = "";
}
