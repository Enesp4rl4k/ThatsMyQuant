using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

public sealed class PortfolioRebalancingScheduler
{
    public PortfolioRebalancePlan BuildPlan(
        string market,
        decimal cash,
        IReadOnlyCollection<Position> positions,
        IReadOnlyDictionary<string, decimal> latestPrices,
        IReadOnlyDictionary<string, decimal> targetWeights,
        decimal driftTolerancePercent = 5m)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(latestPrices);
        ArgumentNullException.ThrowIfNull(targetWeights);

        var session = MarketHoursScheduler.GetCurrentSession(market);
        var holdings = positions
            .Select(position =>
            {
                latestPrices.TryGetValue(position.Symbol, out var price);
                var effectivePrice = price > 0m ? price : position.AverageEntryPrice;
                var marketValue = effectivePrice * position.Quantity;
                return new { position.Symbol, position.Quantity, effectivePrice, MarketValue = marketValue };
            })
            .ToList();

        var totalEquity = cash + holdings.Sum(holding => holding.MarketValue);
        if (totalEquity <= 0m)
        {
            return new PortfolioRebalancePlan(false, session.NextSessionStart, [], "Portfolio equity is zero; no rebalance actions were created.");
        }

        var actions = new List<PortfolioRebalanceAction>();
        foreach (var target in targetWeights)
        {
            var currentHolding = holdings.FirstOrDefault(holding => string.Equals(holding.Symbol, target.Key, StringComparison.OrdinalIgnoreCase));
            var currentValue = currentHolding?.MarketValue ?? 0m;
            latestPrices.TryGetValue(target.Key, out var marketPrice);
            var referencePrice = marketPrice > 0m ? marketPrice : currentHolding?.effectivePrice ?? 0m;
            if (referencePrice <= 0m)
            {
                continue;
            }

            var targetValue = totalEquity * target.Value;
            var driftPercent = targetValue == 0m ? 0m : (currentValue - targetValue) / targetValue * 100m;
            if (Math.Abs(driftPercent) < driftTolerancePercent)
            {
                continue;
            }

            var quantityDelta = (targetValue - currentValue) / referencePrice;
            var actionSide = quantityDelta >= 0m ? TradeSide.Buy : TradeSide.Sell;
            actions.Add(new PortfolioRebalanceAction(
                target.Key,
                actionSide,
                Math.Round(Math.Abs(quantityDelta), 6),
                Math.Round(currentValue, 2),
                Math.Round(targetValue, 2),
                Math.Round(driftPercent, 2)));
        }

        var summary = actions.Count == 0
            ? "Portfolio weights are within the drift tolerance."
            : session.CanTrade
                ? $"Generated {actions.Count} rebalance actions for the current session."
                : $"Generated {actions.Count} rebalance actions for the next trading session.";

        return new PortfolioRebalancePlan(session.CanTrade, session.NextSessionStart, actions, summary);
    }
}

public sealed record PortfolioRebalancePlan(
    bool CanExecuteNow,
    DateTime ScheduledForUtc,
    IReadOnlyList<PortfolioRebalanceAction> Actions,
    string Summary);

public sealed record PortfolioRebalanceAction(
    string Symbol,
    TradeSide Side,
    decimal Quantity,
    decimal CurrentValue,
    decimal TargetValue,
    decimal DriftPercent);
