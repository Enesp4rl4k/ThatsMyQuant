using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Contracts.Models;

/// <summary>
/// Result of a backtest run — the single most important output for strategy validation.
/// </summary>
public record BacktestResult
{
    public string Symbol { get; init; } = "";
    public int TotalTrades { get; init; }
    public int WinningTrades { get; init; }
    public int LosingTrades { get; init; }
    public double WinRate => TotalTrades > 0 ? WinningTrades * 100.0 / TotalTrades : 0;

    public decimal TotalPnL { get; init; }
    public decimal MaxDrawdown { get; init; }
    public decimal MaxDrawdownPercent { get; init; }

    /// <summary>Sharpe Ratio — risk-adjusted return. Above 1.0 = good, above 2.0 = excellent.</summary>
    public double SharpeRatio { get; init; }

    /// <summary>Sortino Ratio — like Sharpe but only penalizes downside volatility.</summary>
    public double SortinoRatio { get; init; }

    /// <summary>Average risk/reward ratio of all trades.</summary>
    public double AverageRR { get; init; }

    /// <summary>Profit factor = gross profit / gross loss. Above 1.5 = good.</summary>
    public double ProfitFactor { get; init; }

    /// <summary>Maximum consecutive losing trades.</summary>
    public int MaxConsecutiveLosses { get; init; }

    /// <summary>Starting capital used in the backtest.</summary>
    public decimal StartingCapital { get; init; }

    /// <summary>Final equity after all trades.</summary>
    public decimal FinalEquity { get; init; }

    /// <summary>Equity curve — equity value after each trade.</summary>
    public List<decimal> EquityCurve { get; init; } = new();

    /// <summary>List of individual trade results.</summary>
    public List<BacktestTradeResult> Trades { get; init; } = new();

    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
    public string Reasoning { get; init; } = "";
    public DecisionCoverageFlags CoverageFlags { get; init; } = DecisionCoverageFlags.None;
    public string CoverageSummary { get; init; } = "";
    public Dictionary<string, int> EntryRejectReasons { get; init; } = new();
    public bool InferenceModelAvailable { get; init; }
    public bool InferencePredictionValid { get; init; }

    // ── Pillar 4: Portfolio-level validation breakdowns ──

    /// <summary>Performance breakdown by market regime (Trending, Volatile, Ranging).</summary>
    public Dictionary<string, BucketPerformance> RegimeBuckets { get; init; } = new();

    /// <summary>Performance breakdown by exit policy tag (TrendRunner, MeanReversion, etc).</summary>
    public Dictionary<string, BucketPerformance> ExitPolicyBuckets { get; init; } = new();

    /// <summary>Performance breakdown by calendar quarter (e.g. "2025-Q3").</summary>
    public Dictionary<string, BucketPerformance> QuarterlyBuckets { get; init; } = new();

    /// <summary>Top 5 entry rejection reasons ordered by count descending.</summary>
    public List<string> TopRejectReasons { get; init; } = new();

    /// <summary>Total round-trip fees paid across all trades.</summary>
    public decimal TotalFeesPaid { get; init; }

    /// <summary>Net funding cash-flow across all trades.</summary>
    public decimal TotalNetFunding { get; init; }

    /// <summary>Gross PnL before fees and funding — shows raw alpha before execution drag.</summary>
    public decimal GrossPnL { get; init; }

    /// <summary>Cost leakage: percentage of gross PnL consumed by fees + slippage.</summary>
    public double CostLeakagePercent { get; init; }
}

/// <summary>
/// Performance metrics for a bucketed subset of trades (regime, exit policy, quarter).
/// </summary>
public record BucketPerformance
{
    public int TradeCount { get; init; }
    public int Winners { get; init; }
    public double WinRate => TradeCount > 0 ? Winners * 100.0 / TradeCount : 0;
    public decimal TotalPnL { get; init; }
    public double AverageRR { get; init; }
    public double ProfitFactor { get; init; }
}

public record BacktestTradeResult
{
    public DateTime EntryTime { get; init; }
    public DateTime ExitTime { get; init; }
    public TradeSide Side { get; init; }
    public string StrategyTag { get; init; } = "TrendConfluence";
    public string RegimeTag { get; init; } = "Unknown";
    public string ExitPolicyTag { get; init; } = "TrendRunner";
    public string ExecutionMode { get; init; } = "SIMULATED";
    public string PromotionStage { get; init; } = "Backtest";
    public decimal EntryPrice { get; init; }
    public decimal ExitPrice { get; init; }
    public decimal PnL { get; init; }
    public decimal PnLPercent { get; init; }
    public int Confidence { get; init; }
    public string ExitReason { get; init; } = ""; // SL, TP, TrailingSL, Signal
    public string Reasoning { get; init; } = "";

    /// <summary>Round-trip fees charged for this trade in the simulation.</summary>
    public decimal TotalFeesPaid { get; init; }

    /// <summary>Net funding (perp); spot-style engines leave this at zero.</summary>
    public decimal NetFunding { get; init; }
}
