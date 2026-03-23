using Microsoft.Extensions.Logging;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Dedicated risk-calculation agent. Has absolute authority to override the Technical Analyst Agent's required position size.
/// Heavily utilizes the mathematically proven fractional Kelly Criterion to prevent ruin.
/// </summary>
public class RiskAgent
{
    private readonly ILogger<RiskAgent> _logger;
    private volatile bool _killSwitchArmed;

    public bool IsKillSwitchArmed => _killSwitchArmed;

    public RiskAgent(ILogger<RiskAgent> logger)
    {
        _logger = logger;
    }

    public void ResetKillSwitch() => _killSwitchArmed = false;

    public Task<(bool Approved, string Reason, decimal SafePositionSizePercent)> EvaluateAsync(
        TradeDecision candidate, 
        RiskParameters riskProfile, 
        decimal symbol24hVolume,
        CancellationToken cancellationToken = default)
    {
        // 1. Kelly Criterion — f = (b*p − q) / b  (half-Kelly for safety)
        //    b = expected profit multiple on a win  (1.5x assumed)
        //    p = win probability from confidence score
        decimal p = (decimal)(candidate.Confidence / 100.0);
        decimal q = 1m - p;
        decimal b = 1.5m;
        decimal rawKelly = Math.Max(0m, (b * p - q) / b);

        // 2. Size Constraint Resolution (half-Kelly capped by config max)
        decimal maxAllowedByConfig = riskProfile.MaxPositionSizePercent;
        decimal safeAllocation = Math.Min(rawKelly * 0.5m, maxAllowedByConfig);

        if (candidate.Confidence < 65)
        {
            safeAllocation *= 0.60m;
        }
        else if (candidate.Confidence < 75)
        {
            safeAllocation *= 0.80m;
        }

        if (string.Equals(candidate.RegimeTag, "Volatile", StringComparison.OrdinalIgnoreCase))
        {
            safeAllocation *= 0.60m;
        }
        else if (string.Equals(candidate.RegimeTag, "Ranging", StringComparison.OrdinalIgnoreCase))
        {
            safeAllocation *= 0.80m;
        }

        if (safeAllocation <= 0.001m)
        {
            return Task.FromResult((false, "Negative Edge / Kelly Fraction too small.", 0m));
        }

        // 3. ADV (Average Daily Volume) Protection: warn if allocation exceeds 5% of 24h liquidity
        if (symbol24hVolume > 0)
        {
            decimal maxAllowedByAdv = symbol24hVolume * 0.05m;
            if (safeAllocation > maxAllowedByAdv)
            {
                _logger.LogWarning(
                    "ADV guard: requested size {Size:P2} exceeds 5% of 24h volume ({Adv:N0}). Capping.",
                    safeAllocation, symbol24hVolume);
                safeAllocation = maxAllowedByAdv;
            }
        }
        else
        {
            safeAllocation *= 0.70m;
        }

        if (riskProfile.StopLossPercent > 5.0m)
        {
            return Task.FromResult((false, $"Proposed Stop Loss parameter ({riskProfile.StopLossPercent:P2}) penetrates HFT Maximum Allowable Risk Profile (5.00%).", 0m));
        }

        return Task.FromResult((true, "Risk Constraints Satisfied.", safeAllocation));
    }

    public Task<(bool Approved, string Reason)> EvaluatePortfolioGuardrailsAsync(
        IReadOnlyList<Trade> recentTrades,
        Signal signal,
        decimal requestedPositionFraction,
        RiskParameters riskProfile,
        decimal assumedStartingEquity = 0m)
    {
        if (_killSwitchArmed)
        {
            return Task.FromResult((false, "Kill-switch is armed due to prior drawdown breach."));
        }

        // Use configured equity or fall back to the sum of all closed-trade notional (rough proxy)
        if (assumedStartingEquity <= 0)
        {
            assumedStartingEquity = riskProfile.StartingEquity > 0 ? riskProfile.StartingEquity : 100_000m;
        }

        var dayStart = DateTime.UtcNow.Date;
        var dailyClosedPnL = recentTrades
            .Where(t => t.ExitTime.HasValue && t.ExitTime.Value >= dayStart)
            .Sum(t => (t.ExitPrice - t.EntryPrice) * t.Quantity * (t.Side == TradeSide.Sell ? -1 : 1));

        var lossPercent = assumedStartingEquity <= 0
            ? 0
            : Math.Abs(Math.Min(0, dailyClosedPnL) / assumedStartingEquity) * 100m;

        if (lossPercent >= riskProfile.MaxDailyLossPercent)
        {
            _killSwitchArmed = true;
            return Task.FromResult((false,
                $"Daily loss limit breached ({lossPercent:F2}% >= {riskProfile.MaxDailyLossPercent:F2}%). Kill-switch armed."));
        }

        var recentClosed = recentTrades
            .Where(t => t.ExitTime.HasValue)
            .OrderByDescending(t => t.ExitTime)
            .Take(4)
            .ToList();
        var consecutiveLosses = 0;
        foreach (var trade in recentClosed)
        {
            var pnl = (trade.ExitPrice - trade.EntryPrice) * trade.Quantity * (trade.Side == TradeSide.Sell ? -1 : 1);
            if (pnl < 0)
            {
                consecutiveLosses++;
                continue;
            }

            break;
        }

        if (consecutiveLosses >= 3)
        {
            return Task.FromResult((false, $"Loss-streak guard active ({consecutiveLosses} consecutive losses). Cool down before new entries."));
        }

        var openTrades = recentTrades.Count(t => !t.IsClosed && t.Status == TradeStatus.Executed);
        if (openTrades >= 5)
        {
            return Task.FromResult((false, "Max concurrent open trades reached (5)."));
        }

        var symbolOpenTrades = recentTrades.Count(t =>
            !t.IsClosed &&
            t.Status == TradeStatus.Executed &&
            string.Equals(t.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase));
        if (symbolOpenTrades >= 2)
        {
            return Task.FromResult((false, $"Max open trades for {signal.Symbol} reached (2)."));
        }

        var market = signal.Symbol.Contains("-") ? "CRYPTO" :
            (signal.Symbol.Contains("=") ? "FOREX" : "BIST");
        var marketOpenTrades = recentTrades.Count(t =>
            !t.IsClosed &&
            t.Status == TradeStatus.Executed &&
            (market == "CRYPTO" ? t.Symbol.Contains("-") :
                market == "FOREX" ? t.Symbol.Contains("=") :
                (!t.Symbol.Contains("-") && !t.Symbol.Contains("="))));
        if (marketOpenTrades >= 3)
        {
            return Task.FromResult((false, $"Max open trades in {market} reached (3)."));
        }

        var maxAllowedFraction = riskProfile.MaxPositionSizePercent / 100m;
        if (requestedPositionFraction > maxAllowedFraction)
        {
            return Task.FromResult((false,
                $"Requested position size {requestedPositionFraction:P2} exceeds configured max {maxAllowedFraction:P2}."));
        }

        return Task.FromResult((true, "Portfolio risk checks passed."));
    }
}
