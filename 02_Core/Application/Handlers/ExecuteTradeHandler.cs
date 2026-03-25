using MediatR;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Application.Resilience;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using System.Globalization;

namespace ThatsMyQuant.Application.Handlers;

public class ExecuteTradeHandler : IRequestHandler<ExecuteTradeCommand, Trade>
{
    private readonly ILogger<ExecuteTradeHandler> _logger;
    private readonly IExchangeService _exchangeService;
    private readonly ITradeRepository _tradeRepository;

    public ExecuteTradeHandler(
        ILogger<ExecuteTradeHandler> logger, 
        IExchangeService exchangeService,
        ITradeRepository tradeRepository)
    {
        _logger = logger;
        _exchangeService = exchangeService;
        _tradeRepository = tradeRepository;
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
                EntryTime = DateTime.UtcNow
            };
        }

        // Persist trade result
        await _tradeRepository.AddAsync(trade);

        _logger.LogInformation("Trade {Status}: {TradeId} @ {Price}",
            trade.Status, trade.Id, trade.EntryPrice);

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
        if (signal.Metadata.TryGetValue("ExitPolicyTag", out var ept)) trade.ExitPolicyTag = ept;
        if (signal.Metadata.TryGetValue("ExecutionMode", out var em)) trade.ExecutionMode = em;

        if (signal.Price > 0 && trade.EntryPrice > 0)
            trade.EntrySlippagePercent = ((trade.EntryPrice - signal.Price) / signal.Price) * 100m;
    }

}
