using Microsoft.Extensions.Logging;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Dedicated risk-calculation agent. Has absolute authority to override the Technical Analyst Agent's required position size.
/// Heavily utilizes the mathematically proven fractional Kelly Criterion to prevent ruin.
/// Now enhanced with HFT rate-limiting, circuit breakers, and real-time portfolio tracking.
/// </summary>
public class RiskAgent
{
    private readonly ILogger<RiskAgent> _logger;
    private readonly MemoryPortfolioManager? _portfolioManager;
    private readonly TokenBucketRateLimiter _rateLimiter;
    private volatile bool _killSwitchArmed;

    public bool IsKillSwitchArmed => _killSwitchArmed;

    public RiskAgent(ILogger<RiskAgent> logger, MemoryPortfolioManager? portfolioManager = null)
    {
        _logger = logger;
        _portfolioManager = portfolioManager;
        _rateLimiter = new TokenBucketRateLimiter(10, 10.0);
    }

    public void ResetKillSwitch() => _killSwitchArmed = false;

    public void ArmKillSwitch(string reason)
    {
        _killSwitchArmed = true;
        _logger.LogCritical("RISK KILL-SWITCH ARMED: {Reason}", reason);
    }

    public Task<(bool Approved, string Reason, decimal SafePositionSizePercent)> EvaluateAsync(
        TradeDecision candidate, 
        RiskParameters riskProfile, 
        decimal symbol24hVolume)
    {
        if (_killSwitchArmed) return Task.FromResult((false, "Kill-switch is armed.", 0m));
        if (!_rateLimiter.TryConsume()) return Task.FromResult((false, "HFT Rate limit exceeded.", 0m));

        decimal p = (decimal)(candidate.Confidence / 100.0);
        decimal q = 1m - p;
        decimal b = 1.5m;
        decimal rawKelly = Math.Max(0m, (b * p - q) / b);

        decimal safeAllocation = Math.Min(rawKelly * 0.5m, riskProfile.MaxPositionSizePercent / 100m);
        if (safeAllocation <= 0.0001m) return Task.FromResult((false, "Negative Edge.", 0m));

        if (symbol24hVolume > 0)
        {
            decimal maxAllowedByAdv = symbol24hVolume * 0.05m;
            if (safeAllocation > maxAllowedByAdv) safeAllocation = maxAllowedByAdv;
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
        if (_killSwitchArmed) return Task.FromResult((false, "Kill-switch is armed."));

        var balanceCheck = CheckAvailableBalance(signal);
        if (!balanceCheck.Approved) return Task.FromResult(balanceCheck);

        var lossCheck = CheckDailyLossLimit(recentTrades, riskProfile, assumedStartingEquity);
        if (!lossCheck.Approved) return Task.FromResult(lossCheck);

        var countCheck = CheckConcurrentTradeLimits(recentTrades, signal);
        if (!countCheck.Approved) return Task.FromResult(countCheck);

        var streakCheck = CheckLossStreak(recentTrades, signal);
        if (!streakCheck.Approved) return Task.FromResult(streakCheck);

        return Task.FromResult((true, "Portfolio risk checks passed."));
    }

    private (bool Approved, string Reason) CheckAvailableBalance(Signal signal)
    {
        if (_portfolioManager == null || string.IsNullOrWhiteSpace(signal.Symbol)) return (true, "");
        
        var quoteAsset = signal.Symbol.EndsWith("USDT") ? "USDT" : "TRY"; 
        var available = _portfolioManager.GetAvailableBalance(quoteAsset);
        return available <= 0 ? (false, "Insufficient available balance.") : (true, "");
    }

    private (bool Approved, string Reason) CheckDailyLossLimit(IReadOnlyList<Trade> recentTrades, RiskParameters riskProfile, decimal assumedStartingEquity)
    {
        var dayStart = DateTime.UtcNow.Date;
        var dailyClosedPnL = recentTrades
            .Where(t => t.ExitTime.HasValue && t.ExitTime.Value >= dayStart && t.Status == TradeStatus.Executed && t.ExitPrice > 0)
            .Sum(t => (t.ExitPrice - t.EntryPrice) * t.Quantity * (t.Side == TradeSide.Sell ? -1 : 1));

        var equity = GetBaseEquityForRiskCheck(assumedStartingEquity, riskProfile);
        var lossPercent = equity <= 0 ? 0 : Math.Abs(Math.Min(0, dailyClosedPnL) / equity) * 100m;

        if (lossPercent >= riskProfile.MaxDailyLossPercent)
        {
            ArmKillSwitch($"Daily loss limit breached ({lossPercent:F2}%).");
            return (false, "Daily loss limit breached.");
        }
        return (true, "");
    }

    private decimal GetBaseEquityForRiskCheck(decimal assumedStartingEquity, RiskParameters riskProfile)
    {
        if (assumedStartingEquity > 0) return assumedStartingEquity;
        if (riskProfile.StartingEquity > 0) return riskProfile.StartingEquity;
        return 100_000m;
    }

    private (bool Approved, string Reason) CheckConcurrentTradeLimits(IReadOnlyList<Trade> recentTrades, Signal signal)
    {
        if (recentTrades.Count(t => !t.IsClosed && t.Status == TradeStatus.Executed) >= 5)
            return (false, "Max concurrent open trades reached (5).");

        var symbolOpenTrades = recentTrades.Count(t =>
            !t.IsClosed && t.Status == TradeStatus.Executed &&
            string.Equals(t.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase));
        
        return symbolOpenTrades >= 2 ? (false, $"Max open trades for {signal.Symbol} reached (2).") : (true, "");
    }

    private (bool Approved, string Reason) CheckLossStreak(IReadOnlyList<Trade> recentTrades, Signal signal)
    {
        var symbolRecentTrades = recentTrades
            .Where(t => string.Equals(t.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase) && t.IsClosed && t.Status == TradeStatus.Executed)
            .OrderByDescending(t => t.ExitTime)
            .Take(3)
            .ToList();

        if (symbolRecentTrades.Count >= 3 && symbolRecentTrades.All(t => (t.ExitPrice - t.EntryPrice) * (t.Side == TradeSide.Sell ? -1 : 1) < 0))
        {
            return (false, $"Loss-streak guard: 3 consecutive losses on {signal.Symbol}.");
        }
        return (true, "");
    }
}

internal sealed class TokenBucketRateLimiter
{
    private readonly int _capacity;
    private readonly double _refillRate;
    private double _tokens;
    private DateTime _lastRefill;
    private readonly object _lock = new();

    public TokenBucketRateLimiter(int capacity, double refillRate)
    {
        _capacity = capacity;
        _refillRate = refillRate;
        _tokens = capacity;
        _lastRefill = DateTime.UtcNow;
    }

    public bool TryConsume()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastRefill).TotalSeconds;
            _tokens = Math.Min(_capacity, _tokens + elapsed * _refillRate);
            _lastRefill = now;

            if (_tokens >= 1.0)
            {
                _tokens -= 1.0;
                return true;
            }
            return false;
        }
    }
}
