using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Threading;
using ThatsMyQuant.Application.Risk;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Dedicated portfolio governor responsible for sizing, drawdown controls,
/// portfolio heat caps, and execution-quality feedback loops.
/// </summary>
public class RiskAgent : IFocusedAgent
{
    private readonly ILogger<RiskAgent> _logger;
    private readonly MemoryPortfolioManager? _portfolioManager;
    private readonly TokenBucketRateLimiter _rateLimiter;
    private readonly RiskGovernorOptions _options;
    private readonly ConcurrentDictionary<Guid, Trade> _observedTrades = new();
    private volatile bool _killSwitchArmed;

    private static readonly IReadOnlyList<HashSet<string>> KnownCorrelatedGroups =
    [
        new(StringComparer.OrdinalIgnoreCase) { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BTC-USD", "ETH-USD", "SOL-USD" },
        new(StringComparer.OrdinalIgnoreCase) { "XAUUSD", "XAGUSD" },
        new(StringComparer.OrdinalIgnoreCase) { "EURUSD", "GBPUSD", "AUDUSD" }
    ];

    public bool IsKillSwitchArmed => _killSwitchArmed;

    public RiskAgent(
        ILogger<RiskAgent> logger,
        MemoryPortfolioManager? portfolioManager = null,
        IOptions<RiskGovernorOptions>? options = null)
    {
        _logger = logger;
        _portfolioManager = portfolioManager;
        _options = options?.Value ?? new RiskGovernorOptions();
        _rateLimiter = new TokenBucketRateLimiter(10, 10.0);
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
        decimal symbol24hVolume,
        IReadOnlyList<Trade>? recentTrades = null,
        Signal? signal = null)
    {
        if (_killSwitchArmed) return Task.FromResult((false, "Kill-switch is armed.", 0m));
        if (!_rateLimiter.TryConsume()) return Task.FromResult((false, "HFT Rate limit exceeded.", 0m));

        var state = BuildPortfolioState(
            recentTrades,
            signal,
            candidate.PositionSizePercent,
            riskProfile,
            riskProfile.StartingEquity);

        if (state.DrawdownPercent >= _options.HardDrawdownPercent)
            return Task.FromResult((false, "Hard drawdown guard is active.", 0m));

        if (state.ExecutionHealth < _options.MinimumExecutionHealthToTrade)
            return Task.FromResult((false, "Execution quality is too weak for new risk.", 0m));

        decimal confidenceKelly = CalculateConfidenceKelly(candidate, riskProfile);
        decimal requestedFraction = candidate.PositionSizePercent > 0m
            ? candidate.PositionSizePercent
            : riskProfile.MaxPositionSizePercent / 100m;

        decimal safeAllocation = Math.Min(requestedFraction, confidenceKelly);
        safeAllocation *= state.DrawdownMultiplier;
        safeAllocation *= state.ExecutionMultiplier;
        safeAllocation *= state.PerformanceMultiplier;
        safeAllocation *= ResolveHeatCompressionMultiplier(state.OpenHeat);
        safeAllocation *= ResolveCorrelationCompressionMultiplier(state.CorrelatedHeat);
        safeAllocation *= ResolveLiquidityMultiplier(symbol24hVolume);
        safeAllocation = Math.Min(safeAllocation, ResolveHeadroomCap(signal, state, riskProfile));

        if (safeAllocation <= _options.MinimumTradableFraction)
        {
            return Task.FromResult((false, "Risk governor compressed size below minimum tradable threshold.", 0m));
        }

        var reasons = new List<string>
        {
            $"size={safeAllocation:P2}",
            $"drawdown={state.DrawdownPercent:F2}%",
            $"executionHealth={state.ExecutionHealth:F2}",
            $"heat={state.OpenHeat:P1}"
        };

        if (signal != null && state.SymbolHeat > 0m)
            reasons.Add($"{signal.Symbol} heat={state.SymbolHeat:P1}");

        return Task.FromResult((true, string.Join(", ", reasons), safeAllocation));
    }

    public Task<(bool Approved, string Reason)> EvaluatePortfolioGuardrailsAsync(
        IReadOnlyList<Trade> recentTrades,
        Signal signal,
        decimal requestedPositionFraction,
        RiskParameters riskProfile,
        decimal assumedStartingEquity = 0m)
    {
        if (_killSwitchArmed) return Task.FromResult((false, "Kill-switch is armed."));

        var state = BuildPortfolioState(
            recentTrades,
            signal,
            requestedPositionFraction,
            riskProfile,
            assumedStartingEquity);

        var balanceCheck = CheckAvailableBalance(signal);
        if (!balanceCheck.Approved) return Task.FromResult(balanceCheck);

        var lossCheck = CheckDailyLossLimit(state, riskProfile);
        if (!lossCheck.Approved) return Task.FromResult(lossCheck);

        var drawdownCheck = CheckDrawdownLimit(state);
        if (!drawdownCheck.Approved) return Task.FromResult(drawdownCheck);

        var countCheck = CheckConcurrentTradeLimits(state.OpenTrades, signal);
        if (!countCheck.Approved) return Task.FromResult(countCheck);

        var heatCheck = CheckPortfolioHeat(state);
        if (!heatCheck.Approved) return Task.FromResult(heatCheck);

        var symbolCheck = CheckSymbolExposure(state, signal);
        if (!symbolCheck.Approved) return Task.FromResult(symbolCheck);

        var correlationCheck = CheckCorrelationExposure(state, signal);
        if (!correlationCheck.Approved) return Task.FromResult(correlationCheck);

        var executionCheck = CheckExecutionHealth(state);
        if (!executionCheck.Approved) return Task.FromResult(executionCheck);

        var streakCheck = CheckLossStreak(state.ClosedTrades, signal);
        if (!streakCheck.Approved) return Task.FromResult(streakCheck);

        return Task.FromResult((true, "Portfolio risk checks passed."));
    }

    private (bool Approved, string Reason) CheckAvailableBalance(Signal signal)
    {
        if (_portfolioManager == null || string.IsNullOrWhiteSpace(signal.Symbol)) return (true, "");

        var quoteAsset = signal.Symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ? "USDT" : "TRY";
        var available = _portfolioManager.GetAvailableBalance(quoteAsset);
        return available <= 0m ? (false, "Insufficient available balance.") : (true, "");
    }

    public void NotifyTradeUpdate(Trade trade)
    {
        if (trade == null) return;

        _observedTrades.TryGetValue(trade.Id, out var previous);
        _observedTrades[trade.Id] = trade;

        TrimObservedTradesIfNeeded();
        SyncPortfolioSnapshot(previous, trade);
    }

    private void SyncPortfolioSnapshot(Trade? previous, Trade trade)
    {
        if (_portfolioManager == null) return;

        if (previous is { Status: TradeStatus.Executed } && !previous.IsClosed &&
            (trade.IsClosed || trade.Status != TradeStatus.Executed))
        {
            _portfolioManager.ClosePosition(previous.Symbol, trade.ExitPrice > 0m ? trade.ExitPrice : previous.EntryPrice);
        }

        if (trade.Status == TradeStatus.Executed && !trade.IsClosed)
        {
            _portfolioManager.OpenPosition(
                trade.Symbol,
                trade.EntryPrice,
                trade.Quantity,
                trade.Side == TradeSide.Buy);
        }
        else if (trade.Status == TradeStatus.Executed && trade.IsClosed)
        {
            _portfolioManager.ClosePosition(trade.Symbol, trade.ExitPrice > 0m ? trade.ExitPrice : trade.EntryPrice);
        }
    }

    private void TrimObservedTradesIfNeeded()
    {
        if (_observedTrades.Count <= _options.MaxTrackedTrades) return;

        var staleTradeIds = _observedTrades.Values
            .Where(t => t.IsClosed)
            .OrderBy(t => t.ExitTime ?? t.EntryTime)
            .Take(Math.Max(1, _observedTrades.Count - _options.MaxTrackedTrades))
            .Select(t => t.Id)
            .ToList();

        foreach (var tradeId in staleTradeIds)
        {
            _observedTrades.TryRemove(tradeId, out _);
        }
    }

    private (bool Approved, string Reason) CheckDailyLossLimit(PortfolioRiskState state, RiskParameters riskProfile)
    {
        if (state.DailyLossPercent >= riskProfile.MaxDailyLossPercent)
        {
            ArmKillSwitch($"Daily loss limit breached ({state.DailyLossPercent:F2}%).");
            return (false, "Daily loss limit breached.");
        }

        return (true, "");
    }

    private (bool Approved, string Reason) CheckDrawdownLimit(PortfolioRiskState state)
    {
        if (state.DrawdownPercent >= _options.HardDrawdownPercent)
        {
            ArmKillSwitch($"Drawdown guard breached ({state.DrawdownPercent:F2}%).");
            return (false, "Portfolio drawdown breaker is active.");
        }

        return (true, "");
    }

    private decimal GetBaseEquityForRiskCheck(decimal assumedStartingEquity, RiskParameters riskProfile)
    {
        if (assumedStartingEquity > 0m) return assumedStartingEquity;
        if (riskProfile.StartingEquity > 0m) return riskProfile.StartingEquity;
        return 100_000m;
    }

    private (bool Approved, string Reason) CheckConcurrentTradeLimits(IReadOnlyList<Trade> openTrades, Signal signal)
    {
        if (openTrades.Count >= _options.MaxOpenTrades)
            return (false, $"Max concurrent open trades reached ({_options.MaxOpenTrades}).");

        var symbolOpenTrades = openTrades.Count(t =>
            string.Equals(t.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase));

        return symbolOpenTrades >= _options.MaxOpenTradesPerSymbol
            ? (false, $"Max open trades for {signal.Symbol} reached ({_options.MaxOpenTradesPerSymbol}).")
            : (true, "");
    }

    private (bool Approved, string Reason) CheckPortfolioHeat(PortfolioRiskState state)
    {
        if (state.OpenHeat >= _options.MaxPortfolioHeat)
        {
            return (false, $"Portfolio heat is already at {state.OpenHeat:P1}, above the {_options.MaxPortfolioHeat:P0} cap.");
        }

        return (true, "");
    }

    private (bool Approved, string Reason) CheckSymbolExposure(PortfolioRiskState state, Signal signal)
    {
        if (state.SymbolHeat >= _options.MaxSymbolExposure)
        {
            return (false, $"{signal.Symbol} exposure is already at {state.SymbolHeat:P1}, above the {_options.MaxSymbolExposure:P0} cap.");
        }

        return (true, "");
    }

    private (bool Approved, string Reason) CheckCorrelationExposure(PortfolioRiskState state, Signal signal)
    {
        if (state.CorrelatedHeat >= _options.MaxCorrelatedExposure)
        {
            return (false, $"Correlated exposure around {signal.Symbol} is already at {state.CorrelatedHeat:P1}, above the {_options.MaxCorrelatedExposure:P0} cap.");
        }

        return (true, "");
    }

    private (bool Approved, string Reason) CheckExecutionHealth(PortfolioRiskState state)
    {
        if (state.ExecutionHealth < _options.MinimumExecutionHealthToTrade)
        {
            return (false, "Execution venue quality is degraded beyond risk tolerance.");
        }

        return (true, "");
    }

    private (bool Approved, string Reason) CheckLossStreak(IReadOnlyList<Trade> closedTrades, Signal signal)
    {
        var symbolRecentTrades = closedTrades
            .Where(t => string.Equals(t.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.ExitTime)
            .Take(3)
            .ToList();

        if (symbolRecentTrades.Count >= 3 && symbolRecentTrades.All(t => CalculateNetPnl(t) < 0m))
        {
            return (false, $"Loss-streak guard: 3 consecutive losses on {signal.Symbol}.");
        }

        return (true, "");
    }

    private PortfolioRiskState BuildPortfolioState(
        IReadOnlyList<Trade>? recentTrades,
        Signal? signal,
        decimal requestedPositionFraction,
        RiskParameters riskProfile,
        decimal assumedStartingEquity)
    {
        var mergedTrades = MergeKnownTrades(recentTrades);
        var executedTrades = mergedTrades
            .Where(t => t.Status == TradeStatus.Executed)
            .ToList();
        var openTrades = executedTrades
            .Where(t => !t.IsClosed)
            .OrderByDescending(t => t.EntryTime)
            .ToList();
        var closedTrades = executedTrades
            .Where(t => t.IsClosed)
            .OrderByDescending(t => t.ExitTime ?? t.EntryTime)
            .ToList();

        var baseEquity = GetBaseEquityForRiskCheck(assumedStartingEquity, riskProfile);
        var openHeat = ResolveOpenHeat(openTrades, baseEquity);
        var symbolHeat = signal == null
            ? 0m
            : ResolveOpenHeat(openTrades.Where(t => string.Equals(t.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase)).ToList(), baseEquity);
        var correlatedHeat = signal == null
            ? 0m
            : ResolveOpenHeat(openTrades.Where(t => IsCorrelated(t.Symbol, signal.Symbol)).ToList(), baseEquity);
        var drawdownPercent = ResolveDrawdownPercent(baseEquity, closedTrades);
        var dailyLossPercent = ResolveDailyLossPercent(baseEquity, closedTrades);
        var executionHealth = ResolveExecutionHealth(closedTrades);

        return new PortfolioRiskState(
            OpenTrades: openTrades,
            ClosedTrades: closedTrades,
            OpenHeat: openHeat,
            SymbolHeat: symbolHeat,
            CorrelatedHeat: correlatedHeat,
            DrawdownPercent: drawdownPercent,
            DailyLossPercent: dailyLossPercent,
            ExecutionHealth: executionHealth,
            ExecutionMultiplier: ResolveExecutionMultiplier(executionHealth),
            PerformanceMultiplier: ResolvePerformanceMultiplier(closedTrades, signal),
            DrawdownMultiplier: ResolveDrawdownMultiplier(drawdownPercent),
            RequestedPositionFraction: requestedPositionFraction);
    }

    private IReadOnlyList<Trade> MergeKnownTrades(IReadOnlyList<Trade>? recentTrades)
    {
        var merged = new Dictionary<Guid, Trade>();

        if (recentTrades != null)
        {
            foreach (var trade in recentTrades)
            {
                merged[trade.Id] = trade;
            }
        }

        foreach (var entry in _observedTrades)
        {
            merged[entry.Key] = entry.Value;
        }

        return merged.Values
            .OrderByDescending(t => t.ExitTime ?? t.EntryTime)
            .Take(_options.MaxTrackedTrades)
            .ToList();
    }

    private decimal ResolveOpenHeat(IReadOnlyList<Trade> openTrades, decimal baseEquity)
    {
        var explicitHeat = openTrades.Sum(t => Math.Max(0m, t.PositionSizePercent));
        if (explicitHeat > 0m) return explicitHeat;

        if (baseEquity <= 0m) return 0m;
        return openTrades.Sum(t => Math.Max(0m, t.EntryPrice * t.Quantity) / baseEquity);
    }

    private decimal ResolveDrawdownPercent(decimal baseEquity, IReadOnlyList<Trade> closedTrades)
    {
        if (_portfolioManager != null)
        {
            var portfolioDrawdown = _portfolioManager.GetDrawdownPercent();
            if (portfolioDrawdown > 0m) return portfolioDrawdown;
        }

        if (baseEquity <= 0m || closedTrades.Count == 0) return 0m;

        decimal runningEquity = baseEquity;
        decimal peakEquity = baseEquity;
        decimal maxDrawdown = 0m;

        foreach (var trade in closedTrades.OrderBy(t => t.ExitTime ?? t.EntryTime))
        {
            runningEquity += CalculateNetPnl(trade);
            if (runningEquity > peakEquity) peakEquity = runningEquity;
            if (peakEquity <= 0m) continue;

            var drawdown = ((peakEquity - runningEquity) / peakEquity) * 100m;
            if (drawdown > maxDrawdown) maxDrawdown = drawdown;
        }

        return Math.Max(0m, maxDrawdown);
    }

    private static decimal ResolveDailyLossPercent(decimal baseEquity, IReadOnlyList<Trade> closedTrades)
    {
        if (baseEquity <= 0m) return 0m;

        var dailyPnl = closedTrades
            .Where(t => t.ExitTime.HasValue && t.ExitTime.Value.Date == DateTime.UtcNow.Date)
            .Sum(CalculateNetPnl);
        return Math.Abs(Math.Min(0m, dailyPnl) / baseEquity) * 100m;
    }

    private decimal ResolveExecutionHealth(IReadOnlyList<Trade> closedTrades)
    {
        var liveTrades = closedTrades
            .Where(IsLiveTrade)
            .Take(_options.AdaptiveLookbackTrades)
            .ToList();
        if (liveTrades.Count < _options.MinimumClosedTradesForAdaptiveSizing)
            return 1m;

        var avgSlippage = liveTrades.Average(t => Math.Abs(t.EntrySlippagePercent));
        var avgLatency = liveTrades.Average(t => (decimal)Math.Max(0L, t.ExecutionLatencyMs));
        var venueBiasValues = liveTrades
            .Select(t => t.VenueExecutionBias > 0m ? t.VenueExecutionBias : 1m)
            .ToList();
        var avgVenueBias = venueBiasValues.Count > 0 ? venueBiasValues.Average() : 1m;

        var slippageScore = ScoreInverse(avgSlippage, _options.PreferredEntrySlippagePercent, _options.HardEntrySlippagePercent);
        var latencyScore = ScoreInverse(avgLatency, _options.PreferredExecutionLatencyMs, _options.HardExecutionLatencyMs);
        var venueScore = Clamp(avgVenueBias, 0.50m, 1.05m);

        return Clamp((slippageScore * 0.45m) + (latencyScore * 0.35m) + (venueScore * 0.20m), 0m, 1.05m);
    }

    private decimal ResolveExecutionMultiplier(decimal executionHealth)
        => Clamp(
            _options.MinimumExecutionMultiplier +
            ((_options.MaximumExecutionMultiplier - _options.MinimumExecutionMultiplier) * executionHealth),
            _options.MinimumExecutionMultiplier,
            _options.MaximumExecutionMultiplier);

    private decimal ResolvePerformanceMultiplier(IReadOnlyList<Trade> closedTrades, Signal? signal)
    {
        var sample = closedTrades
            .Where(t => signal == null || string.Equals(t.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase))
            .Take(_options.AdaptiveLookbackTrades)
            .ToList();

        if (sample.Count < _options.MinimumClosedTradesForAdaptiveSizing)
        {
            sample = closedTrades
                .Take(_options.AdaptiveLookbackTrades)
                .ToList();
        }

        if (sample.Count < _options.MinimumClosedTradesForAdaptiveSizing)
            return 1m;

        var wins = sample.Where(t => CalculateNetPnl(t) > 0m).ToList();
        var losses = sample.Where(t => CalculateNetPnl(t) < 0m).ToList();
        var grossProfit = wins.Sum(CalculateNetPnl);
        var grossLoss = Math.Abs(losses.Sum(CalculateNetPnl));
        var winRate = sample.Count == 0 ? 0m : wins.Count / (decimal)sample.Count;
        var profitFactor = grossLoss <= 0m
            ? (grossProfit > 0m ? 3m : 1m)
            : grossProfit / grossLoss;

        var adaptiveKelly = KellyCriterionCalculator.CalculateFractionalKelly(
            winRate,
            Math.Max(1m, profitFactor),
            _options.FractionalKelly);

        return Clamp(
            _options.MinimumPerformanceMultiplier + (adaptiveKelly * (_options.MaximumPerformanceMultiplier - _options.MinimumPerformanceMultiplier) * 2m),
            _options.MinimumPerformanceMultiplier,
            _options.MaximumPerformanceMultiplier);
    }

    private decimal ResolveDrawdownMultiplier(decimal drawdownPercent)
    {
        if (drawdownPercent <= _options.SoftDrawdownPercent) return 1m;
        if (drawdownPercent >= _options.HardDrawdownPercent) return _options.MinimumDrawdownMultiplier;

        var span = _options.HardDrawdownPercent - _options.SoftDrawdownPercent;
        if (span <= 0m) return _options.MinimumDrawdownMultiplier;

        var progress = (drawdownPercent - _options.SoftDrawdownPercent) / span;
        return Clamp(1m - ((1m - _options.MinimumDrawdownMultiplier) * progress), _options.MinimumDrawdownMultiplier, 1m);
    }

    private decimal ResolveHeatCompressionMultiplier(decimal openHeat)
    {
        var softHeat = _options.MaxPortfolioHeat * 0.70m;
        if (openHeat <= softHeat) return 1m;
        if (openHeat >= _options.MaxPortfolioHeat) return 0.35m;

        var progress = (openHeat - softHeat) / Math.Max(0.0001m, _options.MaxPortfolioHeat - softHeat);
        return Clamp(1m - (0.65m * progress), 0.35m, 1m);
    }

    private decimal ResolveCorrelationCompressionMultiplier(decimal correlatedHeat)
    {
        var softCorrelation = _options.MaxCorrelatedExposure * 0.75m;
        if (correlatedHeat <= softCorrelation) return 1m;
        if (correlatedHeat >= _options.MaxCorrelatedExposure) return 0.25m;

        var progress = (correlatedHeat - softCorrelation) / Math.Max(0.0001m, _options.MaxCorrelatedExposure - softCorrelation);
        return Clamp(1m - (0.75m * progress), 0.25m, 1m);
    }

    private decimal ResolveLiquidityMultiplier(decimal symbol24hVolume)
    {
        if (symbol24hVolume <= 0m) return 1m;
        if (symbol24hVolume <= _options.HardLiquidity24hVolume) return 0.25m;
        if (symbol24hVolume >= _options.SoftLiquidity24hVolume) return 1m;

        var progress = (symbol24hVolume - _options.HardLiquidity24hVolume) /
                       Math.Max(1m, _options.SoftLiquidity24hVolume - _options.HardLiquidity24hVolume);
        return Clamp(0.25m + (0.75m * progress), 0.25m, 1m);
    }

    private decimal ResolveHeadroomCap(Signal? signal, PortfolioRiskState state, RiskParameters riskProfile)
    {
        var caps = new List<decimal>
        {
            riskProfile.MaxPositionSizePercent / 100m,
            Math.Max(0m, _options.MaxPortfolioHeat - state.OpenHeat)
        };

        if (signal != null)
        {
            caps.Add(Math.Max(0m, _options.MaxSymbolExposure - state.SymbolHeat));
            caps.Add(Math.Max(0m, _options.MaxCorrelatedExposure - state.CorrelatedHeat));
        }

        return caps.Min();
    }

    private decimal CalculateConfidenceKelly(TradeDecision candidate, RiskParameters riskProfile)
    {
        var p = Clamp(candidate.Confidence / 100m, 0m, 0.99m);
        var q = 1m - p;
        const decimal b = 1.5m;
        var rawKelly = Math.Max(0m, (b * p - q) / b);

        return Math.Min(rawKelly * _options.FractionalKelly, riskProfile.MaxPositionSizePercent / 100m);
    }

    private static decimal ScoreInverse(decimal actual, decimal preferred, decimal hard)
    {
        if (actual <= preferred) return 1m;
        if (actual >= hard) return 0m;

        return 1m - ((actual - preferred) / Math.Max(0.0001m, hard - preferred));
    }

    private static decimal Clamp(decimal value, decimal min, decimal max)
        => Math.Min(max, Math.Max(min, value));

    private static bool IsLiveTrade(Trade trade)
        => string.Equals(trade.ExecutionMode, "REAL", StringComparison.OrdinalIgnoreCase)
           || (!string.IsNullOrWhiteSpace(trade.PromotionStage)
               && trade.PromotionStage.Contains("Live", StringComparison.OrdinalIgnoreCase));

    private static decimal CalculateNetPnl(Trade trade)
    {
        var direction = trade.Side == TradeSide.Sell ? -1m : 1m;
        var grossPnl = (trade.ExitPrice - trade.EntryPrice) * trade.Quantity * direction;
        var slippageCost = trade.EntryPrice * trade.Quantity * (Math.Abs(trade.EntrySlippagePercent) / 100m);
        return grossPnl - slippageCost;
    }

    private static bool IsCorrelated(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var group in KnownCorrelatedGroups)
        {
            if (group.Contains(left) && group.Contains(right))
                return true;
        }

        return false;
    }

    private sealed record PortfolioRiskState(
        IReadOnlyList<Trade> OpenTrades,
        IReadOnlyList<Trade> ClosedTrades,
        decimal OpenHeat,
        decimal SymbolHeat,
        decimal CorrelatedHeat,
        decimal DrawdownPercent,
        decimal DailyLossPercent,
        decimal ExecutionHealth,
        decimal ExecutionMultiplier,
        decimal PerformanceMultiplier,
        decimal DrawdownMultiplier,
        decimal RequestedPositionFraction);
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
