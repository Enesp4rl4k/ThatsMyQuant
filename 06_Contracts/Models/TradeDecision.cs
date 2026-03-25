using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Contracts.Models;

/// <summary>
/// The final output of the strategy engine — a typed, reasoned trade decision.
/// </summary>
public record TradeDecision
{
    /// <summary>Whether the strategy recommends taking this trade.</summary>
    public bool ShouldTrade { get; init; }

    /// <summary>Buy or Sell.</summary>
    public TradeSide Side { get; init; }

    /// <summary>Confidence score 0-100. Minimum 60 to execute. Higher = stronger confluence.</summary>
    public int Confidence { get; init; }

    /// <summary>Human-readable breakdown of why this decision was made.</summary>
    public string Reasoning { get; init; } = string.Empty;

    /// <summary>Computed stop-loss price based on ATR.</summary>
    public decimal StopLoss { get; init; }

    /// <summary>Computed take-profit price based on ATR and risk/reward ratio.</summary>
    public decimal TakeProfit { get; init; }

    /// <summary>Position size as a fraction of portfolio (0.0 - 1.0) from Kelly Criterion.</summary>
    public decimal PositionSizePercent { get; init; }

    /// <summary>Strategy family that produced the decision.</summary>
    public string StrategyTag { get; init; } = "TrendConfluence";

    /// <summary>Detected market regime for the decision.</summary>
    public string RegimeTag { get; init; } = "Unknown";

    /// <summary>Optional partial take-profit level for scaling out.</summary>
    public decimal PartialTakeProfit { get; init; }

    /// <summary>Position fraction to exit at the partial target.</summary>
    public decimal PartialExitFraction { get; init; }

    /// <summary>Named exit policy used by simulation/execution layers.</summary>
    public string ExitPolicyTag { get; init; } = "Balanced";

    /// <summary>When this fraction of the target path is reached, arm breakeven protection.</summary>
    public decimal BreakevenTriggerFraction { get; init; } = 0.50m;

    /// <summary>Optional max holding bars before a time-stop exit is preferred.</summary>
    public int MaxHoldingBars { get; init; }

    /// <summary>The indicator snapshot used to make this decision.</summary>
    public IndicatorSnapshot? Indicators { get; init; }

    /// <summary>Create a rejection decision.</summary>
    public static TradeDecision Reject(string reason, IndicatorSnapshot? indicators = null)
        => new()
        {
            ShouldTrade = false,
            Confidence = 0,
            Reasoning = reason,
            Indicators = indicators
        };
}
