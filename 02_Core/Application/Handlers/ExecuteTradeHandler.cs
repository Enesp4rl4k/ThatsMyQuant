using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Application.Observability;
using ThatsMyQuant.Application.Resilience;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Application.Strategy;
using System.Globalization;
using ThatsMyQuant.Contracts.Exceptions;
using ThatsMyQuant.Contracts.Helpers;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Handlers;

public class ExecuteTradeHandler : IRequestHandler<ExecuteTradeCommand, Trade>
{
    private static readonly HashSet<string> PromotedStages = new(StringComparer.OrdinalIgnoreCase)
    {
        "LiveProbe",
        "ScaledLive"
    };

    private readonly ILogger<ExecuteTradeHandler> _logger;
    private readonly ITradeRepository _tradeRepository;
    private readonly CircuitBreakerService _circuitBreaker;
    private readonly PositionSupervisorService _positionSupervisor;
    private readonly AdaptiveExecutionOrchestrator _adaptiveExecutionOrchestrator;
    private readonly OperationalTelemetry _telemetry;
    private readonly IStrategyPerformanceStore? _performanceStore;
    private readonly bool _requirePromotion;
    private readonly IOptions<OrderBookExecutionOptions> _orderBookExecution;
    private readonly decimal _defaultLeverage;
    private readonly MarginMode _defaultMarginMode;
    private readonly ThatsMyQuant.Application.Analytics.ExecutionParityService? _parityService;
    private readonly ThatsMyQuant.Application.Strategy.ProMode.IOperatorAlertService? _operatorAlerts;
    private readonly SlippageAlertOptions _slippageAlert;
    private readonly OrderExecutionOptions _orderExecution;

    // Per-symbol cooldown so a wide-spread venue does not spam Telegram.
    // Static because MediatR handlers are typically Transient and the map must
    // survive across invocations.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _slippageAlertCooldown =
        new(StringComparer.OrdinalIgnoreCase);

    public ExecuteTradeHandler(
        ILogger<ExecuteTradeHandler> logger,
        ITradeRepository tradeRepository,
        CircuitBreakerService circuitBreaker,
        PositionSupervisorService positionSupervisor,
        AdaptiveExecutionOrchestrator adaptiveExecutionOrchestrator,
        IOptions<OrderBookExecutionOptions> orderBookExecution,
        OperationalTelemetry? telemetry = null,
        IStrategyPerformanceStore? performanceStore = null,
        IConfiguration? configuration = null,
        ThatsMyQuant.Application.Analytics.ExecutionParityService? parityService = null,
        ThatsMyQuant.Application.Strategy.ProMode.IOperatorAlertService? operatorAlerts = null,
        IOptions<SlippageAlertOptions>? slippageAlertOptions = null,
        IOptions<OrderExecutionOptions>? orderExecutionOptions = null)
    {
        _parityService = parityService;
        _logger = logger;
        _tradeRepository = tradeRepository;
        _circuitBreaker = circuitBreaker;
        _positionSupervisor = positionSupervisor;
        _adaptiveExecutionOrchestrator = adaptiveExecutionOrchestrator;
        _orderBookExecution = orderBookExecution;
        _telemetry = telemetry ?? new OperationalTelemetry();
        _performanceStore = performanceStore;
        _operatorAlerts = operatorAlerts;
        _slippageAlert = slippageAlertOptions?.Value ?? new SlippageAlertOptions();
        _orderExecution = orderExecutionOptions?.Value ?? new OrderExecutionOptions();
        _orderExecution.Normalize();

        var flag = configuration?["TMQ_REQUIRE_PROMOTION"] ?? configuration?["Promotion:Required"];
        _requirePromotion = bool.TryParse(flag, out var required) && required;
        _defaultLeverage = decimal.TryParse(
                configuration?["Execution:Leverage:DefaultLeverage"],
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var configuredLeverage)
            ? Math.Max(1m, configuredLeverage)
            : 1m;
        _defaultMarginMode = Enum.TryParse<MarginMode>(configuration?["Execution:Leverage:DefaultMarginMode"], true, out var configuredMode)
            ? configuredMode
            : MarginMode.Isolated;
    }

    public async Task<Trade> Handle(ExecuteTradeCommand request, CancellationToken cancellationToken)
    {
        using var logScope = TradingLogScope.Begin(_logger, request.Signal);
        _logger.LogInformation("Executing Trade for Signal: {Id}", request.Signal.Id);

        // Persistent idempotency guard: never execute the same signal twice.
        var existingTrade = await _tradeRepository.GetBySignalIdAsync(request.Signal.Id);
        if (existingTrade != null)
        {
            _logger.LogWarning(
                "Duplicate execution request blocked for Signal {SignalId}. Returning Trade {TradeId}.",
                request.Signal.Id,
                existingTrade.Id);
            return existingTrade;
        }

        var executionPlan = await _adaptiveExecutionOrchestrator.CreatePlanAsync(request.Signal, cancellationToken);
        var executionSignal = executionPlan.Signal;
        EnsureLeverageMetadata(executionSignal);
        ExecutionOrderIdentity.EnsureClientOrderId(executionSignal);
        var trackedTrade = _positionSupervisor.CreateSubmittedTrade(executionSignal);
        MapSignalMetadataToTrade(executionSignal, trackedTrade);

        var circuitGuard = _circuitBreaker.CheckOrderAllowed();
        if (!circuitGuard.Allowed)
        {
            _logger.LogWarning(
                "Circuit breaker blocked execution for Signal {SignalId}: {Reason}",
                request.Signal.Id,
                circuitGuard.BlockReason);

            _positionSupervisor.MarkFailed(trackedTrade, circuitGuard.BlockReason ?? "Circuit breaker blocked execution.");
            if (await TryReturnExistingTradeOnDuplicateAsync(trackedTrade, request.Signal.Id) is { } duplicateTrade)
                return duplicateTrade;
            _telemetry.RecordTradeFailed(request.Signal.Symbol);
            return trackedTrade;
        }

        if (_requirePromotion)
        {
            var promotionBlock = await CheckPromotionGateAsync(executionSignal);
            if (promotionBlock != null)
            {
                _logger.LogWarning(
                    "Promotion gate blocked execution for Signal {SignalId}: {Reason}",
                    request.Signal.Id,
                    promotionBlock);

                _positionSupervisor.MarkFailed(trackedTrade, promotionBlock);
                if (await TryReturnExistingTradeOnDuplicateAsync(trackedTrade, request.Signal.Id) is { } duplicateTrade)
                    return duplicateTrade;
                _telemetry.RecordTradeFailed(request.Signal.Symbol);
                return trackedTrade;
            }
        }

        var isHftMode = executionSignal.Metadata.TryGetValue("ExecutionMode", out var mode) &&
                        string.Equals(mode, "HFT", StringComparison.OrdinalIgnoreCase);

        // Domain Logic: Reject weak liquidity HFT probes before hitting the venue
        if (isHftMode)
        {
            if (executionSignal.Volume24h < 500_000m)
            {
                _logger.LogWarning("Rejecting HFT Signal {Id} due to insufficient 24h volume ({Volume})", request.Signal.Id, executionSignal.Volume24h);
                _positionSupervisor.MarkFailed(trackedTrade, "Insufficient 24h volume for HFT execution.");
                if (await TryReturnExistingTradeOnDuplicateAsync(trackedTrade, request.Signal.Id) is { } duplicateTrade)
                    return duplicateTrade;
                _telemetry.RecordTradeFailed(request.Signal.Symbol);
                return trackedTrade;
            }
        }

        OrderBookSnapshot? liveBook = null;
        if (executionPlan.ExchangeService.TryGetLatestOrderBook(executionSignal.Symbol, out var ob))
            liveBook = ob;

        // [P1.4] Maker rebate routing — convert opening Market signals to maker-Limit
        // orders at top-of-book inside price. The OkxExecutionService already handles
        // post_only submission and the timeout-then-market fallback when these options
        // are set; this block decides ONLY whether to rewrite the signal.
        TryRouteAsMakerLimit(executionSignal, liveBook, isHftMode);

        if (!OrderBookExecutionAdvisor.TryApply(
                executionSignal,
                liveBook,
                _orderBookExecution.Value,
                isHftMode,
                _logger,
                out var orderBookBlock))
        {
            _positionSupervisor.MarkFailed(trackedTrade, orderBookBlock ?? "Order book gate rejected execution.");
            if (await TryReturnExistingTradeOnDuplicateAsync(trackedTrade, request.Signal.Id) is { } duplicateTrade)
                return duplicateTrade;
            _telemetry.RecordTradeFailed(request.Signal.Symbol);
            return trackedTrade;
        }

        if (await TryReturnExistingTradeOnDuplicateAsync(trackedTrade, request.Signal.Id) is { } claimedTrade)
            return claimedTrade;
        _positionSupervisor.MarkAccepted(trackedTrade);
        await _tradeRepository.UpdateAsync(trackedTrade);

        // Execute with Polly full pipeline (Retry + Circuit Breaker)
        var pipeline = ResiliencePolicies.CreateFullPipeline(_logger);
        try
        {
            var venueTrade = await pipeline.ExecuteAsync(async ct =>
                await executionPlan.ExchangeService.ExecuteOrderAsync(executionSignal), cancellationToken);

            _positionSupervisor.ApplyExecutionFill(trackedTrade, venueTrade);
            MapSignalMetadataToTrade(executionSignal, trackedTrade);
            if (executionSignal.Price > 0m && trackedTrade.EntryPrice > 0m)
            {
                _telemetry.RecordEntrySlippagePercent(trackedTrade.EntrySlippagePercent, executionSignal.Symbol);
                EvaluateSlippageBreach(executionSignal.Symbol, trackedTrade.EntrySlippagePercent);
            }

            _circuitBreaker.RecordSuccess();
            _telemetry.RecordTradeSucceeded(
                trackedTrade.ExecutionLatencyMs > 0 ? trackedTrade.ExecutionLatencyMs : venueTrade.ExecutionLatencyMs,
                executionSignal.Symbol);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trade execution FAILED for Signal {Id} after retries + circuit breaker.",
                request.Signal.Id);
            _circuitBreaker.RecordError(ex);
            _positionSupervisor.MarkFailed(trackedTrade, ex.Message);
            MapSignalMetadataToTrade(executionSignal, trackedTrade);
            _telemetry.RecordTradeFailed(executionSignal.Symbol);
        }

        await _tradeRepository.UpdateAsync(trackedTrade);

        // Parity tracking: expected = signal price, actual = realised entry fill.
        // The service classifies divergence (price/size/both) for TCA + live health.
        if (_parityService is not null && trackedTrade.EntryPrice > 0 && request.Signal.Price > 0)
        {
            var correlationId = request.Signal.Id.ToString();
            var nowUtc = DateTime.UtcNow;
            _parityService.RecordPaperFill(correlationId, request.Signal.Price, request.Signal.Quantity, nowUtc);
            _parityService.RecordLiveFill(correlationId, trackedTrade.EntryPrice, request.Signal.Quantity, nowUtc);
        }

        _logger.LogInformation("Trade {Status}: {TradeId} @ {Price}",
            trackedTrade.Status, trackedTrade.Id, trackedTrade.EntryPrice);

        return trackedTrade;
    }

    private async Task<Trade?> TryReturnExistingTradeOnDuplicateAsync(Trade trade, Guid signalId)
    {
        try
        {
            await _tradeRepository.AddAsync(trade);
            return null;
        }
        catch (DuplicateSignalTradeException)
        {
            var existingTrade = await _tradeRepository.GetBySignalIdAsync(signalId);
            if (existingTrade is not null)
            {
                _logger.LogWarning(
                    "Concurrent execution claim lost for Signal {SignalId}. Returning existing Trade {TradeId}.",
                    signalId,
                    existingTrade.Id);
                return existingTrade;
            }

            throw;
        }
    }

    private void MapSignalMetadataToTrade(Signal signal, Trade trade)
    {
        trade.Side = signal.Side;
        trade.StrategyTag = signal.StrategyTag;
        trade.RegimeTag = signal.RegimeTag;

        trade.EdgeScore = TryGetDecimal(signal, "EdgeScore") ?? trade.EdgeScore;
        trade.EdgeBand = TryGetString(signal, "EdgeBand") ?? trade.EdgeBand;
        trade.VenueImpactLabel = TryGetString(signal, "VenueImpactLabel") ?? trade.VenueImpactLabel;
        trade.VenueExecutionScope = TryGetString(signal, "VenueExecutionScope") ?? trade.VenueExecutionScope;
        trade.ExactBucketImpactLabel = TryGetString(signal, "ExactBucketImpactLabel") ?? trade.ExactBucketImpactLabel;
        trade.ExactBucketSummary = TryGetString(signal, "ExactBucketSummary") ?? trade.ExactBucketSummary;
        trade.ProfitLeakAuditSummary = TryGetString(signal, "ProfitLeakAuditSummary") ?? trade.ProfitLeakAuditSummary;
        trade.ProfitLeakWorstBucketLabel = TryGetString(signal, "ProfitLeakWorstBucketLabel") ?? trade.ProfitLeakWorstBucketLabel;
        trade.ProfitLeakWorstBucketBeforeLiveChange = TryGetString(signal, "ProfitLeakWorstBucketBeforeLiveChange") ?? trade.ProfitLeakWorstBucketBeforeLiveChange;
        trade.ProfitLeakWorstBucketAfterLiveChange = TryGetString(signal, "ProfitLeakWorstBucketAfterLiveChange") ?? trade.ProfitLeakWorstBucketAfterLiveChange;
        trade.ProfitLeakAuditSizeMultiplier = TryGetDecimal(signal, "ProfitLeakAuditSizeMultiplier") ?? trade.ProfitLeakAuditSizeMultiplier;
        trade.ProfitLeakExactNetExpectancy = TryGetDecimal(signal, "ProfitLeakExactNetExpectancy") ?? trade.ProfitLeakExactNetExpectancy;
        trade.ProfitLeakExitPolicyNetExpectancy = TryGetDecimal(signal, "ProfitLeakExitPolicyNetExpectancy") ?? trade.ProfitLeakExitPolicyNetExpectancy;
        trade.ProfitLeakRegimeNetExpectancy = TryGetDecimal(signal, "ProfitLeakRegimeNetExpectancy") ?? trade.ProfitLeakRegimeNetExpectancy;
        trade.ProfitLeakVenueNetExpectancy = TryGetDecimal(signal, "ProfitLeakVenueNetExpectancy") ?? trade.ProfitLeakVenueNetExpectancy;
        trade.ProfitLeakReducedSizeNetExpectancy = TryGetDecimal(signal, "ProfitLeakReducedSizeNetExpectancy") ?? trade.ProfitLeakReducedSizeNetExpectancy;
        trade.ProfitLeakReducedSizeNoise = TryGetBool(signal, "ProfitLeakReducedSizeNoise") ?? trade.ProfitLeakReducedSizeNoise;
        trade.ProfitLeakLoserClusterRate = TryGetDecimal(signal, "ProfitLeakLoserClusterRate") ?? trade.ProfitLeakLoserClusterRate;
        trade.ProfitLeakDominantLosingExitPolicy = TryGetString(signal, "ProfitLeakDominantLosingExitPolicy") ?? trade.ProfitLeakDominantLosingExitPolicy;
        trade.ConcordanceImpactLabel = TryGetString(signal, "ConcordanceImpactLabel") ?? trade.ConcordanceImpactLabel;
        trade.ConcordanceSummary = TryGetString(signal, "ConcordanceSummary") ?? trade.ConcordanceSummary;
        trade.FeatureDriftImpactLabel = TryGetString(signal, "FeatureDriftImpactLabel") ?? trade.FeatureDriftImpactLabel;
        trade.FeatureDriftSummary = TryGetString(signal, "FeatureDriftSummary") ?? trade.FeatureDriftSummary;
        trade.FeatureDriftDetected = TryGetBool(signal, "FeatureDriftDetected") ?? trade.FeatureDriftDetected;
        trade.FeatureDriftCritical = TryGetBool(signal, "FeatureDriftCritical") ?? trade.FeatureDriftCritical;
        trade.DriftedFeatureCount = TryGetInt(signal, "DriftedFeatureCount") ?? trade.DriftedFeatureCount;
        trade.FeatureDriftMaxZScore = TryGetDecimal(signal, "FeatureDriftMaxZScore") ?? trade.FeatureDriftMaxZScore;
        trade.MlModelVersion = TryGetString(signal, "MlModelVersion") ?? trade.MlModelVersion;
        trade.ExitPolicyTag = TryGetString(signal, "ExitPolicyTag") ?? trade.ExitPolicyTag;
        trade.ExecutionMode = TryGetString(signal, "ExecutionMode") ?? trade.ExecutionMode;
        trade.PositionSizePercent = TryGetDecimal(signal, "RequestedPositionFraction") ?? trade.PositionSizePercent;
        trade.StopLoss = TryGetDecimal(signal, "StopLoss") ?? trade.StopLoss;
        trade.TakeProfit = TryGetDecimal(signal, "TakeProfit") ?? trade.TakeProfit;
        trade.PartialTakeProfit = TryGetDecimal(signal, "PartialTakeProfit") ?? trade.PartialTakeProfit;
        trade.PartialExitFraction = TryGetDecimal(signal, "PartialExitFraction") ?? trade.PartialExitFraction;
        trade.BreakevenTriggerFraction = TryGetDecimal(signal, "BreakevenTriggerFraction") ?? trade.BreakevenTriggerFraction;
        trade.MaxHoldingBars = TryGetInt(signal, "MaxHoldingBars") ?? trade.MaxHoldingBars;
        trade.RequestedQuantity = TryGetDecimal(signal, "RequestedQuantity") ?? trade.RequestedQuantity;
        if (trade.RemainingQuantity <= 0m && trade.RequestedQuantity > 0m && trade.LifecycleStatus == TradeLifecycleStatus.Submitted)
            trade.RemainingQuantity = trade.RequestedQuantity;

        if (signal.Price > 0 && trade.EntryPrice > 0)
            trade.EntrySlippagePercent = ((trade.EntryPrice - signal.Price) / signal.Price) * 100m;

        if (TryGetDecimal(signal, "Leverage") is { } leverage && leverage > 0m)
            trade.Leverage = leverage;
        if (TryGetString(signal, "MarginMode") is { } marginRaw && Enum.TryParse<MarginMode>(marginRaw, true, out var marginMode))
            trade.MarginMode = marginMode;
    }

    private void EnsureLeverageMetadata(Signal signal)
    {
        signal.Metadata ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!signal.Metadata.ContainsKey("Leverage"))
            signal.Metadata["Leverage"] = _defaultLeverage.ToString(CultureInfo.InvariantCulture);
        if (!signal.Metadata.ContainsKey("MarginMode"))
            signal.Metadata["MarginMode"] = _defaultMarginMode.ToString();
    }

    private async Task<string?> CheckPromotionGateAsync(Signal signal)
    {
        if (_performanceStore == null)
        {
            return "Promotion required but no performance store registered.";
        }

        var strategyTag = signal.StrategyTag;
        if (string.IsNullOrWhiteSpace(strategyTag))
        {
            return "Promotion required: signal missing StrategyTag.";
        }

        StrategyPerformanceState state;
        try
        {
            state = await _performanceStore.LoadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Promotion gate: failed to load strategy performance state.");
            return "Promotion required but performance state unavailable.";
        }

        var backtestMatch = state.BacktestBuckets.FirstOrDefault(b =>
            string.Equals(b.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(b.StrategyTag, strategyTag, StringComparison.OrdinalIgnoreCase));
        if (backtestMatch == null)
        {
            return $"Promotion required: no backtest record for strategy '{strategyTag}' on {signal.Symbol}.";
        }

        if (!backtestMatch.BacktestPass)
        {
            return $"Promotion required: backtest acceptance gate failed for strategy '{strategyTag}' on {signal.Symbol}.";
        }

        var paperMatch = state.PaperBuckets.FirstOrDefault(b =>
            string.Equals(b.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(b.StrategyTag, strategyTag, StringComparison.OrdinalIgnoreCase));
        if (paperMatch == null)
        {
            return $"Promotion required: no paper record for strategy '{strategyTag}' on {signal.Symbol}.";
        }

        if (!paperMatch.PaperPass)
        {
            return $"Promotion required: paper gate failed for strategy '{strategyTag}' on {signal.Symbol}.";
        }

        var match = state.LiveBuckets.FirstOrDefault(b =>
            string.Equals(b.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(b.StrategyTag, strategyTag, StringComparison.OrdinalIgnoreCase));

        if (match == null)
        {
            return $"Promotion required: no live record for strategy '{strategyTag}' on {signal.Symbol}.";
        }

        if (!match.PromotionReady || !PromotedStages.Contains(match.PromotionStage))
        {
            return $"Promotion required: strategy '{strategyTag}' stage='{match.PromotionStage}' ready={match.PromotionReady}.";
        }

        return null;
    }

    /// <summary>
    /// [P1.4] Rewrites an opening Market signal as a maker-Limit signal at the
    /// top-of-book inside price using <see cref="MakerExecutionPolicy"/>. The
    /// downstream <c>OkxExecutionService</c> handles post_only submission and the
    /// timeout-then-market fallback when <c>PostOnlyMode</c> + <c>LimitOrderTimeoutSeconds</c>
    /// are configured. Closing / reduceOnly orders are NEVER rerouted —
    /// guaranteed-exit semantics outweigh fee savings on stops.
    /// </summary>
    private void TryRouteAsMakerLimit(Signal signal, OrderBookSnapshot? book, bool isHftMode)
    {
        if (!_orderExecution.EnableMakerRouting) return;
        if (signal.OrderType != OrderType.Market) return;
        if (book is null) return;
        if (book.Value.Bids.Length == 0 || book.Value.Asks.Length == 0) return;
        if (isHftMode) return; // HFT signals need taker latency

        bool isReduceOnly = signal.Metadata.TryGetValue("ReduceOnly", out var ro)
            && bool.TryParse(ro, out var roFlag) && roFlag;
        if (isReduceOnly) return;

        decimal bestBid = book.Value.Bids[0].Price;
        decimal bestAsk = book.Value.Asks[0].Price;
        if (bestBid <= 0m || bestAsk <= 0m || bestAsk <= bestBid) return;

        decimal mid = (bestBid + bestAsk) / 2m;
        decimal spread = bestAsk - bestBid;

        try
        {
            var plan = MakerExecutionPolicy.Plan(
                side: signal.Side,
                mid: mid,
                topOfBookSpread: spread,
                urgency: _orderExecution.MakerUrgency,
                patientTimeoutMs: _orderExecution.MakerPatientTimeoutMs,
                urgentTimeoutMs: _orderExecution.MakerUrgentTimeoutMs,
                fallback: MakerExecutionPolicy.FallbackAction.Market);

            if (plan.OrderType != OrderType.Limit) return; // policy decided market (e.g. zero spread)

            signal.OrderType = OrderType.Limit;
            signal.Price = plan.LimitPrice;
            signal.Metadata["MakerRouted"] = "true";
            signal.Metadata["MakerExpectedRebateBps"] = plan.ExpectedMakerRebateBps.ToString(CultureInfo.InvariantCulture);
            signal.Metadata["MakerPlanReason"] = plan.Reason;

            _logger.LogInformation(
                "Maker routing: {Symbol} {Side} → Limit @ {Px} (mid={Mid}, spread={Spr}, urgency={U:F2}, timeout={T}ms)",
                signal.Symbol, signal.Side, plan.LimitPrice, mid, spread,
                _orderExecution.MakerUrgency, plan.TimeoutMs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Maker routing skipped for {Symbol}: {Msg}", signal.Symbol, ex.Message);
        }
    }

    private static string? TryGetString(Signal signal, string key) =>
        signal.Metadata.TryGetValue(key, out var val) ? val : null;

    private static decimal? TryGetDecimal(Signal signal, string key) =>
        signal.Metadata.TryGetValue(key, out var val) && decimal.TryParse(val, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static bool? TryGetBool(Signal signal, string key) =>
        signal.Metadata.TryGetValue(key, out var val) && bool.TryParse(val, out var parsed) ? parsed : null;

    private static int? TryGetInt(Signal signal, string key) =>
        signal.Metadata.TryGetValue(key, out var val) && int.TryParse(val, out var parsed) ? parsed : null;

    /// <summary>
    /// Compares the fill's entry slippage against <see cref="SlippageAlertOptions.ThresholdBps"/>
    /// and fires a Telegram / log alert via <see cref="IOperatorAlertService"/> on breach.
    /// Per-symbol cooldown prevents alert floods on a wide-spread venue.
    /// </summary>
    private void EvaluateSlippageBreach(string symbol, decimal slippagePercent)
    {
        if (!_slippageAlert.Enabled || _slippageAlert.ThresholdBps <= 0m) return;

        var absBps = Math.Abs(slippagePercent) * 100m; // percent → bps
        if (absBps < _slippageAlert.ThresholdBps) return;

        _telemetry.RecordSlippageBreach(symbol);

        var now = DateTime.UtcNow;
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, _slippageAlert.CooldownSeconds));
        var key = symbol ?? string.Empty;
        if (_slippageAlertCooldown.TryGetValue(key, out var lastAlert) && now - lastAlert < cooldown)
        {
            return; // suppressed by cooldown
        }
        _slippageAlertCooldown[key] = now;

        _logger.LogWarning(
            "Slippage breach: {Symbol} |slip|={Bps:F1}bps (threshold {Threshold}bps).",
            symbol, absBps, _slippageAlert.ThresholdBps);

        if (_operatorAlerts is not null)
        {
            var title = $"Slippage breach · {symbol}";
            var body = $"|entry slip| = {absBps:F1}bps (threshold {_slippageAlert.ThresholdBps}bps). Check venue spread / venue health; consider widening limit offset or lowering order size.";
            _ = _operatorAlerts.NotifyAsync(
                ThatsMyQuant.Application.Strategy.ProMode.AlertPriority.Warning,
                title, body);
        }
    }
}
