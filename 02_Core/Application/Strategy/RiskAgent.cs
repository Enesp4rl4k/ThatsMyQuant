using Microsoft.Extensions.Logging;
using System.Threading;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Dedicated risk-calculation agent. Has absolute authority to override the Technical Analyst Agent's required position size.
/// Heavily utilizes the mathematically proven fractional Kelly Criterion to prevent ruin.
/// Now enhanced with HFT rate-limiting, circuit breakers, and real-time portfolio tracking.
/// </summary>
public class RiskAgent : IFocusedAgent
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

    public Task ResetAsync(string symbol, CancellationToken ct = default)
    {
        _killSwitchArmed = false;
        _logger.LogInformation("RiskAgent reset for {Symbol}. Kill-switch disarmed.", symbol);
        return Task.CompletedTask;
    }

    public Task ProcessStepAsync(Candle candle, CancellationToken ct = default)
    {
        // Risk agent might monitor drawdown or volatility spikes per step
        return Task.CompletedTask;
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

    private decimal _dailyClosedPnL;
    private int _activeTradeCount;
    private DateTime _lastPnLResetDate = DateTime.UtcNow.Date;

    public void NotifyTradeUpdate(Trade trade)
    {
        // 🔥 HFT Optimization: Track PnL incrementally to avoid LINQ Sum() on every tick
        if (trade.ExitTime.HasValue && trade.ExitTime.Value.Date == DateTime.UtcNow.Date && trade.Status == TradeStatus.Executed)
        {
            var pnl = (trade.ExitPrice - trade.EntryPrice) * trade.Quantity * (trade.Side == TradeSide.Sell ? -1 : 1);
            InterlockedAdd(ref _dailyClosedPnL, pnl);
        }

        // Track active trades without LINQ Count()
        if (trade.Status == TradeStatus.Executed && !trade.IsClosed)
        {
            Interlocked.Increment(ref _activeTradeCount);
        }
        else if (trade.IsClosed)
        {
            Interlocked.Decrement(ref _activeTradeCount);
        }
    }

    private (bool Approved, string Reason) CheckDailyLossLimit(IReadOnlyList<Trade> recentTrades, RiskParameters riskProfile, decimal assumedStartingEquity)
    {
        // Reset daily PnL if date changed
        if (DateTime.UtcNow.Date > _lastPnLResetDate)
        {
            _dailyClosedPnL = 0;
            _lastPnLResetDate = DateTime.UtcNow.Date;
        }

        var equity = GetBaseEquityForRiskCheck(assumedStartingEquity, riskProfile);
        var lossPercent = equity <= 0 ? 0 : Math.Abs(Math.Min(0, _dailyClosedPnL) / equity) * 100m;

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
        if (_activeTradeCount >= 5)
            return (false, "Max concurrent open trades reached (5).");

        // Per-symbol check still needs a small loop or a dictionary, but we can optimize it if needed.
        // For now, eliminating the global count check is already a win.
        var symbolOpenTrades = recentTrades.Count(t =>
            !t.IsClosed && t.Status == TradeStatus.Executed &&
            string.Equals(t.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase));
        
        return symbolOpenTrades >= 2 ? (false, $"Max open trades for {signal.Symbol} reached (2).") : (true, "");
    }

    private static void InterlockedAdd(ref decimal location, decimal value)
    {
        decimal initial, computed;
        do
        {
            initial = location;
            computed = initial + value;
        } while (initial != Interlocked.CompareExchange(ref location, computed, initial));
    }

    private (bool Approved, string Reason) CheckLossStreak(IReadOnlyList<Trade> recentTrades, Signal signal)
    {
        // Streak check is only performed on entry evaluation, so a small LINQ is acceptable here,
        // but we take only 3 items to keep it O(1) effectively.
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
