using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// A strict algorithmic boundary ensuring that HFT scaling doesn't bleed capital to exchange fees.
/// Validates if the spread between Entry and TakeProfit naturally overcomes the two-way maker/taker commissions.
/// </summary>
public static class CommissionGuard
{
    // Typical Tier-1 Exchange Fee (e.g. Binance Retail Taker = 0.1%, VIP Taker = 0.04%. Using 0.04% as baseline.)
    public const decimal DefaultTakerFeeRate = 0.0004m; 

    /// <summary>
    /// Validates if the projected profit of an expected trade strictly exceeds the two-way commission costs.
    /// Negative-EV trades where the broker takes the entire profit margin will return false.
    /// </summary>
    public static bool IsTradeProfitableAfterFees(
        decimal entryPrice,
        decimal expectedExitPrice,
        decimal quantity,
        decimal feeRate = DefaultTakerFeeRate,
        TradingCoreOptions? options = null)
    {
        if (quantity <= 0 || entryPrice <= 0 || expectedExitPrice <= 0) return false;

        decimal effectiveFeeRate = options?.DefaultTakerFeeRate ?? feeRate;
        decimal expectedGrossProfit = Math.Abs(expectedExitPrice - entryPrice) * quantity;

        // Slippage/Fee is levied on both notional entry volume and notional exit volume
        decimal entryFee = (entryPrice * quantity) * effectiveFeeRate;
        decimal exitFee = (expectedExitPrice * quantity) * effectiveFeeRate;
        decimal totalEstimatedFee = entryFee + exitFee;

        // Strict constraint: The Gross Profit must be strictly greater than the costs.
        return expectedGrossProfit > totalEstimatedFee;
    }

    /// <summary>
    /// Calculates the absolute Net Profit/Loss (PnL) after two-way commissions have been deducted.
    /// </summary>
    public static decimal CalculateNetPnL(
        decimal entryPrice,
        decimal exitPrice,
        decimal quantity,
        string side = "Buy",
        decimal feeRate = DefaultTakerFeeRate,
        TradingCoreOptions? options = null)
    {
        decimal effectiveFeeRate = options?.DefaultTakerFeeRate ?? feeRate;
        decimal grossPnL = side == "Buy"
            ? (exitPrice - entryPrice) * quantity
            : (entryPrice - exitPrice) * quantity;

        decimal entryFee = (entryPrice * quantity) * effectiveFeeRate;
        decimal exitFee = (exitPrice * quantity) * effectiveFeeRate;
        
        return grossPnL - (entryFee + exitFee);
    }
}
