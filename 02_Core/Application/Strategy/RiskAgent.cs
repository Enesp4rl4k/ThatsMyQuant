using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Globalization;
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
public class RiskAgent : IFocusedAgent, INotificationHandler<VpinToxicityAlert>, INotificationHandler<StaleDataAlert>
{
    private readonly ILogger<RiskAgent> _logger;
    private readonly IPortfolioManager? _portfolioManager;
    private readonly TokenBucketRateLimiter _rateLimiter;
    private readonly RiskGovernorOptions _options;
    private readonly ConcurrentDictionary<Guid, Trade> _observedTrades = new();
    private volatile bool _softPauseArmed;
    private volatile string _softPauseReason = "Soft pause disarmed.";

    private static readonly IReadOnlyList<HashSet<string>> KnownCorrelatedGroups =
    [
        new(StringComparer.OrdinalIgnoreCase) { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BTC-USD", "ETH-USD", "SOL-USD" },
        new(StringComparer.OrdinalIgnoreCase) { "XAUUSD", "XAGUSD" },
        new(StringComparer.OrdinalIgnoreCase) { "EURUSD", "GBPUSD", "AUDUSD" }
    ];

    public bool IsSoftPauseArmed => _softPauseArmed;
    public string SoftPauseReason => _softPauseReason;
    public bool IsKillSwitchArmed => IsSoftPauseArmed;

    private readonly PortfolioRiskAnalytics? _portfolioRisk;

    public RiskAgent(
        ILogger<RiskAgent> logger,
        IPortfolioManager? portfolioManager = null,
        IOptions<RiskGovernorOptions>? options = null,
        PortfolioRiskAnalytics? portfolioRisk = null)
    {
        _logger = logger;
        _portfolioManager = portfolioManager;
        _options = options?.Value ?? new RiskGovernorOptions();
        _rateLimiter = new TokenBucketRateLimiter(10, 10.0);
        _portfolioRisk = portfolioRisk;
    }

    /// <summary>Minimum periodic-return samples before CVaR compression activates.</summary>
    private const int CvarMinSamples = 30;
    /// <summary>Confidence level for historical CVaR computation (95th percentile tail).</summary>
    private const double CvarConfidenceLevel = 0.95;
    /// <summary>Default baseline CVaR; compression kicks in when tail exceeds this value.</summary>
    private const decimal CvarBaseline = 0.02m;
    /// <summary>Fractional size reduction applied per 1× of excess tail-risk above baseline (20%).</summary>
    private const decimal CvarCompressionStep = 0.2m;
    /// <summary>Hard floor for CVaR-driven position-size compression (40% of nominal).</summary>
    private const decimal CvarCompressionFloor = 0.4m;

    /// <summary>
    /// CVaR-based compression: when the recent periodic-return tail is heavy, we
    /// shrink position sizing beyond the standard drawdown multiplier. This catches
    /// scenarios where drawdown hasn't yet accumulated but the *distribution* of
    /// returns has fattened — a leading indicator of trouble.
    /// Returns a multiplier in [<see cref="CvarCompressionFloor"/>, 1.0].
    /// </summary>
    public decimal ResolveCvarCompression(IReadOnlyList<decimal> periodicReturns, decimal baselineCvar = CvarBaseline)
    {
        if (_portfolioRisk is null || periodicReturns.Count < CvarMinSamples) return 1.0m;
        var cvar = _portfolioRisk.ComputeHistoricalCVaR(periodicReturns, confidence: CvarConfidenceLevel);
        if (cvar <= baselineCvar) return 1.0m;
        // Each 1× baseline of excess tail compresses size by CvarCompressionStep, floor at CvarCompressionFloor.
        var excess = (cvar - baselineCvar) / baselineCvar;
        var compression = Math.Max(CvarCompressionFloor, 1.0m - CvarCompressionStep * excess);
        return compression;
    }

    public Task ResetAsync(string symbol, CancellationToken ct = default)
    {
        _softPauseArmed = false;
        _logger.LogInformation("RiskAgent reset for {Symbol}. Soft pause disarmed.", symbol);
        return Task.CompletedTask;
    }

    public Task ProcessStepAsync(Candle candle, CancellationToken ct = default)
        => Task.CompletedTask;

    public void ResetSoftPause()
    {
        _softPauseArmed = false;
        _softPauseReason = "Soft pause disarmed.";
    }

    public void ResetKillSwitch() => ResetSoftPause();

    public void ArmSoftPause(string reason)
    {
        _softPauseArmed = true;
        _softPauseReason = string.IsNullOrWhiteSpace(reason) ? "Soft pause armed." : reason;
        _logger.LogWarning("RISK SOFT PAUSE ARMED (New Entries Halted): {Reason}", _softPauseReason);
    }

    public void ArmKillSwitch(string reason) => ArmSoftPause(reason);

    public Task Handle(VpinToxicityAlert notification, CancellationToken cancellationToken)
    {
        ArmSoftPause($"Toxic order flow (VPIN Z-Score: {notification.ZScore:F2}) on {notification.Symbol}. Pausing new entries.");
        return Task.CompletedTask;
    }

    public Task Handle(StaleDataAlert notification, CancellationToken cancellationToken)
    {
        ArmSoftPause($"Stale Data Guard tripped on {notification.Symbol}. Latency: {notification.LatencyMs}ms. Pausing new entries.");
        return Task.CompletedTask;
    }

    public RiskGovernorDashboardSnapshot GetDashboardSnapshot(
        IReadOnlyList<Trade>? recentTrades,
        Signal? signal,
        RiskParameters riskProfile,
        decimal requestedPositionFraction = 0m,
        decimal assumedStartingEquity = 0m)
    {
        var state = BuildPortfolioState(
            recentTrades,
            signal,
            requestedPositionFraction,
            riskProfile,
            assumedStartingEquity);

        return new RiskGovernorDashboardSnapshot(
            signal?.Symbol ?? string.Empty,
            _softPauseArmed,
            _softPauseReason,
            state.OpenTrades.Count,
            state.ClosedTrades.Count,
            state.OpenHeat,
            _options.MaxPortfolioHeat,
            state.SymbolHeat,
            _options.MaxSymbolExposure,
            state.CorrelatedHeat,
            _options.MaxCorrelatedExposure,
            state.DrawdownPercent,
            _options.SoftDrawdownPercent,
            _options.HardDrawdownPercent,
            state.DailyLossPercent,
            riskProfile.MaxDailyLossPercent,
            state.ExecutionHealth,
            _options.MinimumExecutionHealthToTrade,
            state.ExecutionMultiplier,
            state.PerformanceMultiplier,
            state.DrawdownMultiplier,
            state.RequestedPositionFraction);
    }

    public Task<(bool Approved, string Reason, decimal SafePositionSizePercent)> EvaluateAsync(
        TradeDecision candidate,
        RiskParameters riskProfile,
        decimal symbol24hVolume,
        IReadOnlyList<Trade>? recentTrades = null,
        Signal? signal = null)
    {
        if (_softPauseArmed) return Task.FromResult((false, $"Soft pause is armed: {_softPauseReason}", 0m));
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

        decimal confidenceKelly = CalculateConfidenceKelly(candidate, riskProfile, state.ClosedTrades, signal);
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
        if (_softPauseArmed) return Task.FromResult((false, $"Soft pause is armed: {_softPauseReason}"));

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

        var leverageCheck = CheckLeverageLimit(signal);
        if (!leverageCheck.Approved) return Task.FromResult(leverageCheck);

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

        var overflow = _observedTrades.Count - _options.MaxTrackedTrades;

        // Phase 1: evict closed trades first (oldest by exit/entry time).
        var closedIds = _observedTrades.Values
            .Where(t => t.IsClosed)
            .OrderBy(t => t.ExitTime ?? t.EntryTime)
            .Take(overflow)
            .Select(t => t.Id)
            .ToList();

        foreach (var tradeId in closedIds)
        {
            _observedTrades.TryRemove(tradeId, out _);
        }

        // Phase 2: if we're still over the cap (all trades are open — rare, but possible
        // under 24/7 operation with many symbols and no closures for extended periods),
        // age-out the oldest-opened trades too. Without this the dictionary grows
        // unbounded and eventually OOMs.
        var stillOver = _observedTrades.Count - _options.MaxTrackedTrades;
        if (stillOver <= 0) return;

        var oldestOpenIds = _observedTrades.Values
            .OrderBy(t => t.EntryTime)
            .Take(stillOver)
            .Select(t => t.Id)
            .ToList();

        foreach (var tradeId in oldestOpenIds)
        {
            _observedTrades.TryRemove(tradeId, out _);
        }

        if (oldestOpenIds.Count > 0)
        {
            _logger.LogWarning(
                "RiskAgent._observedTrades capacity guard aged out {Count} open trade records " +
                "(dictionary exceeded MaxTrackedTrades={Max} with no closed trades to evict).",
                oldestOpenIds.Count, _options.MaxTrackedTrades);
        }
    }

    private (bool Approved, string Reason) CheckDailyLossLimit(PortfolioRiskState state, RiskParameters riskProfile)
    {
        if (state.DailyLossPercent >= riskProfile.MaxDailyLossPercent)
        {
            ArmSoftPause($"Daily loss limit breached ({state.DailyLossPercent:F2}%).");
            return (false, "Daily loss limit breached.");
        }

        return (true, "");
    }

    private (bool Approved, string Reason) CheckDrawdownLimit(PortfolioRiskState state)
    {
        if (state.DrawdownPercent >= _options.HardDrawdownPercent)
        {
            ArmSoftPause($"Drawdown guard breached ({state.DrawdownPercent:F2}%).");
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

    private (bool Approved, string Reason) CheckLeverageLimit(Signal signal)
    {
        if (!signal.Metadata.TryGetValue("Leverage", out var raw) ||
            !decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var lev))
        {
            return (true, "");
        }

        var marginMode = MarginMode.Isolated;
        if (signal.Metadata.TryGetValue("MarginMode", out var marginRaw) &&
            Enum.TryParse<MarginMode>(marginRaw, true, out var parsedMode))
        {
            marginMode = parsedMode;
        }

        var modeMax = marginMode == MarginMode.Cross
            ? _options.MaxRequestedLeverageCross
            : _options.MaxRequestedLeverageIsolated;
        var absoluteMax = _options.MaxRequestedLeverage;
        var effectiveMax = Math.Min(Math.Max(1m, modeMax), Math.Max(1m, absoluteMax));
        if (lev > effectiveMax)
        {
            return (false, $"Requested leverage {lev:F2} exceeds {marginMode} max {effectiveMax:F2}.");
        }

        return (true, "");
    }

    private (bool Approved, string Reason) CheckLossStreak(IReadOnlyList<Trade> closedTrades, Signal signal)
    {
        var symbolRecentTrades = closedTrades
            .Where(t => string.Equals(t.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.ExitTime)
            .Take(Math.Max(_options.LossStreakLookbackTrades, _options.LossStreakThreshold))
            .ToList();

        if (symbolRecentTrades.Any(t => ResolveSingleTradeLossPercent(t) >= _options.SevereSingleLossPercent))
        {
            return (false, $"Loss-streak guard: severe single loss >= {_options.SevereSingleLossPercent:F1}% on {signal.Symbol}.");
        }

        var consecutiveLosses = symbolRecentTrades
            .TakeWhile(t => CalculateNetPnl(t, _options.CommissionPercentPerSide) < 0m)
            .Count();
        if (consecutiveLosses >= _options.LossStreakThreshold)
        {
            return (false, $"Loss-streak guard: {consecutiveLosses} consecutive losses on {signal.Symbol}.");
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
        if (baseEquity <= 0m) return 0m;
        return openTrades.Sum(t =>
        {
            var explicitHeat = Math.Max(0m, t.PositionSizePercent);
            if (explicitHeat > 0m)
            {
                return explicitHeat;
            }

            return Math.Max(0m, t.EntryPrice * t.Quantity) / baseEquity;
        });
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
            runningEquity += CalculateNetPnl(trade, _options.CommissionPercentPerSide);
            if (runningEquity > peakEquity) peakEquity = runningEquity;
            if (peakEquity <= 0m) continue;

            var drawdown = ((peakEquity - runningEquity) / peakEquity) * 100m;
            if (drawdown > maxDrawdown) maxDrawdown = drawdown;
        }

        return Math.Max(0m, maxDrawdown);
    }

    private decimal ResolveDailyLossPercent(decimal baseEquity, IReadOnlyList<Trade> closedTrades)
    {
        if (baseEquity <= 0m) return 0m;

        var dailyPnl = closedTrades
            .Where(t => t.ExitTime.HasValue && t.ExitTime.Value.Date == DateTime.UtcNow.Date)
            .Sum(trade => CalculateNetPnl(trade, _options.CommissionPercentPerSide));
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

        var wins = sample.Where(t => CalculateNetPnl(t, _options.CommissionPercentPerSide) > 0m).ToList();
        var losses = sample.Where(t => CalculateNetPnl(t, _options.CommissionPercentPerSide) < 0m).ToList();
        var grossProfit = wins.Sum(trade => CalculateNetPnl(trade, _options.CommissionPercentPerSide));
        var grossLoss = Math.Abs(losses.Sum(trade => CalculateNetPnl(trade, _options.CommissionPercentPerSide)));
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

    private decimal CalculateConfidenceKelly(
        TradeDecision candidate,
        RiskParameters riskProfile,
        IReadOnlyList<Trade> closedTrades,
        Signal? signal)
    {
        var p = Clamp(candidate.Confidence / 100m, 0m, 0.99m);
        var q = 1m - p;
        var b = ResolveKellyEdgeRatio(candidate, closedTrades, signal);
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

    private static decimal CalculateNetPnl(Trade trade, decimal commissionPercentPerSide)
    {
        var direction = trade.Side == TradeSide.Sell ? -1m : 1m;
        var grossPnl = (trade.ExitPrice - trade.EntryPrice) * trade.Quantity * direction;
        var slippageCost = trade.EntryPrice * trade.Quantity * (Math.Abs(trade.EntrySlippagePercent) / 100m);
        var feeRate = Math.Max(0m, commissionPercentPerSide) / 100m;
        var fees = ((trade.EntryPrice * trade.Quantity) + (trade.ExitPrice * trade.Quantity)) * feeRate;
        return grossPnl - slippageCost - fees;
    }

    private decimal ResolveKellyEdgeRatio(TradeDecision candidate, IReadOnlyList<Trade> closedTrades, Signal? signal)
    {
        var impliedDecisionRr = ResolveDecisionRiskReward(candidate, signal);
        var symbol = signal?.Symbol;
        var strategyTag = string.IsNullOrWhiteSpace(candidate.StrategyTag) ? null : candidate.StrategyTag;
        var sample = closedTrades
            .Where(t =>
                (string.IsNullOrWhiteSpace(symbol) || string.Equals(t.Symbol, symbol, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(strategyTag) || string.Equals(t.StrategyTag, strategyTag, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(t => t.ExitTime ?? t.EntryTime)
            .Take(Math.Max(5, _options.AdaptiveLookbackTrades))
            .ToList();

        if (sample.Count < 3)
        {
            return impliedDecisionRr;
        }

        var wins = sample
            .Select(t => CalculateNetPnl(t, _options.CommissionPercentPerSide))
            .Where(pnl => pnl > 0m)
            .ToList();
        var losses = sample
            .Select(t => CalculateNetPnl(t, _options.CommissionPercentPerSide))
            .Where(pnl => pnl < 0m)
            .Select(Math.Abs)
            .ToList();

        if (wins.Count == 0 || losses.Count == 0)
        {
            return impliedDecisionRr;
        }

        var historicalAverageRr = wins.Average() / Math.Max(0.0001m, losses.Average());
        return Clamp(historicalAverageRr, 0.5m, 5.0m);
    }

    private static decimal ResolveDecisionRiskReward(TradeDecision candidate, Signal? signal)
    {
        if (signal?.Price is not > 0m || candidate.StopLoss <= 0m || candidate.TakeProfit <= 0m)
        {
            return 1.5m;
        }

        var entry = signal.Price;
        var reward = Math.Abs(candidate.TakeProfit - entry);
        var risk = Math.Abs(entry - candidate.StopLoss);
        if (reward <= 0m || risk <= 0m)
        {
            return 1.5m;
        }

        return Clamp(reward / risk, 0.5m, 5.0m);
    }

    private static decimal ResolveSingleTradeLossPercent(Trade trade)
    {
        var notional = Math.Max(0m, trade.EntryPrice * trade.Quantity);
        if (notional <= 0m)
        {
            return 0m;
        }

        var direction = trade.Side == TradeSide.Sell ? -1m : 1m;
        var grossPnl = (trade.ExitPrice - trade.EntryPrice) * trade.Quantity * direction;
        return grossPnl < 0m ? Math.Abs(grossPnl) / notional * 100m : 0m;
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

public sealed record RiskGovernorDashboardSnapshot(
    string Symbol,
    bool IsSoftPauseArmed,
    string SoftPauseReason,
    int OpenTradeCount,
    int ClosedTradeCount,
    decimal OpenHeat,
    decimal MaxPortfolioHeat,
    decimal SymbolHeat,
    decimal MaxSymbolExposure,
    decimal CorrelatedHeat,
    decimal MaxCorrelatedExposure,
    decimal DrawdownPercent,
    decimal SoftDrawdownPercent,
    decimal HardDrawdownPercent,
    decimal DailyLossPercent,
    decimal MaxDailyLossPercent,
    decimal ExecutionHealth,
    decimal MinimumExecutionHealth,
    decimal ExecutionMultiplier,
    decimal PerformanceMultiplier,
    decimal DrawdownMultiplier,
    decimal RequestedPositionFraction)
{
    public bool IsKillSwitchArmed => IsSoftPauseArmed;
    public string KillSwitchReason => SoftPauseReason;
}

internal sealed class TokenBucketRateLimiter
{
    private const int TokenScale = 1_000_000;
    private readonly int _capacity;
    private readonly int _capacityScaled;
    private readonly double _refillPerMillisecondScaled;
    private long _state;

    public TokenBucketRateLimiter(int capacity, double refillRate)
    {
        _capacity = capacity;
        _capacityScaled = checked(capacity * TokenScale);
        _refillPerMillisecondScaled = refillRate * TokenScale / 1000.0;
        _state = PackState((uint)Environment.TickCount64, _capacityScaled);
    }

    public bool TryConsume()
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref _state);
            var (lastTick, tokensScaled) = UnpackState(snapshot);
            var nowTick = (uint)Environment.TickCount64;
            var elapsedMilliseconds = unchecked(nowTick - lastTick);
            var refilledTokens = tokensScaled;

            if (elapsedMilliseconds > 0)
            {
                var refillAmount = (int)Math.Min(
                    _capacityScaled - tokensScaled,
                    Math.Round(elapsedMilliseconds * _refillPerMillisecondScaled, MidpointRounding.AwayFromZero));

                if (refillAmount > 0)
                {
                    refilledTokens = Math.Min(_capacityScaled, tokensScaled + refillAmount);
                }
            }

            var canConsume = refilledTokens >= TokenScale;
            var nextTokens = canConsume ? refilledTokens - TokenScale : refilledTokens;
            var nextState = PackState(nowTick, nextTokens);

            if (Interlocked.CompareExchange(ref _state, nextState, snapshot) == snapshot)
            {
                return canConsume;
            }
        }
    }

    private static long PackState(uint tick, int tokensScaled)
        => ((long)tick << 32) | (uint)tokensScaled;

    private static (uint Tick, int TokensScaled) UnpackState(long state)
        => ((uint)(state >> 32), unchecked((int)(state & 0xFFFFFFFF)));
}
