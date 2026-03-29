namespace ThatsMyQuant.Contracts.Options;

/// <summary>
/// Root configuration section ("Trading" in appsettings.json).
/// All previously hardcoded trading constants live here.
/// </summary>
public sealed class TradingOptions
{
    public const string SectionName = "Trading";

    public RiskOptions Risk { get; set; } = new();
    public BacktestOptions Backtest { get; set; } = new();
    public SwarmOptions Swarm { get; set; } = new();
    public MomentumOptions Momentum { get; set; } = new();
    public BreakoutOptions Breakout { get; set; } = new();
    public ScalpingOptions Scalping { get; set; } = new();
    public StatArbOptions StatArb { get; set; } = new();
    public VolatilityExpansionOptions VolatilityExpansion { get; set; } = new();
    public MlOptions Ml { get; set; } = new();
    public TickReplayOptions TickReplay { get; set; } = new();
}

// ── Risk Agent ──────────────────────────────────────────────────────────────

public sealed class RiskOptions
{
    /// <summary>Token bucket capacity (max burst).</summary>
    public int RateLimitCapacity { get; set; } = 10;

    /// <summary>Token bucket refill rate (tokens/sec).</summary>
    public double RateLimitRefillRate { get; set; } = 10.0;

    /// <summary>Fallback equity when no starting equity is provided.</summary>
    public decimal EquityFallback { get; set; } = 100_000m;

    /// <summary>Max concurrent open trades across all symbols.</summary>
    public int MaxConcurrentTrades { get; set; } = 5;

    /// <summary>Max concurrent open trades per symbol.</summary>
    public int MaxTradesPerSymbol { get; set; } = 2;

    /// <summary>Consecutive losses on a symbol before blocking new entries.</summary>
    public int LossStreakLimit { get; set; } = 3;

    /// <summary>Kelly fraction for high-volume (HFT) symbols.</summary>
    public decimal KellyFractionHft { get; set; } = 0.25m;

    /// <summary>Kelly fraction for low-volume (swing) symbols.</summary>
    public decimal KellyFractionSwing { get; set; } = 0.5m;

    /// <summary>Volume threshold that separates HFT from swing Kelly sizing.</summary>
    public decimal HftVolumeThreshold { get; set; } = 500_000m;

    /// <summary>Max order size as fraction of average daily volume.</summary>
    public decimal MaxAdvFraction { get; set; } = 0.02m;

    /// <summary>Minimum Kelly allocation below which the trade is rejected (negative edge).</summary>
    public decimal MinKellyAllocation { get; set; } = 0.0001m;

    /// <summary>Reward/risk ratio clamp range.</summary>
    public decimal RewardRiskMin { get; set; } = 0.5m;
    public decimal RewardRiskMax { get; set; } = 5.0m;

    /// <summary>Default reward/risk ratio when SL/TP not available.</summary>
    public decimal DefaultRewardRisk { get; set; } = 1.5m;

    // ── Dynamic Kelly ──

    /// <summary>Minimum recent trades required to compute dynamic Kelly (below this, use static fractions).</summary>
    public int DynamicKellyMinTrades { get; set; } = 10;

    /// <summary>Window of recent trades for dynamic Kelly computation.</summary>
    public int DynamicKellyWindow { get; set; } = 50;

    /// <summary>Win rate below which Kelly fraction is halved (losing edge).</summary>
    public decimal DynamicKellyMinWinRate { get; set; } = 0.40m;

    /// <summary>Win rate above which Kelly fraction gets a bonus (strong edge).</summary>
    public decimal DynamicKellyStrongWinRate { get; set; } = 0.55m;

    // ── Drawdown Circuit Breaker ──

    /// <summary>Drawdown % at which position size starts scaling down (soft limit).</summary>
    public decimal DrawdownSoftLimitPct { get; set; } = 3.0m;

    /// <summary>Drawdown % at which trading halts entirely (hard limit).</summary>
    public decimal DrawdownHardLimitPct { get; set; } = 7.0m;

    /// <summary>Minimum position size multiplier at soft-to-hard drawdown range (0.1 = 10% of normal).</summary>
    public decimal DrawdownMinSizeMultiplier { get; set; } = 0.1m;

    // ── Correlation Guard ──

    /// <summary>Correlation threshold above which additional exposure is blocked.</summary>
    public double CorrelationThreshold { get; set; } = 0.7;

    /// <summary>Minimum candles needed to compute meaningful correlation.</summary>
    public int CorrelationMinCandles { get; set; } = 20;

    /// <summary>Maximum total portfolio exposure (sum of all position fractions) as a multiplier of equity.</summary>
    public decimal MaxPortfolioExposure { get; set; } = 0.8m;
}

// ── Backtest Engine ─────────────────────────────────────────────────────────

public sealed class BacktestOptions
{
    /// <summary>Base slippage fraction per side (bid-ask spread proxy).</summary>
    public decimal BaseSlippageFraction { get; set; } = 0.001m;

    /// <summary>Almgren-Chriss impact coefficient: extra slip = coeff * sqrt(participation).</summary>
    public decimal ImpactCoefficient { get; set; } = 0.001m;

    /// <summary>Taker fee per side (exchange commission).</summary>
    public decimal TakerFeeFraction { get; set; } = 0.001m;

    /// <summary>Slippage multiplier when candle has zero volume.</summary>
    public decimal ZeroVolumePenaltyMultiplier { get; set; } = 3m;

    /// <summary>Minimum candles required to run a backtest.</summary>
    public int MinCandlesRequired { get; set; } = 60;

    /// <summary>Minimum confidence from technical analysis to consider entry.</summary>
    public int MinTechnicalConfidence { get; set; } = 55;

    /// <summary>Minimum consensus confidence (after agent adjustment) to enter.</summary>
    public int MinConsensusConfidence { get; set; } = 55;

    /// <summary>ATR percentage threshold for high-volatility regime detection.</summary>
    public decimal RegimeHighVolAtrPct { get; set; } = 0.02m;

    /// <summary>SMA deviation threshold for trending detection in high-vol regime.</summary>
    public decimal RegimeHighVolTrendDeviation { get; set; } = 0.03m;

    /// <summary>SMA deviation threshold for trending detection in low-vol regime.</summary>
    public decimal RegimeLowVolTrendDeviation { get; set; } = 0.02m;

    /// <summary>Default starting capital for backtests.</summary>
    public decimal DefaultStartingCapital { get; set; } = 100_000m;
}

// ── Swarm Orchestrator ──────────────────────────────────────────────────────

public sealed class SwarmOptions
{
    /// <summary>ML predicted return below which a trade is vetoed.</summary>
    public float MlDownsideThreshold { get; set; } = -0.01f;

    /// <summary>Minimum weighted confidence score to approve a trade.</summary>
    public float SuccessThreshold { get; set; } = 0.6f;

    /// <summary>Weight of sentiment agent vote in confidence score.</summary>
    public float SentimentWeight { get; set; } = 0.3f;

    /// <summary>Weight of risk agent vote in confidence score.</summary>
    public float RiskWeight { get; set; } = 0.4f;

    /// <summary>Weight of ML inference in confidence score.</summary>
    public float MlWeight { get; set; } = 0.3f;

    /// <summary>Weight of strategy votes in confidence score.</summary>
    public float StrategyVoteWeight { get; set; } = 0.3f;

    /// <summary>Confidence boost from microstructure/OFI injection.</summary>
    public float OfiConfidenceBoost { get; set; } = 0.1f;

    /// <summary>ML-adjusted size clamp range (prevents degenerate sizing).</summary>
    public decimal MlSizeClampMin { get; set; } = 0.5m;
    public decimal MlSizeClampMax { get; set; } = 1.5m;

    /// <summary>Minimum fraction of agents that must vote (non-abstain) for signal to count.</summary>
    public float MinParticipationRate { get; set; } = 0.5f;

    /// <summary>Max position size for volatile regime (overrides default).</summary>
    public decimal VolatileRegimeMaxPositionPct { get; set; } = 2.5m;

    /// <summary>Max position size for trending regime (overrides default).</summary>
    public decimal TrendingRegimeMaxPositionPct { get; set; } = 7.5m;

    /// <summary>Per-step agent timeout in milliseconds. One hung agent won't block the entire pipeline.</summary>
    public int AgentTimeoutMs { get; set; } = 5000;
}

// ── Strategy Agents ─────────────────────────────────────────────────────────

public sealed class MomentumOptions
{
    public int RsiPeriod { get; set; } = 14;
    public double RsiBullThreshold { get; set; } = 60.0;
    public double RsiBearThreshold { get; set; } = 40.0;
    public int AdxPeriod { get; set; } = 14;
    public double AdxTrendThreshold { get; set; } = 20.0;
    public double AdxStrongThreshold { get; set; } = 25.0;
    public int EmaFastPeriod { get; set; } = 9;
    public int EmaSlowPeriod { get; set; } = 21;
    public int MinCandles { get; set; } = 30;
    public int SwarmWeight { get; set; } = 20;

    /// <summary>Aggregation factor for multi-timeframe RSI (synthetic higher TF).</summary>
    public int MultiTfAggFactor { get; set; } = 4;

    /// <summary>RSI midpoint for multi-TF confirmation.</summary>
    public double MultiTfRsiMidpoint { get; set; } = 50.0;

    /// <summary>Confidence per confirmed factor.</summary>
    public int BaseConfidencePerFactor { get; set; } = 6;
    public int VolumeExpansionBonus { get; set; } = 4;
    public int StrongTrendBonus { get; set; } = 2;
    public int VwapAgreementBonus { get; set; } = 2;
    public int VwapDisagreementPenalty { get; set; } = 4;

    /// <summary>Volume expansion: last 3 avg must exceed prev 5 avg by this multiplier.</summary>
    public double VolumeExpansionMultiplier { get; set; } = 1.1;
}

public sealed class BreakoutOptions
{
    public int MinConsolidationBars { get; set; } = 10;
    public int LookbackBars { get; set; } = 50;
    public double RangeThresholdPercent { get; set; } = 0.03;
    public double VolumeSurgeMultiple { get; set; } = 1.5;
    public int VolumeAvgBars { get; set; } = 20;
    public int ShortBase { get; set; } = 10;
    public int MediumBase { get; set; } = 20;
    public int LongBase { get; set; } = 35;
    public double RetestProximityPercent { get; set; } = 0.005;
    public int RetestLookbackBars { get; set; } = 5;
    public int VolumeProfileBins { get; set; } = 10;
    public double ThinAirThreshold { get; set; } = 0.5;
    public int SwarmWeight { get; set; } = 25;
    public double FibRetracementLevel { get; set; } = 0.786;
    public int MaxSwingHighStaleBars { get; set; } = 40;

    public int BaseConfidencePerFactor { get; set; } = 6;
    public int LongBaseDurationBonus { get; set; } = 4;
    public int MediumBaseDurationBonus { get; set; } = 2;
    public int ThinAirBonus { get; set; } = 2;
    public double TightConsolidationQuality { get; set; } = 0.6;
    public int TightConsolidationBonus { get; set; } = 2;
}

public sealed class ScalpingOptions
{
    public double StrongOfiThreshold { get; set; } = 0.6;
    public double MinOfiThreshold { get; set; } = 0.5;
    public double MaxSpreadBps { get; set; } = 10.0;
    public double MinFillProbability { get; set; } = 0.7;
    public double TickRsiBullThreshold { get; set; } = 0.55;
    public double TickRsiBearThreshold { get; set; } = 0.45;
    public double EmaDeviationThreshold { get; set; } = 0.001;
    public double MaxVwapDeviation { get; set; } = 1.5;
    public int SwarmWeight { get; set; } = 15;

    public int BaseConfidencePerFactor { get; set; } = 5;
    public double VeryStrongOfiThreshold { get; set; } = 0.8;
    public int VeryStrongOfiBonus { get; set; } = 3;
    public double TightSpreadBps { get; set; } = 5.0;
    public int TightSpreadBonus { get; set; } = 2;
}

public sealed class StatArbOptions
{
    public int LookbackBars { get; set; } = 50;
    public double EntryZScore { get; set; } = 2.5;
    public double MinCoefficientOfVariation { get; set; } = 0.005;
    public double FastHalfLife { get; set; } = 5.0;
    public double MaxHalfLife { get; set; } = 15.0;
    public double AdfCriticalValue { get; set; } = -2.86;
    public int SwarmWeight { get; set; } = 30;

    public int BaseConfidencePerFactor { get; set; } = 5;
    public double ZScoreMagnitudeCap { get; set; } = 2.0;
    public double ZScoreBonusMultiplier { get; set; } = 2.5;
    public int FastHalfLifeBonus { get; set; } = 3;
    public double AdfBonusOffset { get; set; } = 1.0;
    public int StrongStationarityBonus { get; set; } = 2;
    public double VwapAgreementThreshold { get; set; } = 0.5;
    public int VwapAgreementBonus { get; set; } = 2;
}

public sealed class VolatilityExpansionOptions
{
    public int BbPeriod { get; set; } = 20;
    public double BbStdDevMultiplier { get; set; } = 2.0;
    public int AtrFastPeriod { get; set; } = 14;
    public int AtrSlowPeriod { get; set; } = 50;
    public double AtrExpansionThreshold { get; set; } = 2.0;
    public double AtrStrongExpansion { get; set; } = 2.0;
    public int KcPeriod { get; set; } = 20;
    public double KcAtrMultiplier { get; set; } = 1.5;
    public int DirectionBars { get; set; } = 3;
    public double DirectionThreshold { get; set; } = 0.8;
    public int VolAvgBars { get; set; } = 20;
    public double VolExpansionMultiple { get; set; } = 1.3;
    public int MinCandles { get; set; } = 55;
    public int SwarmWeight { get; set; } = 20;

    /// <summary>BB width expansion threshold.</summary>
    public double BbWidthExpansionThreshold { get; set; } = 1.2;
    /// <summary>BB width squeeze threshold.</summary>
    public double BbWidthSqueezeThreshold { get; set; } = 0.8;

    public int BaseConfidencePerFactor { get; set; } = 5;
    public int StrongAtrExpansionBonus { get; set; } = 3;
    public double ClearDirectionThreshold { get; set; } = 0.8;
    public int ClearDirectionBonus { get; set; } = 2;
    public int ExtremePctBBonus { get; set; } = 2;
}

// ── ML Pipeline ─────────────────────────────────────────────────────────────

public sealed class MlOptions
{
    /// <summary>Number of features in the feature tensor.</summary>
    public int FeatureTensorSize { get; set; } = 5;

    /// <summary>RSI period for feature engineering.</summary>
    public int RsiPeriod { get; set; } = 14;

    /// <summary>Fast EMA period for feature engineering.</summary>
    public int EmaFastPeriod { get; set; } = 9;

    /// <summary>Price buffer ring size for feature engineering.</summary>
    public int PriceBufferSize { get; set; } = 100;

    /// <summary>Minimum ticks before volatility is considered valid.</summary>
    public int VolatilityWarmup { get; set; } = 20;

    /// <summary>Maximum volatility clamp (10% return stddev).</summary>
    public double MaxVolatility { get; set; } = 0.10;

    /// <summary>ML random seed for reproducibility.</summary>
    public int RandomSeed { get; set; } = 42;
}
