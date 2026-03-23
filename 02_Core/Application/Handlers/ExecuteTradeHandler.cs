using MediatR;
using Microsoft.Extensions.Logging;
using System.Globalization;
using ThatsMyQuant.Application.Commands;
using ThatsMyQuant.Application.Resilience;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using ThatsMyQuant.Domain.Interfaces;

namespace ThatsMyQuant.Application.Handlers;

public class ExecuteTradeHandler : IRequestHandler<ExecuteTradeCommand, Trade>
{
    private readonly ILogger<ExecuteTradeHandler> _logger;
    private readonly IExchangeService _exchangeService;
    private readonly ITradeRepository _tradeRepository;
    private readonly HftExecutionService _hftExecutionService;
    private readonly ExitPolicyLearningService? _exitPolicyLearningService;

    public ExecuteTradeHandler(
        ILogger<ExecuteTradeHandler> logger, 
        IExchangeService exchangeService,
        ITradeRepository tradeRepository,
        HftExecutionService hftExecutionService,
        ExitPolicyLearningService? exitPolicyLearningService = null)
    {
        _logger = logger;
        _exchangeService = exchangeService;
        _tradeRepository = tradeRepository;
        _hftExecutionService = hftExecutionService;
        _exitPolicyLearningService = exitPolicyLearningService;
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

        // Execute with Polly full pipeline (Retry + Circuit Breaker)
        var pipeline = ResiliencePolicies.CreateFullPipeline(_logger);

        Trade trade;
        try
        {
            trade = IsHftSignal(request.Signal)
                ? await pipeline.ExecuteAsync(async ct =>
                    await _hftExecutionService.ExecuteAsync(request.Signal, ct), cancellationToken)
                : await pipeline.ExecuteAsync(async ct =>
                    await _exchangeService.ExecuteOrderAsync(request.Signal), cancellationToken);

            trade.Side = request.Signal.Side;
            trade.StrategyTag = request.Signal.StrategyTag;
            trade.RegimeTag = request.Signal.RegimeTag;
            trade.ExitPolicyTag = request.Signal.Metadata.GetValueOrDefault("ExitPolicyTag", trade.ExitPolicyTag);
            trade.EdgeBand = request.Signal.Metadata.GetValueOrDefault("EdgeBand", string.Empty);
            trade.ExactBucketImpactLabel = request.Signal.Metadata.GetValueOrDefault("ExactBucketImpactLabel", string.Empty);
            trade.ExactBucketSummary = request.Signal.Metadata.GetValueOrDefault("ExactBucketSummary", string.Empty);
            trade.FinalAllocatorScope = request.Signal.Metadata.GetValueOrDefault("FinalAllocatorScope", string.Empty);
            trade.VenueExecutionScope = request.Signal.Metadata.GetValueOrDefault("VenueExecutionScope", string.Empty);
            trade.VenueImpactLabel = request.Signal.Metadata.GetValueOrDefault("VenueImpactLabel", string.Empty);
            trade.ConcordanceImpactLabel = request.Signal.Metadata.GetValueOrDefault("ConcordanceImpactLabel", string.Empty);
            trade.ConcordanceSummary = request.Signal.Metadata.GetValueOrDefault("ConcordanceSummary", string.Empty);
            trade.ExecutionMode = request.Signal.Metadata.GetValueOrDefault("ExecutionMode", "REAL");
            trade.PromotionStage = request.Signal.Metadata.GetValueOrDefault("PromotionStage", "Backtest");
            if (decimal.TryParse(request.Signal.Metadata.GetValueOrDefault("EdgeScore", "0"), NumberStyles.Number, CultureInfo.InvariantCulture, out var edgeScore) ||
                decimal.TryParse(request.Signal.Metadata.GetValueOrDefault("EdgeScore", "0"), out edgeScore))
            {
                trade.EdgeScore = edgeScore;
            }
            if (decimal.TryParse(request.Signal.Metadata.GetValueOrDefault("FinalAllocatorBias", "0"), NumberStyles.Number, CultureInfo.InvariantCulture, out var finalAllocatorBias) ||
                decimal.TryParse(request.Signal.Metadata.GetValueOrDefault("FinalAllocatorBias", "0"), out finalAllocatorBias))
            {
                trade.FinalAllocatorBias = finalAllocatorBias;
            }
            if (decimal.TryParse(request.Signal.Metadata.GetValueOrDefault("VenueExecutionBias", "0"), NumberStyles.Number, CultureInfo.InvariantCulture, out var venueExecutionBias) ||
                decimal.TryParse(request.Signal.Metadata.GetValueOrDefault("VenueExecutionBias", "0"), out venueExecutionBias))
            {
                trade.VenueExecutionBias = venueExecutionBias;
            }
            if (trade.Status == default)
            {
                trade.Status = TradeStatus.Executed;
            }
            if (trade.EntrySlippagePercent == 0m)
            {
                trade.EntrySlippagePercent = request.Signal.Price > 0
                    ? Math.Abs((trade.EntryPrice - request.Signal.Price) / request.Signal.Price) * 100m
                    : 0m;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trade execution FAILED for Signal {Id} after retries + circuit breaker.",
                request.Signal.Id);

            // Persist the failed trade for audit
            trade = new Trade
            {
                SignalId = request.Signal.Id,
                Symbol = request.Signal.Symbol,
                Side = request.Signal.Side,
                EntryPrice = request.Signal.Price,
                Quantity = 0,
                Status = TradeStatus.Failed,
                StrategyTag = request.Signal.StrategyTag,
                RegimeTag = request.Signal.RegimeTag,
                ExitPolicyTag = request.Signal.Metadata.GetValueOrDefault("ExitPolicyTag", "TrendRunner"),
                EdgeBand = request.Signal.Metadata.GetValueOrDefault("EdgeBand", string.Empty),
                ExactBucketImpactLabel = request.Signal.Metadata.GetValueOrDefault("ExactBucketImpactLabel", string.Empty),
                ExactBucketSummary = request.Signal.Metadata.GetValueOrDefault("ExactBucketSummary", string.Empty),
                FinalAllocatorScope = request.Signal.Metadata.GetValueOrDefault("FinalAllocatorScope", string.Empty),
                VenueExecutionScope = request.Signal.Metadata.GetValueOrDefault("VenueExecutionScope", string.Empty),
                VenueImpactLabel = request.Signal.Metadata.GetValueOrDefault("VenueImpactLabel", string.Empty),
                ConcordanceImpactLabel = request.Signal.Metadata.GetValueOrDefault("ConcordanceImpactLabel", string.Empty),
                ConcordanceSummary = request.Signal.Metadata.GetValueOrDefault("ConcordanceSummary", string.Empty),
                ExecutionMode = request.Signal.Metadata.GetValueOrDefault("ExecutionMode", "REAL"),
                PromotionStage = request.Signal.Metadata.GetValueOrDefault("PromotionStage", "Backtest"),
                EdgeScore = decimal.TryParse(request.Signal.Metadata.GetValueOrDefault("EdgeScore", "0"), NumberStyles.Number, CultureInfo.InvariantCulture, out var failedEdgeScore) ||
                            decimal.TryParse(request.Signal.Metadata.GetValueOrDefault("EdgeScore", "0"), out failedEdgeScore)
                    ? failedEdgeScore
                    : 0m,
                FinalAllocatorBias = decimal.TryParse(request.Signal.Metadata.GetValueOrDefault("FinalAllocatorBias", "0"), NumberStyles.Number, CultureInfo.InvariantCulture, out var failedAllocatorBias) ||
                                     decimal.TryParse(request.Signal.Metadata.GetValueOrDefault("FinalAllocatorBias", "0"), out failedAllocatorBias)
                    ? failedAllocatorBias
                    : 0m,
                VenueExecutionBias = decimal.TryParse(request.Signal.Metadata.GetValueOrDefault("VenueExecutionBias", "0"), NumberStyles.Number, CultureInfo.InvariantCulture, out var failedVenueBias) ||
                                     decimal.TryParse(request.Signal.Metadata.GetValueOrDefault("VenueExecutionBias", "0"), out failedVenueBias)
                    ? failedVenueBias
                    : 0m,
                EntrySlippagePercent = 0m,
                EntryTime = DateTime.UtcNow
            };
        }

        // Persist trade result
        await _tradeRepository.AddAsync(trade);
        _exitPolicyLearningService?.RecordLiveState(new[] { trade });

        _logger.LogInformation("Trade {Status}: {TradeId} @ {Price}",
            trade.Status, trade.Id, trade.EntryPrice);

        return trade;
    }

    private static bool IsHftSignal(Signal signal)
        => signal.Source.Contains("HFT", StringComparison.OrdinalIgnoreCase) ||
           (signal.Metadata.TryGetValue("ExecutionMode", out var mode) &&
            mode.Contains("HFT", StringComparison.OrdinalIgnoreCase)) ||
           string.Equals(signal.StrategyTag, "HftMicroEdge", StringComparison.OrdinalIgnoreCase);
}
