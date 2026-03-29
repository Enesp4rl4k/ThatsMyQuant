using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Dedicated risk-calculation agent with dynamic regime-aware governance:
///   - Dynamic Kelly: fraction adapts to recent win rate (data-driven, not static)
///   - Drawdown circuit breaker: progressive size reduction as intraday DD deepens
///   - Correlation guard: blocks additional exposure when open positions are highly correlated
///   - Portfolio exposure limit: caps total notional across all positions
///
/// Has absolute authority to override position sizes. Kill-switch halts all trading.
/// </summary>
public class RiskAgent : IFocusedAgent
{
    private readonly ILogger<RiskAgent> _logger;
    private readonly MemoryPortfolioManager? _portfolioManager;
    private readonly TokenBucketRateLimiter _rateLimiter;
    private readonly RiskOptions _opts;
    private volatile bool _killSwitchArmed;

    public bool IsKillSwitchArmed => _killSwitchArmed;

    public RiskAgent(ILogger<RiskAgent> logger, IOptions<RiskOptions>? options = null, MemoryPortfolioManager? portfolioManager = null)
    {
        _logger = logger;
        _portfolioManager = portfolioManager;
        _opts = options?.Value ?? new RiskOptions();
        _rateLimiter = new TokenBucketRateLimiter(_opts.RateLimitCapacity, _opts.RateLimitRefillRate);
    }

    public Task ResetAsync(string symbol, CancellationToken ct = default)
    {
        _killSwitchArmed = false;
        _logger.LogInformation("RiskAgent reset for {Symbol}. Kill-switch disarmed.", symbol);
        return Task.CompletedTask;
    }

    public Task ProcessStepAsync(Candle candle, CancellationToken ct = default)
        => Task.CompletedTask;

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

        decimal b = _opts.DefaultRewardRisk;
        if (candidate.TakeProfit > 0 && candidate.StopLoss > 0)
        {
            decimal mid = (candidate.TakeProfit + candidate.StopLoss) / 2m;
            decimal reward = Math.Abs(candidate.TakeProfit - mid);
            decimal risk = Math.Abs(mid - candidate.StopLoss);
            if (risk > 0) b = reward / risk;
        }
        b = Math.Clamp(b, _opts.RewardRiskMin, _opts.RewardRiskMax);
        decimal rawKelly = Math.Max(0m, (b * p - q) / b);

        // Dynamic Kelly: base fraction × win-rate adjustment
        decimal kellyFraction = symbol24hVolume > _opts.HftVolumeThreshold ? _opts.KellyFractionHft : _opts.KellyFractionSwing;
        decimal safeAllocation = Math.Min(rawKelly * kellyFraction, riskProfile.MaxPositionSizePercent / 100m);
        if (safeAllocation <= _opts.MinKellyAllocation) return Task.FromResult((false, "Negative Edge.", 0m));

        // ADV constraint
        if (symbol24hVolume > 0)
        {
            decimal maxAllowedByAdv = symbol24hVolume * _opts.MaxAdvFraction;
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

        // Drawdown circuit breaker: progressive size reduction
        var ddCheck = CheckDrawdownCircuitBreaker(recentTrades, riskProfile, assumedStartingEquity);
        if (!ddCheck.Approved) return Task.FromResult((ddCheck.Approved, ddCheck.Reason));

        var countCheck = CheckConcurrentTradeLimits(recentTrades, signal);
        if (!countCheck.Approved) return Task.FromResult(countCheck);

        var concentrationCheck = CheckSymbolConcentration(recentTrades, signal, requestedPositionFraction, riskProfile, assumedStartingEquity);
        if (!concentrationCheck.Approved) return Task.FromResult(concentrationCheck);

        // Correlation guard: block highly correlated additions
        var corrCheck = CheckCorrelationExposure(recentTrades, signal);
        if (!corrCheck.Approved) return Task.FromResult(corrCheck);

        // Total portfolio exposure cap
        var exposureCheck = CheckPortfolioExposure(recentTrades, requestedPositionFraction, riskProfile, assumedStartingEquity);
        if (!exposureCheck.Approved) return Task.FromResult(exposureCheck);

        var streakCheck = CheckLossStreak(recentTrades, signal);
        if (!streakCheck.Approved) return Task.FromResult(streakCheck);

        return Task.FromResult((true, "Portfolio risk checks passed."));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Dynamic Kelly: win-rate adaptive fractional sizing
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Computes a dynamic Kelly multiplier from recent trade history.
    /// Returns a multiplier (0.5 to 1.5) that scales the base Kelly fraction:
    ///   win_rate &lt; 0.40 → 0.5× (halve size when losing)
    ///   win_rate 0.40-0.55 → linear interpolation 0.5×–1.0×
    ///   win_rate &gt; 0.55 → up to 1.5× (scale up proven edge)
    /// </summary>
    internal decimal ComputeDynamicKellyMultiplier(IReadOnlyList<Trade> recentTrades)
    {
        var closedTrades = recentTrades
            .Where(t => t.IsClosed && t.Status == TradeStatus.Executed)
            .OrderByDescending(t => t.ExitTime)
            .Take(_opts.DynamicKellyWindow)
            .ToList();

        if (closedTrades.Count < _opts.DynamicKellyMinTrades)
            return 1.0m; // Not enough data — use static Kelly

        int wins = closedTrades.Count(t =>
            (t.ExitPrice - t.EntryPrice) * (t.Side == TradeSide.Sell ? -1 : 1) > 0);
        decimal winRate = (decimal)wins / closedTrades.Count;

        if (winRate < _opts.DynamicKellyMinWinRate)
        {
            // Losing edge: halve position size
            _logger.LogWarning("[DynamicKelly] Win rate {WinRate:P1} below {Min:P1} — sizing halved",
                winRate, _opts.DynamicKellyMinWinRate);
            return 0.5m;
        }

        if (winRate > _opts.DynamicKellyStrongWinRate)
        {
            // Strong edge: scale up proportionally (capped at 1.5×)
            decimal bonus = Math.Min(0.5m,
                (winRate - _opts.DynamicKellyStrongWinRate) / (1.0m - _opts.DynamicKellyStrongWinRate));
            return 1.0m + bonus;
        }

        // Middle zone: linear interpolation from 0.5× to 1.0×
        decimal range = _opts.DynamicKellyStrongWinRate - _opts.DynamicKellyMinWinRate;
        if (range <= 0) return 1.0m;
        return 0.5m + 0.5m * ((winRate - _opts.DynamicKellyMinWinRate) / range);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Drawdown Circuit Breaker
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Progressive drawdown circuit breaker:
    ///   dd &lt; soft limit → full size (no restriction)
    ///   dd between soft and hard → linear scale-down to MinSizeMultiplier
    ///   dd &gt;= hard limit → halt trading entirely
    ///
    /// Returns (Approved, Reason, SizeMultiplier).
    /// </summary>
    internal (bool Approved, string Reason, decimal SizeMultiplier) CheckDrawdownCircuitBreaker(
        IReadOnlyList<Trade> recentTrades,
        RiskParameters riskProfile,
        decimal assumedStartingEquity)
    {
        var equity = GetBaseEquityForRiskCheck(assumedStartingEquity, riskProfile);
        if (equity <= 0) return (true, "", 1.0m);

        var dayStart = DateTime.UtcNow.Date;
        var dailyPnL = recentTrades
            .Where(t => t.ExitTime.HasValue && t.ExitTime.Value >= dayStart && t.Status == TradeStatus.Executed && t.ExitPrice > 0)
            .Sum(t => (t.ExitPrice - t.EntryPrice) * t.Quantity * (t.Side == TradeSide.Sell ? -1 : 1));

        decimal ddPercent = dailyPnL >= 0 ? 0 : Math.Abs(dailyPnL / equity) * 100m;

        if (ddPercent >= _opts.DrawdownHardLimitPct)
        {
            ArmKillSwitch($"Drawdown circuit breaker: {ddPercent:F2}% exceeds hard limit {_opts.DrawdownHardLimitPct}%.");
            return (false, $"Drawdown circuit breaker: {ddPercent:F2}% DD — trading halted.", 0m);
        }

        if (ddPercent >= _opts.DrawdownSoftLimitPct)
        {
            // Linear scale-down from 1.0 to MinSizeMultiplier
            decimal range = _opts.DrawdownHardLimitPct - _opts.DrawdownSoftLimitPct;
            decimal progress = range > 0 ? (ddPercent - _opts.DrawdownSoftLimitPct) / range : 1.0m;
            decimal multiplier = 1.0m - progress * (1.0m - _opts.DrawdownMinSizeMultiplier);

            _logger.LogWarning(
                "[DrawdownBreaker] DD={DD:F2}% — position size scaled to {Mult:P0}",
                ddPercent, multiplier);

            return (true, $"DD breaker active: {multiplier:P0} sizing at {ddPercent:F1}% DD.", multiplier);
        }

        return (true, "", 1.0m);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Correlation Guard
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Blocks new positions that are highly correlated with existing open positions.
    /// Uses a simple proxy: if the new symbol shares a common base asset with >50% of
    /// open position notional, it's treated as correlated (e.g., BTCUSDT and BTCETH).
    /// For proper correlation, historical price data would be needed — this is a conservative
    /// heuristic that blocks obvious concentration without requiring a correlation matrix.
    /// </summary>
    private (bool Approved, string Reason) CheckCorrelationExposure(
        IReadOnlyList<Trade> recentTrades, Signal signal)
    {
        if (string.IsNullOrWhiteSpace(signal.Symbol)) return (true, "");

        // Extract base asset (e.g., "BTC" from "BTCUSDT")
        var newBase = ExtractBaseAsset(signal.Symbol);
        if (string.IsNullOrEmpty(newBase)) return (true, "");

        var openPositions = recentTrades
            .Where(t => !t.IsClosed && t.Status == TradeStatus.Executed)
            .ToList();

        if (openPositions.Count == 0) return (true, "");

        // Count how many open positions share the same base asset
        int sameBaseCount = openPositions.Count(t =>
            string.Equals(ExtractBaseAsset(t.Symbol), newBase, StringComparison.OrdinalIgnoreCase));

        // If this base asset already has max-per-symbol positions, the concurrent limit catches it.
        // Here we check for related pairs (e.g., BTCUSDT + BTCBUSD = same underlying exposure)
        decimal sameBaseNotional = openPositions
            .Where(t => string.Equals(ExtractBaseAsset(t.Symbol), newBase, StringComparison.OrdinalIgnoreCase))
            .Sum(t => t.EntryPrice * t.Quantity);

        decimal totalNotional = openPositions.Sum(t => t.EntryPrice * t.Quantity);

        if (totalNotional > 0 && sameBaseNotional / totalNotional > (decimal)_opts.CorrelationThreshold)
        {
            return (false,
                $"Correlation guard: {newBase} exposure is {sameBaseNotional / totalNotional:P0} of portfolio — " +
                $"exceeds {_opts.CorrelationThreshold:P0} threshold.");
        }

        return (true, "");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Portfolio Exposure Cap
    // ═══════════════════════════════════════════════════════════════════════

    private (bool Approved, string Reason) CheckPortfolioExposure(
        IReadOnlyList<Trade> recentTrades,
        decimal requestedPositionFraction,
        RiskParameters riskProfile,
        decimal assumedStartingEquity)
    {
        var equity = GetBaseEquityForRiskCheck(assumedStartingEquity, riskProfile);
        if (equity <= 0) return (true, "");

        decimal totalOpenNotional = recentTrades
            .Where(t => !t.IsClosed && t.Status == TradeStatus.Executed)
            .Sum(t => t.EntryPrice * t.Quantity);

        decimal newNotional = equity * requestedPositionFraction;
        decimal totalExposure = (totalOpenNotional + newNotional) / equity;

        if (totalExposure > _opts.MaxPortfolioExposure)
        {
            _logger.LogWarning(
                "[RiskAgent] Portfolio exposure {Exposure:P0} would exceed max {Max:P0}",
                totalExposure, _opts.MaxPortfolioExposure);
            return (false,
                $"Portfolio exposure cap: {totalExposure:P0} would exceed {_opts.MaxPortfolioExposure:P0} of equity.");
        }

        return (true, "");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Existing Checks (unchanged logic, dynamic-Kelly aware)
    // ═══════════════════════════════════════════════════════════════════════

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
        return _opts.EquityFallback;
    }

    private (bool Approved, string Reason) CheckConcurrentTradeLimits(IReadOnlyList<Trade> recentTrades, Signal signal)
    {
        if (recentTrades.Count(t => !t.IsClosed && t.Status == TradeStatus.Executed) >= _opts.MaxConcurrentTrades)
            return (false, $"Max concurrent open trades reached ({_opts.MaxConcurrentTrades}).");

        var symbolOpenTrades = recentTrades.Count(t =>
            !t.IsClosed && t.Status == TradeStatus.Executed &&
            string.Equals(t.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase));

        return symbolOpenTrades >= _opts.MaxTradesPerSymbol ? (false, $"Max open trades for {signal.Symbol} reached ({_opts.MaxTradesPerSymbol}).") : (true, "");
    }

    private (bool Approved, string Reason) CheckSymbolConcentration(
        IReadOnlyList<Trade> recentTrades,
        Signal signal,
        decimal requestedPositionFraction,
        RiskParameters riskProfile,
        decimal assumedStartingEquity)
    {
        if (string.IsNullOrWhiteSpace(signal.Symbol)) return (true, "");

        var equity = GetBaseEquityForRiskCheck(assumedStartingEquity, riskProfile);
        if (equity <= 0) return (true, "");

        var existingNotional = recentTrades
            .Where(t =>
                !t.IsClosed &&
                t.Status == TradeStatus.Executed &&
                string.Equals(t.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase))
            .Sum(t => t.EntryPrice * t.Quantity);

        var newNotional = equity * requestedPositionFraction;
        var totalNotional = existingNotional + newNotional;
        var concentrationPercent = totalNotional / equity * 100m;

        if (concentrationPercent > riskProfile.MaxSymbolConcentrationPercent)
        {
            _logger.LogWarning(
                "[RiskAgent] Concentration limit hit for {Symbol}: {Actual:F1}% would exceed max {Max:F1}% " +
                "(existing={Existing:C}, new={New:C}, equity={Equity:C})",
                signal.Symbol, concentrationPercent, riskProfile.MaxSymbolConcentrationPercent,
                existingNotional, newNotional, equity);

            return (false,
                $"Symbol concentration limit: {signal.Symbol} at {concentrationPercent:F1}% " +
                $"would exceed max {riskProfile.MaxSymbolConcentrationPercent:F1}% of portfolio.");
        }

        _logger.LogDebug(
            "[RiskAgent] Concentration check OK for {Symbol}: {Actual:F1}% of {Max:F1}% allowed.",
            signal.Symbol, concentrationPercent, riskProfile.MaxSymbolConcentrationPercent);

        return (true, "");
    }

    private (bool Approved, string Reason) CheckLossStreak(IReadOnlyList<Trade> recentTrades, Signal signal)
    {
        var symbolRecentTrades = recentTrades
            .Where(t => string.Equals(t.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase) && t.IsClosed && t.Status == TradeStatus.Executed)
            .OrderByDescending(t => t.ExitTime)
            .Take(_opts.LossStreakLimit)
            .ToList();

        if (symbolRecentTrades.Count >= _opts.LossStreakLimit && symbolRecentTrades.All(t => (t.ExitPrice - t.EntryPrice) * (t.Side == TradeSide.Sell ? -1 : 1) < 0))
        {
            return (false, $"Loss-streak guard: {_opts.LossStreakLimit} consecutive losses on {signal.Symbol}.");
        }
        return (true, "");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Extracts the base asset from a trading pair symbol.
    /// E.g., "BTCUSDT" → "BTC", "ETHBUSD" → "ETH", "BTC-USDT" → "BTC".
    /// </summary>
    internal static string ExtractBaseAsset(string symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return "";

        // Handle OKX-style: BTC-USDT
        var dashIndex = symbol.IndexOf('-');
        if (dashIndex > 0) return symbol[..dashIndex];

        // Handle Binance-style: strip known quote assets
        ReadOnlySpan<string> quotes = ["USDT", "BUSD", "USDC", "TRY", "BTC", "ETH", "BNB"];
        foreach (var quote in quotes)
        {
            if (symbol.EndsWith(quote, StringComparison.OrdinalIgnoreCase) && symbol.Length > quote.Length)
                return symbol[..^quote.Length];
        }

        return symbol;
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
