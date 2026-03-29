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

        if (signal.Metadata.TryGetValue("EdgeScore", out var esStr) && decimal.TryParse(esStr, CultureInfo.InvariantCulture, out var es))
            trade.EdgeScore = es;

        if (signal.Metadata.TryGetValue("EdgeBand", out var eb)) trade.EdgeBand = eb;
        if (signal.Metadata.TryGetValue("VenueImpactLabel", out var vil)) trade.VenueImpactLabel = vil;
        if (signal.Metadata.TryGetValue("VenueExecutionScope", out var ves)) trade.VenueExecutionScope = ves;
        if (signal.Metadata.TryGetValue("ExactBucketImpactLabel", out var ebil)) trade.ExactBucketImpactLabel = ebil;
        if (signal.Metadata.TryGetValue("ExactBucketSummary", out var ebs)) trade.ExactBucketSummary = ebs;
        if (signal.Metadata.TryGetValue("ConcordanceImpactLabel", out var cil)) trade.ConcordanceImpactLabel = cil;
        if (signal.Metadata.TryGetValue("ConcordanceSummary", out var cs)) trade.ConcordanceSummary = cs;
        if (signal.Metadata.TryGetValue("FeatureDriftImpactLabel", out var fdil)) trade.FeatureDriftImpactLabel = fdil;
        if (signal.Metadata.TryGetValue("FeatureDriftSummary", out var fds)) trade.FeatureDriftSummary = fds;
        if (signal.Metadata.TryGetValue("FeatureDriftDetected", out var fdd) && bool.TryParse(fdd, out var driftDetected)) trade.FeatureDriftDetected = driftDetected;
        if (signal.Metadata.TryGetValue("FeatureDriftCritical", out var fdc) && bool.TryParse(fdc, out var driftCritical)) trade.FeatureDriftCritical = driftCritical;
        if (signal.Metadata.TryGetValue("DriftedFeatureCount", out var dfc) && int.TryParse(dfc, out var driftedFeatureCount)) trade.DriftedFeatureCount = driftedFeatureCount;
        if (signal.Metadata.TryGetValue("FeatureDriftMaxZScore", out var fdz) && decimal.TryParse(fdz, CultureInfo.InvariantCulture, out var driftMaxZScore)) trade.FeatureDriftMaxZScore = driftMaxZScore;
        if (signal.Metadata.TryGetValue("MlModelVersion", out var modelVersion)) trade.MlModelVersion = modelVersion;
        if (signal.Metadata.TryGetValue("ExitPolicyTag", out var ept)) trade.ExitPolicyTag = ept;
        if (signal.Metadata.TryGetValue("ExecutionMode", out var em)) trade.ExecutionMode = em;

        if (signal.Price > 0 && trade.EntryPrice > 0)
            trade.EntrySlippagePercent = ((trade.EntryPrice - signal.Price) / signal.Price) * 100m;
    }

}
