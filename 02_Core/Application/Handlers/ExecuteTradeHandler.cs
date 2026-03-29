using MediatR;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Application.Resilience;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Application.Strategy;
using System.Globalization;

namespace ThatsMyQuant.Application.Handlers;

public class ExecuteTradeHandler : IRequestHandler<ExecuteTradeCommand, Trade>
{
    private readonly ILogger<ExecuteTradeHandler> _logger;
    private readonly IExchangeService _exchangeService;
    private readonly ITradeRepository _tradeRepository;
    private readonly CircuitBreakerService _circuitBreaker;

    public ExecuteTradeHandler(
        ILogger<ExecuteTradeHandler> logger, 
        IExchangeService exchangeService,
        ITradeRepository tradeRepository,
        CircuitBreakerService circuitBreaker)
    {
        _logger = logger;
        _exchangeService = exchangeService;
        _tradeRepository = tradeRepository;
        _circuitBreaker = circuitBreaker;
    }

    public async Task<Trade> Handle(ExecuteTradeCommand request, CancellationToken cancellationToken)
    {
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

        var circuitGuard = _circuitBreaker.CheckOrderAllowed();
        if (!circuitGuard.Allowed)
        {
            _logger.LogWarning(
                "Circuit breaker blocked execution for Signal {SignalId}: {Reason}",
                request.Signal.Id,
                circuitGuard.BlockReason);

            var blockedTrade = CreateFailedTradeFromSignal(request.Signal);
            await _tradeRepository.AddAsync(blockedTrade);
            return blockedTrade;
        }

        // Domain Logic: Reject weak liquidity HFT probes before hitting the venue
        if (request.Signal.Metadata.TryGetValue("ExecutionMode", out var mode) && mode == "HFT")
        {
            if (request.Signal.Volume24h < 500_000m)
            {
                _logger.LogWarning("Rejecting HFT Signal {Id} due to insufficient 24h volume ({Volume})", request.Signal.Id, request.Signal.Volume24h);
                return new Trade { Status = TradeStatus.Failed, SignalId = request.Signal.Id, Symbol = request.Signal.Symbol };
            }
        }

        // Execute with Polly full pipeline (Retry + Circuit Breaker)
        var pipeline = ResiliencePolicies.CreateFullPipeline(_logger);

        Trade trade;
        try
        {
            trade = await pipeline.ExecuteAsync(async ct =>
                await _exchangeService.ExecuteOrderAsync(request.Signal), cancellationToken);

            trade.Status = TradeStatus.Executed;
            MapSignalMetadataToTrade(request.Signal, trade);
            _circuitBreaker.RecordSuccess();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trade execution FAILED for Signal {Id} after retries + circuit breaker.",
                request.Signal.Id);
            _circuitBreaker.RecordError(ex);

            // Persist the failed trade for audit
            trade = CreateFailedTradeFromSignal(request.Signal);
        }

        // Persist trade result
        await _tradeRepository.AddAsync(trade);

        _logger.LogInformation("Trade {Status}: {TradeId} @ {Price}",
            trade.Status, trade.Id, trade.EntryPrice);

        return trade;
    }

    private Trade CreateFailedTradeFromSignal(Signal signal)
    {
        var trade = new Trade
        {
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            Side = signal.Side,
            EntryPrice = signal.Price,
            Quantity = 0,
            Status = TradeStatus.Failed,
            StrategyTag = signal.StrategyTag,
            RegimeTag = signal.RegimeTag,
            EntryTime = DateTime.UtcNow
        };

        MapSignalMetadataToTrade(signal, trade);
        return trade;
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

        if (signal.Price > 0 && trade.EntryPrice > 0)
            trade.EntrySlippagePercent = ((trade.EntryPrice - signal.Price) / signal.Price) * 100m;
    }

    private static string? TryGetString(Signal signal, string key) =>
        signal.Metadata.TryGetValue(key, out var val) ? val : null;

    private static decimal? TryGetDecimal(Signal signal, string key) =>
        signal.Metadata.TryGetValue(key, out var val) && decimal.TryParse(val, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static bool? TryGetBool(Signal signal, string key) =>
        signal.Metadata.TryGetValue(key, out var val) && bool.TryParse(val, out var parsed) ? parsed : null;

    private static int? TryGetInt(Signal signal, string key) =>
        signal.Metadata.TryGetValue(key, out var val) && int.TryParse(val, out var parsed) ? parsed : null;
}
