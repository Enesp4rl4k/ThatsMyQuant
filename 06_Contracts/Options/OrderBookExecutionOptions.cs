namespace ThatsMyQuant.Contracts.Options;

/// <summary>Live L2 order book gates applied before venue execution (section: Execution:OrderBook).</summary>
public sealed class OrderBookExecutionOptions
{
    public bool Enabled { get; set; } = true;
    public bool EnableForHft { get; set; } = false;

    /// <summary>Full bid–ask spread % of mid; above this we may block when BlockOnWideSpread is true.</summary>
    public decimal MaxSpreadPercent { get; set; } = 0.35m;

    /// <summary>Top-of-book imbalance = bidQty / (bidQty + askQty). Below this for buys, quantity may be reduced.</summary>
    public decimal MinTopImbalanceForBuy { get; set; } = 0.38m;

    /// <summary>Above this for sells, quantity may be reduced.</summary>
    public decimal MaxTopImbalanceForSell { get; set; } = 0.62m;

    /// <summary>Multiply quantity when the book is against our side (0–1).</summary>
    public decimal QuantityReductionFactor { get; set; } = 0.82m;

    public bool BlockOnWideSpread { get; set; } = true;

    public bool ReduceQuantityOnWeakSide { get; set; } = true;

    /// <summary>Apply low-weight confidence modulation from L2 (non-HFT by default).</summary>
    public bool UseForAlphaModulation { get; set; } = true;

    /// <summary>Absolute confidence impact cap in points (e.g. 6 => -6..+6).</summary>
    public decimal AlphaMaxConfidenceDelta { get; set; } = 6m;
}
