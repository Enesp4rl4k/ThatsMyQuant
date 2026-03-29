using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Application.Resilience;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Application.Common;

namespace ThatsMyQuant.Application.Handlers;

public class ProcessSignalHandler : IRequestHandler<ProcessSignalCommand, bool>
{
    private readonly ILogger<ProcessSignalHandler> _logger;
    private readonly IMediator _mediator;
    private readonly ISignalRepository _signalRepository;
    private readonly ITechnicalAnalysisService _technicalAnalysis;
    private readonly IMarketDataService _marketDataService;
    private readonly ISignalValidator[] _validators;
    private readonly IAgentCoordinator _agentCoordinator;
    private readonly bool _useWorkerHandoff;

    public ProcessSignalHandler(
        ILogger<ProcessSignalHandler> logger,
        IMediator mediator,
        IMarketDataService marketDataService,
        ISignalRepository signalRepository,
        ITechnicalAnalysisService technicalAnalysis,
        IEnumerable<ISignalValidator> validators,
        IAgentCoordinator agentCoordinator,
        IConfiguration configuration)
    {
        _logger = logger;
        _mediator = mediator;
        _marketDataService = marketDataService;
        _signalRepository = signalRepository;
        _technicalAnalysis = technicalAnalysis;
        _validators = validators.OrderBy(v => v.Order).ToArray();
        _agentCoordinator = agentCoordinator;
        _useWorkerHandoff = bool.TryParse(configuration["Execution:UseWorkerHandoff"], out var useWorkerHandoff)
            && useWorkerHandoff;
    }

    public async Task<bool> Handle(ProcessSignalCommand request, CancellationToken cancellationToken)
    {
        TradingMetrics.SignalsReceived.Add(1, new KeyValuePair<string, object?>("symbol", request.Signal.Symbol));
        LogSignalStart(request.Signal);

        // 0. Persist Signal immediately (audit trail)
        await _signalRepository.AddAsync(request.Signal);

        // 0.5: Modular Validation Chain (Volume, Price, Market Hours, etc.)
        // Run all validators concurrently, then check results in priority order
        var validationTasks = _validators.Select(v => v.ValidateAsync(request.Signal)).ToArray();
        var validationResults = await Task.WhenAll(validationTasks);

        for (int i = 0; i < validationResults.Length; i++)
        {
            var (isValid, failureReason) = validationResults[i];
            if (!isValid)
            {
                request.Signal.Status = TradeStatus.Rejected;
                request.Signal.Reasoning = failureReason;
                await _signalRepository.UpdateAsync(request.Signal);
                TradingMetrics.SignalsRejected.Add(1, new KeyValuePair<string, object?>("reason", "validation"));
                _logger.LogWarning("Signal Validation Failed: {Reason}", failureReason);
                return false;
            }
        }

        var pipeline = ResiliencePolicies.CreateRetryPipeline(_logger);

        // STEP 1: Fetch Candle Data
        var candles = await pipeline.ExecuteAsync(async ct =>
            await _marketDataService.GetCandlesAsync(request.Signal.Symbol, 100, request.Signal.MarketType.ToString()), cancellationToken);

        // STEP 1.5: Detect Market Regime — Adapt Risk Parameters
        var (regime, structure, riskParams) = AnalyzeMarketRegime(candles, request.Signal);

        // STEP 1.7: Build Foundation (Tier 1) and Pattern (Tier 2) snapshots
        var foundation = await _agentCoordinator.BuildFoundationAsync([], candles, cancellationToken);
        var patterns = await _agentCoordinator.BuildPatternAsync(request.Signal, foundation, candles, cancellationToken);
        
        // STEP 2: Technical Analysis — Indicators + Confluence
        var (decision, analysisSuccess) = await PerformTechnicalAnalysisAsync(request.Signal, candles, riskParams);
        if (!analysisSuccess) return false;

        // STEP 3: Swarm Orchestration Decision
        var (finalDecision, swarmApproved) = await NegotiateWithSwarmAsync(
            request.Signal, decision, riskParams, candles, foundation, patterns, regime, cancellationToken);
        if (!swarmApproved)
        {
            TradingMetrics.SignalsRejected.Add(1, new KeyValuePair<string, object?>("reason", "swarm_veto"));
            return false;
        }

        TradingMetrics.SignalsApproved.Add(1, new KeyValuePair<string, object?>("symbol", request.Signal.Symbol));

        // STEP 5: Dispatch execution (sync or worker handoff)
        return await DispatchExecutionAsync(request.Signal, finalDecision, cancellationToken);
    }

    private void LogSignalStart(Signal signal)
    {
        _logger.LogInformation("Processing Signal: {Id} | {Symbol} | {Side} | {Price}",
            signal.Id, signal.Symbol, signal.Side, signal.Price);
    }


    private (MarketRegimeDetector.RegimeResult regime, StructuralAnalysisResult structure, RiskParameters riskParams) AnalyzeMarketRegime(
        List<Candle> candles, 
        Signal signal)
    {
        var regime = MarketRegimeDetector.Detect(candles);
        var structure = StructuralPatternDetector.Analyze(candles);
        
        _logger.LogInformation("📊 Market Regime: {Regime} | Structure: {Structure}", regime.Regime, structure.Structure);
        _logger.LogInformation("📝 Description: {Desc}", structure.Description);

        signal.RegimeTag = regime.Regime.ToString();
        signal.Metadata["MarketStructure"] = structure.Structure.ToString();
        signal.Metadata["StructureDescription"] = structure.Description;

        var riskParams = regime.Regime switch
        {
            MarketRegime.Volatile  => RiskParameters.Default with { MaxPositionSizePercent = 2.5m },
            MarketRegime.Trending  => RiskParameters.Default with { MaxPositionSizePercent = 7.5m },
            _                      => RiskParameters.Default
        };

        return (regime, structure, riskParams);
    }

    private async Task<(TradeDecision decision, bool success)> PerformTechnicalAnalysisAsync(
        Signal signal, 
        List<Candle> candles, 
        RiskParameters riskParams)
    {
        try
        {
            var decision = await _technicalAnalysis.AnalyzeAsync(signal, candles, riskParams);
            
            signal.Confidence = decision.Confidence;
            signal.Reasoning = decision.Reasoning;
            signal.StrategyTag = decision.StrategyTag;
            signal.RegimeTag = decision.RegimeTag;

            return (decision, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Technical analysis failed for {Symbol}", signal.Symbol);
            signal.Status = TradeStatus.Failed;
            signal.Reasoning = $"Analysis error: {ex.Message}";
            await _signalRepository.UpdateAsync(signal);
            return (null!, false);
        }
    }

    private async Task<(TradeDecision decision, bool approved)> NegotiateWithSwarmAsync(
        Signal signal,
        TradeDecision decision,
        RiskParameters riskParams,
        List<Candle> candles,
        FoundationSnapshot foundation,
        PatternSnapshot patterns,
        MarketRegimeDetector.RegimeResult regime,
        CancellationToken ct)
    {
        var swarmRequest = new SwarmNegotiationRequest
        {
            Signal = signal,
            InitialDecision = decision,
            Risk = riskParams,
            Candles = candles,
            Foundation = foundation,
            Patterns = patterns,
            Regime = regime.Regime
        };

        var swarmResult = await _mediator.Send(swarmRequest, ct);

        if (!swarmResult.Approved)
        {
            signal.Status = TradeStatus.Rejected;
            signal.Reasoning += $" | Swarm Rejected: {swarmResult.RejectionFlags}";
            await _signalRepository.UpdateAsync(signal);
            _logger.LogWarning("❌ SWARM REJECTED: {Flags}", swarmResult.RejectionFlags);
            _logger.LogInformation("═══════════════════════════════════════════════════════");
            return (decision, false);
        }

        var adjustedDecision = decision with 
        { 
            PositionSizePercent = swarmResult.AdjustedKellyFraction,
            TakeProfit = swarmResult.AdjustedTakeProfit
        };
        signal.ExecutionAction = swarmResult.ExecutionAction;

        if (signal.Price > 0)
        {
            signal.Quantity = Math.Round((riskParams.StartingEquity * adjustedDecision.PositionSizePercent) / signal.Price, 4);
        }

        _logger.LogInformation("✅ SWARM APPROVED: Confidence={Confidence}/100 | SL={SL:F2} | TP={TP:F2} | Size={Size:P1} | Action={Action} | Qty={Qty}",
            adjustedDecision.Confidence, adjustedDecision.StopLoss, adjustedDecision.TakeProfit, adjustedDecision.PositionSizePercent, signal.ExecutionAction, signal.Quantity);

        return (adjustedDecision, true);
    }

    private async Task<bool> DispatchExecutionAsync(Signal signal, TradeDecision decision, CancellationToken ct)
    {
        if (_useWorkerHandoff)
        {
            signal.Status = TradeStatus.Queued;
            signal.Reasoning += " | Execution queued for worker.";
            await _signalRepository.UpdateAsync(signal);
            _logger.LogInformation("📥 Trade queued for execution worker: {Symbol} ({Side})", signal.Symbol, signal.Side);
        }
        else
        {
            var executedTrade = await _mediator.Send(new ExecuteTradeCommand(signal), ct);
            if (executedTrade == null || executedTrade.Status == TradeStatus.Failed)
            {
                signal.Status = TradeStatus.Failed;
                signal.Reasoning += executedTrade == null ? " | Execution returned no trade result." : " | Execution failed.";
                await _signalRepository.UpdateAsync(signal);
                _logger.LogWarning("Trade execution failed or empty for {Symbol} ({Side})", signal.Symbol, signal.Side);
                return false;
            }

            signal.Status = executedTrade.Status;
            await _signalRepository.UpdateAsync(signal);
            _logger.LogInformation("🚀 Trade dispatched for {Symbol} ({Side})", signal.Symbol, signal.Side);
        }
        _logger.LogInformation("═══════════════════════════════════════════════════════");
        return true;
    }
}
