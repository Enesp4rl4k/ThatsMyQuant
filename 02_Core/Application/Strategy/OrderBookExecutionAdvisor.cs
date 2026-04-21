using System.Globalization;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Uses top-of-book L2 to annotate the signal, optionally block wide spreads, and scale size when flow opposes the side.
/// </summary>
public static class OrderBookExecutionAdvisor
{
    public static bool TryApply(
        Signal signal,
        OrderBookSnapshot? book,
        OrderBookExecutionOptions options,
        bool isHftMode,
        ILogger logger,
        out string? blockReason)
    {
        blockReason = null;
        if (isHftMode && !options.EnableForHft)
            return true;

        if (!options.Enabled || !book.HasValue || book.Value.Bids.Length == 0 || book.Value.Asks.Length == 0)
            return true;

        var b = book.Value;

        var bestBid = b.Bids[0].Price;
        var bestAsk = b.Asks[0].Price;
        if (bestBid <= 0m || bestAsk <= 0m || bestAsk <= bestBid)
            return true;

        var mid = (bestBid + bestAsk) / 2m;
        var spreadPct = (bestAsk - bestBid) / mid * 100m;
        var bidQ = b.Bids[0].Quantity;
        var askQ = b.Asks[0].Quantity;
        var denom = bidQ + askQ;
        var imb = denom > 0m ? bidQ / denom : 0.5m;

        signal.Metadata["OrderBookSpreadPct"] = spreadPct.ToString(CultureInfo.InvariantCulture);
        signal.Metadata["OrderBookTopImbalance"] = imb.ToString(CultureInfo.InvariantCulture);
        signal.Metadata["OrderBookBestBid"] = bestBid.ToString(CultureInfo.InvariantCulture);
        signal.Metadata["OrderBookBestAsk"] = bestAsk.ToString(CultureInfo.InvariantCulture);

        if (options.UseForAlphaModulation)
        {
            var sideBias = signal.Side == TradeSide.Buy ? (imb - 0.5m) * 2m : (0.5m - imb) * 2m; // -1..1
            var spreadPenalty = Math.Clamp(spreadPct / Math.Max(0.0001m, options.MaxSpreadPercent), 0m, 2m);
            var alphaSignal = Math.Clamp(sideBias - (spreadPenalty * 0.25m), -1m, 1m);
            var confidenceDelta = Math.Round(alphaSignal * options.AlphaMaxConfidenceDelta, 2);

            var prevConfidence = signal.Confidence;
            signal.Confidence = (int)Math.Clamp(prevConfidence + confidenceDelta, 0m, 100m);
            signal.Metadata["OrderBookAlphaDelta"] = confidenceDelta.ToString(CultureInfo.InvariantCulture);
            signal.Metadata["OrderBookConfidence"] = $"{prevConfidence}->{signal.Confidence}";
        }

        if (options.BlockOnWideSpread && spreadPct > options.MaxSpreadPercent)
        {
            blockReason =
                $"Order book spread {spreadPct.ToString("F4", CultureInfo.InvariantCulture)}% exceeds max {options.MaxSpreadPercent}%.";
            logger.LogWarning(
                "Order book blocked {Symbol} {Side}: {Reason}",
                signal.Symbol,
                signal.Side,
                blockReason);
            return false;
        }

        if (options.ReduceQuantityOnWeakSide && signal.Quantity > 0m)
        {
            if (signal.Side == TradeSide.Buy && imb < options.MinTopImbalanceForBuy)
            {
                var before = signal.Quantity;
                signal.Quantity *= options.QuantityReductionFactor;
                signal.Metadata["OrderBookQtyAdjusted"] = $"{before.ToString(CultureInfo.InvariantCulture)}->{signal.Quantity.ToString(CultureInfo.InvariantCulture)}";
                logger.LogInformation(
                    "Order book reduced buy qty for {Symbol}: imbalance {Imb:F3} below min {Min:F3}",
                    signal.Symbol,
                    imb,
                    options.MinTopImbalanceForBuy);
            }
            else if (signal.Side == TradeSide.Sell && imb > options.MaxTopImbalanceForSell)
            {
                var before = signal.Quantity;
                signal.Quantity *= options.QuantityReductionFactor;
                signal.Metadata["OrderBookQtyAdjusted"] = $"{before.ToString(CultureInfo.InvariantCulture)}->{signal.Quantity.ToString(CultureInfo.InvariantCulture)}";
                logger.LogInformation(
                    "Order book reduced sell qty for {Symbol}: imbalance {Imb:F3} above max {Max:F3}",
                    signal.Symbol,
                    imb,
                    options.MaxTopImbalanceForSell);
            }
        }

        return true;
    }
}
