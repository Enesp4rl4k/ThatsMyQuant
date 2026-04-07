using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Institutional algorithms don't buy directly at the Mid-Price. If they purchase $500,000 of Bitcoin,
/// they clear out ('slip' through) several layers of the Level-2 order book.
/// This component calculates absolute slippage penalties in Basis Points (BPS).
/// </summary>
public static class SlippageSimulator
{
    private const decimal BasisPoint = 0.0001m; // 1 BPS = 0.01%

    // Default liquidity assumptions (used when no options are provided)
    private const decimal DefaultTier1LiquidityFactor = 0.05m; // 5 BPS per $100k Order
    private const decimal DefaultAltcoinLiquidityFactor = 2.5m; // 250 BPS (2.5%) per $100k Order
    private const int DefaultCatastrophicSlippageCapBps = 5000;

    /// <summary>
    /// Deducts mathematical margins to ensure the ML bot doesn't experience "Paper Trading Illusions"
    /// Calculates how many ticks the price will run against the algorithm when executing the proposed volume.
    /// </summary>
    public static decimal CalculateNetExecutionPrice(
        decimal midPrice,
        decimal notionalVolumeUsd,
        bool isBuy,
        bool isTier1Asset,
        ExecutionOptions? options = null)
    {
        var tier1Factor = options?.Tier1LiquidityFactor ?? DefaultTier1LiquidityFactor;
        var altcoinFactor = options?.AltcoinLiquidityFactor ?? DefaultAltcoinLiquidityFactor;
        var catastrophicCap = options?.CatastrophicSlippageCapBps ?? DefaultCatastrophicSlippageCapBps;

        // For every $10k of capital, how many basis points do we slip mathematically against the spread?
        decimal depthMultiplier = notionalVolumeUsd / 10000m;

        decimal slippageBps = depthMultiplier * (isTier1Asset ? tier1Factor : altcoinFactor);

        // Prevent catastrophic infinite logic bugs where slippage > 50%
        if (slippageBps > catastrophicCap) slippageBps = catastrophicCap;

        decimal absoluteSlippage = midPrice * (slippageBps * BasisPoint);

        // If Buying: Price goes UP (worse). If Selling: Price goes DOWN (worse).
        return isBuy ? midPrice + absoluteSlippage : midPrice - absoluteSlippage;
    }
}
