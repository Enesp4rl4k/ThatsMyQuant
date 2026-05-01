using MediatR;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Notifications;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Strategy;

public class PaperTradingService
{
    private readonly IMediator? _mediator;
    private readonly ITechnicalAnalysisService _analysis;
    private readonly IMarketDataService _marketData;
    private readonly ILogger<PaperTradingService> _logger;
    private readonly StrategyPromotionService? _promotionService;
    private readonly ExitPolicyLearningService? _exitPolicyLearningService;
    private readonly MarketExecutionProfileService _executionProfiles;
    private readonly PositionSupervisorService _positionSupervisor;
    private readonly IExecutionCostSnapshotCache? _costSnapshotCache;
    private readonly IAgentCoordinator? _agentCoordinator;
    private readonly IEnumerable<ISignalValidator> _validators;
    private readonly string _timeframe;
    private readonly PaperLeverageOptions _leverageOptions;
    private readonly PaperPortfolioLimitsOptions _limitsOptions;
    private readonly BacktestCostModel _parityCostModel;
    private readonly PaperExecutionRealismOptions _realismOptions;
    private readonly Random _realismRng;
    private readonly PaperPortfolioPersistenceService? _persistence;
    private readonly MarketUniverseOptions _universeOptions;
    // Portfolio is shared across hosted services (directional + pairs + funding) so the unified
    // cash, open positions and PnL all live in one place. Injected via DI as Singleton — fall
    // back to a fresh instance only when used outside DI (legacy tests).
    public PaperPortfolio Portfolio { get; }

    public PaperTradingService(
        ITechnicalAnalysisService analysis,
        IMarketDataService marketData,
        ILogger<PaperTradingService> logger,
        StrategyPromotionService? promotionService = null,
        ExitPolicyLearningService? exitPolicyLearningService = null,
        MarketExecutionProfileService? executionProfiles = null,
        PositionSupervisorService? positionSupervisor = null,
        IExecutionCostSnapshotCache? costSnapshotCache = null,
        IAgentCoordinator? agentCoordinator = null,
        IEnumerable<ISignalValidator>? validators = null,
        string? timeframe = null,
        PaperLeverageOptions? leverageOptions = null,
        BacktestCostModel? parityCostModel = null,
        PaperExecutionRealismOptions? realismOptions = null,
        PaperPortfolioLimitsOptions? limitsOptions = null,
        IMediator? mediator = null,
        PaperPortfolioPersistenceService? persistence = null,
        MarketUniverseOptions? universeOptions = null,
        PaperPortfolio? portfolio = null)
    {
        Portfolio = portfolio ?? new PaperPortfolio();
        _mediator = mediator;
        _analysis = analysis;
        _marketData = marketData;
        _logger = logger;
        _promotionService = promotionService;
        _exitPolicyLearningService = exitPolicyLearningService;
        _executionProfiles = executionProfiles ?? new MarketExecutionProfileService();
        _positionSupervisor = positionSupervisor ?? new PositionSupervisorService();
        _costSnapshotCache = costSnapshotCache;
        _agentCoordinator = agentCoordinator;
        _validators = validators ?? Array.Empty<ISignalValidator>();
        _timeframe = string.IsNullOrWhiteSpace(timeframe) ? "15m" : timeframe!;
        _leverageOptions = leverageOptions ?? new PaperLeverageOptions();
        _limitsOptions = limitsOptions ?? new PaperPortfolioLimitsOptions();
        _parityCostModel = parityCostModel ?? new BacktestCostModel();
        _realismOptions = realismOptions ?? new PaperExecutionRealismOptions();
        _realismRng = _realismOptions.RandomSeed > 0 ? new Random(_realismOptions.RandomSeed) : new Random();
        _persistence = persistence;
        _universeOptions = universeOptions ?? new MarketUniverseOptions();
    }

    public async Task<string> ProcessSymbolAsync(string symbol)
    {
        await (_costSnapshotCache?.RefreshAsync(symbol) ?? Task.CompletedTask);

        string market = ResolveMarketType(symbol);

        if (!_universeOptions.IsEnabled(market))
        {
            return $"SKIP {symbol}: market {market} disabled via Markets:Enable{market}.";
        }

        var session = MarketHoursScheduler.GetCurrentSession(market);
        if (!session.CanTrade)
        {
            return $"{market} {session.Description}. Next: {session.NextSessionStart:HH:mm}";
        }

        Portfolio.ResetDailyPnLIfNeeded();
        var candles = await _marketData.GetCandlesAsync(symbol, 100, market, _timeframe);
        if (candles == null || candles.Count == 0)
        {
            return $"SKIP {symbol}: no market data returned.";
        }

        var lastCandle = candles.Last();
        var maxAge = GetMaxCandleAge(_timeframe);
        var candleAge = DateTime.UtcNow - lastCandle.Timestamp.ToUniversalTime();
        if (candleAge > maxAge)
        {
            _logger.LogWarning("Stale data for {Symbol}: last candle age {Age} exceeds {Max} ({Timeframe}).",
                symbol, candleAge, maxAge, _timeframe);
            return $"SKIP {symbol}: stale data (age {candleAge.TotalMinutes:F1}m).";
        }

        var currentPrice = lastCandle.Close;

        var position = Portfolio.OpenPositions.FirstOrDefault(p => p.Symbol == symbol);
        if (position != null)
        {
            return await HandleOpenPosition(position, candles);
        }

        if (!IsPreferredTradeWindow(market, candles.Last().Timestamp))
        {
            return $"Session filter: {market} is outside preferred trade window for new entries.";
        }

        var constraintMessage = CheckTradingConstraints();
        if (constraintMessage != null) return constraintMessage;

        var openSymbols = Portfolio.OpenPositions.Select(p => p.Symbol).ToList();
        // Crypto-only paper portfolio (TRX/XLM, LTC, DOGE, AAVE, SOL/AVAX, SOL/LINK) is by design
        // 100% crypto-sector. Real concentration enforcement lives in ConcentrationGuard (per-symbol
        // notional caps); the legacy 40% sector cap here was blocking every entry.
        var corrCheck = CorrelationService.CheckCorrelation(symbol, openSymbols, maxSectorExposurePercent: 100.0);
        if (corrCheck.ExceedsSectorLimit)
        {
            _logger.LogWarning("Correlation block: {Rec}", corrCheck.Recommendation);
            return corrCheck.Recommendation;
        }

        return await OpenNewPosition(symbol, market, currentPrice, candles);
    }

    private static string ResolveMarketType(string symbol)
    {
        return symbol.Contains("-", StringComparison.OrdinalIgnoreCase)
            ? "Crypto"
            : (symbol.Contains("=", StringComparison.OrdinalIgnoreCase) ? "Forex" : "BIST");
    }

    private async Task<string> HandleOpenPosition(PaperPosition position, IReadOnlyList<Candle> candles)
    {
        var currentPrice = candles.Last().Close;
        if (position.IsLeveragedPaperPosition && TryResolveLiquidation(position, candles.Last(), out var liquidationPrice))
        {
            return await ClosePosition(position, candles.Last(), "Liquidation", liquidationPrice, _leverageOptions.LiquidationFeeRate);
        }

        var previousCandle = candles.Count > 1 ? candles[^2] : candles.Last();
        var atr = (decimal)CalculateAtrProxy(candles, currentPrice);
        var lifecycleAction = _positionSupervisor.AdvancePaperPosition(position, candles.Last(), previousCandle, atr);

        if (lifecycleAction?.ActionType == PositionLifecycleActionType.PartialExit)
        {
            return await ApplyPartialExit(position, candles.Last(), lifecycleAction);
        }

        if (lifecycleAction?.ActionType == PositionLifecycleActionType.FullExit)
        {
            return await ClosePosition(position, candles.Last(), lifecycleAction.Reason);
        }

        return $"{position.Symbol} position open — UnrealizedPnL: ${position.UnrealizedPnL:N2}";
    }

    private double CalculateAtrProxy(IReadOnlyList<Candle> candles, decimal currentPrice)
    {
        return candles.Count > 14
            ? (double)(candles.TakeLast(15).Max(c => c.High) - candles.TakeLast(15).Min(c => c.Low)) / 14
            : (double)currentPrice * 0.02;
    }

    /// <summary>
    /// Emergency-flatten every open position. Called on circuit-breaker trip, kill
    /// switch press, or correlation panic. Uses the LAST cached candle per symbol
    /// as exit reference (no new market data fetched — must be fast). Returns a
    /// concise summary suitable for the alert payload.
    ///
    /// Added 2026-04-24 to close the "CB blocks entries but positions keep bleeding"
    /// gap identified in the pre-paper readiness audit.
    /// </summary>
    public async Task<string> FlattenAllPositionsAsync(string reason)
    {
        var snapshot = Portfolio.OpenPositions.ToList();
        if (snapshot.Count == 0) return $"FLATTEN [{reason}] — no open positions.";

        int closed = 0;
        decimal totalPnl = 0m;
        foreach (var pos in snapshot)
        {
            try
            {
                var refCandle = new Candle
                {
                    Timestamp = DateTime.UtcNow,
                    Open = pos.CurrentPrice,
                    High = pos.CurrentPrice,
                    Low = pos.CurrentPrice,
                    Close = pos.CurrentPrice,
                    Volume = 0m
                };
                await ClosePosition(pos, refCandle, $"FLATTEN:{reason}").ConfigureAwait(false);
                closed++;
                totalPnl += pos.RealizedPnL;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FlattenAll: failed to close {Symbol}", pos.Symbol);
            }
        }
        _logger.LogError("FLATTEN ALL [{Reason}] — closed {N} positions, net PnL {PnL:N2}", reason, closed, totalPnl);
        return $"FLATTEN [{reason}] — closed {closed} positions, net PnL {totalPnl:N2}";
    }

    /// <summary>
    /// Closes a single open position by symbol (and optionally side, when there are multiple
    /// legs). Triggered by the operator from the Cockpit UI's "Close" button. Uses the
    /// position's last cached price as the exit reference — same behaviour as the existing
    /// FlattenAll path, just scoped to one symbol.
    ///
    /// Returns null when no matching position is found; otherwise the closed-position summary.
    /// </summary>
    public async Task<string?> CloseSinglePositionAsync(string symbol, TradeSide? side, string reason)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;

        // Snapshot under the same conventions used in FlattenAllPositionsAsync — caller is the
        // request-handling thread; PaperPortfolio mutations elsewhere are guarded by lock(_portfolio).
        var snapshot = Portfolio.OpenPositions
            .Where(p => string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            .Where(p => side is null || p.Side == side.Value)
            .ToList();
        if (snapshot.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        decimal totalPnl = 0m;
        int closed = 0;
        foreach (var pos in snapshot)
        {
            try
            {
                var refCandle = new Candle
                {
                    Timestamp = DateTime.UtcNow,
                    Open  = pos.CurrentPrice,
                    High  = pos.CurrentPrice,
                    Low   = pos.CurrentPrice,
                    Close = pos.CurrentPrice,
                    Volume = 0m
                };
                var msg = await ClosePosition(pos, refCandle, $"MANUAL:{reason}").ConfigureAwait(false);
                sb.AppendLine(msg);
                closed++;
                totalPnl += pos.RealizedPnL;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CloseSingle: failed to close {Symbol}", pos.Symbol);
            }
        }
        _logger.LogWarning("MANUAL CLOSE {Symbol} [{Reason}] — closed {N}, net PnL {PnL:N2}", symbol, reason, closed, totalPnl);
        return sb.ToString();
    }

    private async Task<string> ClosePosition(
        PaperPosition position,
        Candle lastCandle,
        string exitReason,
        decimal? forcedExitPrice = null,
        decimal extraFeeRate = 0m)
    {
        var exitQuantity = Math.Max(0m, position.Quantity);
        if (exitQuantity <= 0m)
        {
            position.Quantity = 0m;
            position.RemainingQuantity = 0m;
            position.ExitTime = DateTime.UtcNow;
            position.ExitReason = exitReason;
            Portfolio.ClosedPositions.Add(position);
            Portfolio.OpenPositions.Remove(position);
            return $"CLOSED {position.Symbol} via {exitReason} — PnL: ${position.RealizedPnL:N2}";
        }

        var refPrice = forcedExitPrice ?? lastCandle.Close;
        var adjustedExit = _executionProfiles.ApplyExecutionPrice(
            refPrice,
            position.Side,
            false,
            position.Symbol,
            null,
            ResolveParitySlippagePercent(),
            0m,
            lastCandle);

        var exitPnl = _executionProfiles.CalculateNetPnl(
            position.EntryPrice,
            adjustedExit,
            exitQuantity,
            position.Side,
            position.Symbol,
            null,
            ResolveParityCommissionPercentPerSide());
        var extraFees = (extraFeeRate > 0m && exitQuantity > 0m)
            ? adjustedExit * exitQuantity * extraFeeRate
            : 0m;
        var fundingNotional = exitQuantity * ((position.EntryPrice + adjustedExit) / 2m);
        var funding = FundingAccrualService.Calculate(
            fundingNotional,
            position.Side,
            position.EntryTime,
            DateTime.UtcNow,
            ResolveParityFundingBpsPer8h());
        exitPnl = exitPnl - extraFees + funding;

        position.ExitTime = DateTime.UtcNow;
        position.ExitReason = exitReason;
        position.NetFunding += funding;
        position.RealizedPnL += exitPnl;
        position.Quantity = 0m;
        position.RemainingQuantity = 0m;

        if (position.IsLeveragedPaperPosition)
        {
            Portfolio.Cash += position.RemainingMargin + exitPnl;
            position.RemainingMargin = 0m;
        }
        else
        {
            // Spot-style cashflow model.
            var closeNotional = adjustedExit * exitQuantity;
            if (position.Side == TradeSide.Buy)
                Portfolio.Cash += closeNotional;
            else
                Portfolio.Cash -= closeNotional;
        }
        Portfolio.DailyPnL += exitPnl;
        Portfolio.ClosedPositions.Add(position);
        Portfolio.OpenPositions.Remove(position);
        Portfolio.AppendEquity(Portfolio.TotalEquity);

        await (_promotionService?.RecordPaperState(Portfolio.ClosedPositions) ?? Task.CompletedTask);
        await (_exitPolicyLearningService?.RecordPaperState(Portfolio.ClosedPositions) ?? Task.CompletedTask);

        // Broadcast close event so ChampionChallenger / analytics subscribers can attribute.
        if (_mediator is not null)
        {
            try
            {
                var pnlPct = position.EntryPrice > 0 && exitQuantity > 0
                    ? (exitPnl / (position.EntryPrice * exitQuantity)) * 100m
                    : 0m;
                await _mediator.Publish(new PositionClosedNotification(
                    Symbol: position.Symbol,
                    StrategyTag: position.StrategyTag ?? "Unknown",
                    RegimeTag: position.RegimeTag ?? "Unknown",
                    ExitPolicyTag: position.ExitPolicyTag ?? "Unknown",
                    ExitReason: exitReason,
                    ExecutionMode: "PAPER",
                    Side: position.Side,
                    EntryPrice: position.EntryPrice,
                    ExitPrice: adjustedExit,
                    Quantity: exitQuantity,
                    NetPnl: exitPnl,
                    NetPnlPercent: pnlPct,
                    EntryTimeUtc: position.EntryTime,
                    ExitTimeUtc: position.ExitTime ?? DateTime.UtcNow,
                    CorrelationId: null));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PositionClosedNotification publish failed — continuing");
            }
        }

        _logger.LogInformation("PAPER {Exit}: {Symbol} PnL=${PnL:N2} ({Reason})",
            exitReason, position.Symbol, position.RealizedPnL, exitReason);

        _persistence?.RequestWrite(Portfolio);
        return $"CLOSED {position.Symbol} via {exitReason} — PnL: ${position.RealizedPnL:N2}";
    }

    private async Task<string> ApplyPartialExit(PaperPosition position, Candle lastCandle, PositionLifecycleAction action)
    {
        var exitQuantity = Math.Min(Math.Max(0m, position.Quantity), action.Quantity);
        if (exitQuantity <= 0m)
        {
            return $"{position.Symbol} position open — UnrealizedPnL: ${position.UnrealizedPnL:N2}";
        }

        var adjustedExit = _executionProfiles.ApplyExecutionPrice(
            lastCandle.Close,
            position.Side,
            false,
            position.Symbol,
            null,
            ResolveParitySlippagePercent(),
            0m,
            lastCandle);

        var partialPnl = _executionProfiles.CalculateNetPnl(
            position.EntryPrice,
            adjustedExit,
            exitQuantity,
            position.Side,
            position.Symbol,
            null,
            ResolveParityCommissionPercentPerSide());
        var fundingNotional = exitQuantity * ((position.EntryPrice + adjustedExit) / 2m);
        var funding = FundingAccrualService.Calculate(
            fundingNotional,
            position.Side,
            position.EntryTime,
            DateTime.UtcNow,
            ResolveParityFundingBpsPer8h());
        partialPnl += funding;

        position.HasTakenPartialProfit = true;
        position.NetFunding += funding;
        position.RealizedPnL += partialPnl;
        position.Quantity = Math.Max(0m, position.Quantity - exitQuantity);
        position.RemainingQuantity = position.Quantity;
        position.CurrentPrice = lastCandle.Close;
        position.ExitReason = action.Reason;

        if (position.IsLeveragedPaperPosition)
        {
            var releaseRatio = position.OpenedQuantity > 0m ? (exitQuantity / position.OpenedQuantity) : 0m;
            var marginRelease = Math.Min(position.RemainingMargin, position.InitialMargin * releaseRatio);
            position.RemainingMargin -= marginRelease;
            Portfolio.Cash += marginRelease + partialPnl;
        }
        else
        {
            var closeNotional = adjustedExit * exitQuantity;
            if (position.Side == TradeSide.Buy)
                Portfolio.Cash += closeNotional;
            else
                Portfolio.Cash -= closeNotional;
        }
        Portfolio.DailyPnL += partialPnl;
        Portfolio.AppendEquity(Portfolio.TotalEquity);

        _logger.LogInformation("PAPER PARTIAL EXIT: {Symbol} Qty={Qty} PnL=${PnL:N2} ({Reason})",
            position.Symbol, exitQuantity, partialPnl, action.Reason);

        if (position.Quantity <= 0m)
        {
            return await ClosePosition(position, lastCandle, action.Reason);
        }

        _persistence?.RequestWrite(Portfolio);
        return $"PARTIAL {position.Symbol} via {action.Reason} — booked ${partialPnl:N2}, remaining {position.Quantity:N4}";
    }

    private string? CheckTradingConstraints()
    {
        var dailyLossLimit = Portfolio.InitialBalance * _limitsOptions.DailyLossLimitRatio;
        if (Portfolio.DailyPnL < -dailyLossLimit)
        {
            return $"⚠️ Daily loss limit hit (${Portfolio.DailyPnL:N2}). No new trades today.";
        }

        if (Portfolio.OpenPositions.Count >= _limitsOptions.MaxConcurrentPositions)
        {
            return $"Max concurrent positions reached ({Portfolio.OpenPositions.Count}/{_limitsOptions.MaxConcurrentPositions}). Close one before opening another.";
        }

        var totalNotional = Portfolio.OpenPositions.Sum(p => p.Quantity * p.CurrentPrice);
        var equity = Portfolio.TotalEquity;
        if (equity > 0m && totalNotional / equity >= _limitsOptions.MaxTotalNotionalRatio)
        {
            return $"Total open notional {totalNotional:N0} exceeds {_limitsOptions.MaxTotalNotionalRatio:P0} of equity.";
        }

        var consecutiveLosses = Portfolio.ClosedPositions
            .OrderByDescending(p => p.ExitTime)
            .Take(Math.Max(1, _limitsOptions.ConsecutiveLossLookback))
            .TakeWhile(p => p.RealizedPnL < 0)
            .Count();

        if (consecutiveLosses >= _limitsOptions.ConsecutiveLossCooldownThreshold)
        {
            return $"Cooldown active after {consecutiveLosses} consecutive paper losses. Wait for a cleaner window.";
        }

        return null;
    }

    private async Task<string> OpenNewPosition(string symbol, string market, decimal currentPrice, IReadOnlyList<Candle> candles)
    {
        var signal = new Signal
        {
            Symbol = symbol,
            Price = currentPrice,
            MarketType = market switch
            {
                "Crypto" => MarketType.Crypto,
                "Forex" => MarketType.Forex,
                _ => MarketType.BIST
            },
            // Paper mode proxy so MinVolumeValidator works when exchange 24h isn't injected.
            Volume24h = candles.Sum(c => c.Volume * Math.Max(0m, c.Close))
        };

        foreach (var validator in _validators.OrderBy(v => v.Order))
        {
            var (valid, reason) = await validator.ValidateAsync(signal);
            if (!valid)
            {
                return $"SKIP {symbol}: validator rejected — {reason}";
            }
        }

        var risk = RiskParameters.Default;
        var decision = await _analysis.AnalyzeAsync(signal, candles.ToList(), risk);

        if (decision is null || !decision.ShouldTrade)
            return $"SKIP {symbol}: {decision?.Reasoning ?? "no decision"}";

        double consensusConfidence = decision.Confidence;
        if (_agentCoordinator != null)
        {
            var regime = MarketRegimeDetector.Detect(candles).Regime;
            var recentHistory = BuildTradeHistory(Portfolio.ClosedPositions, take: 50);

            var snapshot = await _agentCoordinator.RunFullPipelineAsync(
                signal,
                Array.Empty<MarketTick>(),
                candles,
                regime,
                Array.Empty<Trade>(),
                recentHistory) ?? new FiveTierSnapshot();

            float agentAdjustment = (snapshot.Votes ?? Array.Empty<StrategyVote>())
                .Where(v => v.Vote != VoteOutcome.Abstain)
                .Sum(v => v.ConfidenceAdjustment);

            consensusConfidence = (consensusConfidence + agentAdjustment) * (snapshot.Meta?.ConfidenceScalar ?? 1f);

            if (snapshot.RiskGauntlet != null && !snapshot.RiskGauntlet.Approved)
            {
                return $"SKIP {symbol}: risk gauntlet blocked ({snapshot.RiskGauntlet.Reason})";
            }

            if (consensusConfidence < 55)
            {
                return $"SKIP {symbol}: consensus confidence {consensusConfidence:F0} below threshold.";
            }
        }

        decimal adaptiveSize = AdaptiveSizingService.Apply(decision.PositionSizePercent, (int)Math.Clamp(consensusConfidence, 10, 99), market, DateTime.UtcNow);
        decimal positionValue = Portfolio.Cash * adaptiveSize;
        bool isCrypto = string.Equals(market, "Crypto", StringComparison.OrdinalIgnoreCase);
        decimal quantity = currentPrice > 0
            ? (isCrypto
                ? Math.Round(positionValue / currentPrice, 6, MidpointRounding.ToZero)
                : Math.Floor(positionValue / currentPrice))
            : 0m;

        if (quantity <= 0) return $"SKIP {symbol}: insufficient cash for position";

        // Paper realism: stochastic rejection + latency. Off by default to preserve backtest parity.
        if (_realismOptions.Enabled)
        {
            var currentRegime = MarketRegimeDetector.Detect(candles).Regime;
            var rejectProb = currentRegime == MarketRegime.Volatile
                ? _realismOptions.VolatileRejectionProbability
                : _realismOptions.BaseRejectionProbability;
            if (rejectProb > 0 && _realismRng.NextDouble() < rejectProb)
            {
                _logger.LogInformation("PAPER REJECTED {Symbol} (regime={Regime}, p={P:P2}) — simulated venue reject.", symbol, currentRegime, rejectProb);
                return $"REJECTED {symbol}: venue reject (regime={currentRegime}, p={rejectProb:P1})";
            }
        }

        var leverage = ResolveLeverage(signal);
        var marginMode = ResolveMarginMode(signal);
        var isLeveragedPaperPosition = _leverageOptions.Enabled && leverage > 1m;
        var entryPrice = _executionProfiles.ApplyExecutionPrice(
            currentPrice,
            decision.Side,
            true,
            symbol,
            null,
            ResolveParitySlippagePercent(),
            0m,
            candles.Last());

        var notional = quantity * entryPrice;
        var initialMargin = isLeveragedPaperPosition ? notional / leverage : 0m;
        if (isLeveragedPaperPosition && initialMargin > Portfolio.Cash)
            return $"SKIP {symbol}: insufficient margin cash for x{leverage:F2} leverage.";

        var newPosition = new PaperPosition
        {
            Symbol = symbol,
            Side = decision.Side,
            EntryPrice = entryPrice,
            CurrentPrice = currentPrice,
            Quantity = quantity,
            OpenedQuantity = quantity,
            StopLoss = decision.StopLoss,
            TakeProfit = decision.TakeProfit,
            PartialTakeProfit = decision.PartialTakeProfit,
            PartialExitFraction = decision.PartialExitFraction <= 0m ? 0.50m : decision.PartialExitFraction,
            RemainingQuantity = quantity,
            ExitPolicyTag = decision.ExitPolicyTag,
            BreakevenTriggerFraction = decision.BreakevenTriggerFraction,
            MaxHoldingBars = decision.MaxHoldingBars,
            ExecutionMode = "PAPER",
            PromotionStage = "Paper",
            OptimalPriceSinceEntry = currentPrice,
            EntryTime = DateTime.UtcNow,
            Leverage = leverage,
            MarginMode = marginMode,
            InitialMargin = initialMargin,
            RemainingMargin = initialMargin,
            MaintenanceMarginRate = _leverageOptions.MaintenanceMarginRate,
            LiquidationPrice = ResolveLiquidationPrice(entryPrice, decision.Side, leverage, _leverageOptions.MaintenanceMarginRate),
            IsLeveragedPaperPosition = isLeveragedPaperPosition
        };

        if (isLeveragedPaperPosition)
        {
            Portfolio.Cash -= initialMargin;
        }
        else
        {
            var openNotional = quantity * newPosition.EntryPrice;
            if (decision.Side == TradeSide.Buy)
                Portfolio.Cash -= openNotional;
            else
                Portfolio.Cash += openNotional;
        }
        Portfolio.OpenPositions.Add(newPosition);
        Portfolio.AppendEquity(Portfolio.TotalEquity);

        _logger.LogInformation("PAPER TRADE: {Side} {Qty} {Symbol} @ ${Price:F2} | SL=${SL:F2} TP=${TP:F2}",
            decision.Side, quantity, symbol, currentPrice, decision.StopLoss, decision.TakeProfit);

        // Surface the open as a PositionOpenedNotification so Telegram push and the
        // Dashboard live-activity feed can render the bot's reasoning + risk plan.
        try
        {
            await _mediator.Publish(new ThatsMyQuant.Contracts.Notifications.PositionOpenedNotification(
                Symbol: symbol,
                StrategyTag: decision.StrategyTag ?? "",
                RegimeTag: decision.RegimeTag ?? "",
                ExitPolicyTag: decision.ExitPolicyTag ?? "",
                ExecutionMode: "PAPER",
                Side: decision.Side,
                EntryPrice: entryPrice,
                Quantity: quantity,
                StopLoss: decision.StopLoss,
                TakeProfit: decision.TakeProfit,
                EdgeScore: 0m,
                Confidence: (decimal)consensusConfidence,
                Reasoning: decision.Reasoning ?? "",
                EntryTimeUtc: newPosition.EntryTime)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PositionOpenedNotification publish failed — continuing");
        }

        var leverageLabel = isLeveragedPaperPosition ? $" x{leverage:F2}" : string.Empty;
        _persistence?.RequestWrite(Portfolio);
        return $"OPENED {decision.Side} {quantity} {symbol}{leverageLabel} @ ${currentPrice:F2} (Score: {Math.Clamp((int)consensusConfidence, 0, 100)}/100)";
    }

    private decimal ResolveLeverage(Signal signal)
    {
        var requested = signal.Metadata.TryGetValue("Leverage", out var raw) &&
                        decimal.TryParse(raw, out var parsed) &&
                        parsed > 0m
            ? parsed
            : _leverageOptions.DefaultLeverage;
        return Math.Clamp(requested, 1m, Math.Max(1m, _leverageOptions.MaxLeverage));
    }

    private MarginMode ResolveMarginMode(Signal signal)
    {
        if (signal.Metadata.TryGetValue("MarginMode", out var raw) &&
            Enum.TryParse<MarginMode>(raw, true, out var parsed))
        {
            return parsed;
        }

        return _leverageOptions.DefaultMarginMode;
    }

    private static decimal ResolveLiquidationPrice(decimal entryPrice, TradeSide side, decimal leverage, decimal mmr)
    {
        if (entryPrice <= 0m || leverage <= 1m)
            return 0m;
        var buffer = (1m / leverage) - mmr;
        if (buffer <= 0m)
            buffer = 0.001m;
        return side == TradeSide.Buy
            ? entryPrice * (1m - buffer)
            : entryPrice * (1m + buffer);
    }

    private static bool TryResolveLiquidation(PaperPosition position, Candle candle, out decimal liquidationPrice)
    {
        liquidationPrice = position.LiquidationPrice;
        if (liquidationPrice <= 0m)
            return false;
        return position.Side == TradeSide.Buy
            ? candle.Low <= liquidationPrice
            : candle.High >= liquidationPrice;
    }

    private static TimeSpan GetMaxCandleAge(string timeframe) => timeframe.ToLowerInvariant() switch
    {
        "1m" => TimeSpan.FromMinutes(5),
        "5m" => TimeSpan.FromMinutes(15),
        "15m" => TimeSpan.FromMinutes(45),
        "1h" => TimeSpan.FromHours(3),
        "4h" => TimeSpan.FromHours(12),
        "1d" => TimeSpan.FromDays(2),
        _ => TimeSpan.FromHours(2)
    };

    private static IReadOnlyList<Trade> BuildTradeHistory(IReadOnlyList<PaperPosition> closed, int take)
    {
        if (closed.Count == 0) return Array.Empty<Trade>();
        return closed
            .OrderByDescending(p => p.ExitTime ?? DateTime.UtcNow)
            .Take(take)
            .Select(p => new Trade
            {
                Symbol = p.Symbol,
                Side = p.Side,
                EntryPrice = p.EntryPrice,
                ExitPrice = p.CurrentPrice,
                Quantity = p.Quantity,
                EntryTime = p.EntryTime,
                ExitTime = p.ExitTime ?? DateTime.UtcNow,
                StrategyTag = p.StrategyTag,
                RegimeTag = p.RegimeTag,
                ExitPolicyTag = p.ExitPolicyTag,
                Leverage = p.Leverage <= 0m ? 1m : p.Leverage,
                MarginMode = p.MarginMode,
                NetFunding = p.NetFunding,
                Status = TradeStatus.Executed
            })
            .ToList();
    }

    private static bool IsPreferredTradeWindow(string market, DateTime timestamp)
    {
        if (string.Equals(market, "BIST", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Crypto is a 24/7 market — do not hard-block outside Turkey-time killzones.
        // Conviction tuning happens downstream via the QuantIndicatorService session gate
        // (score/size multipliers), not here.
        if (string.Equals(market, "Crypto", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var turkeyTime = timestamp.Kind == DateTimeKind.Utc
            ? TimeZoneInfo.ConvertTimeBySystemTimeZoneId(timestamp, "Turkey Standard Time")
            : TimeZoneInfo.ConvertTimeBySystemTimeZoneId(timestamp.ToUniversalTime(), "Turkey Standard Time");

        return turkeyTime.Hour is >= 10 and < 13 or >= 15 and < 18;
    }

    private decimal ResolveParityCommissionPercentPerSide() => _parityCostModel.CommissionBps / 100m;

    private decimal ResolveParitySlippagePercent() => _parityCostModel.SlippageBps / 100m;

    private decimal ResolveParityFundingBpsPer8h()
        => _leverageOptions.FundingRateBpsPer8h != 0m
            ? _leverageOptions.FundingRateBpsPer8h
            : _parityCostModel.FundingBpsPer8h;
}
