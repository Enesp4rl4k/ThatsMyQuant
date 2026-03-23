using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Commands;
using ThatsMyQuant.Application.Resilience;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using ThatsMyQuant.Domain.Interfaces;
using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Application.Handlers;

public class ProcessSignalHandler : IRequestHandler<ProcessSignalCommand, bool>
{
    private readonly ILogger<ProcessSignalHandler> _logger;
    private readonly IMediator _mediator;
    private readonly IAiReasoningService _aiService;
    private readonly IMarketDataService _marketDataService;
    private readonly ISignalRepository _signalRepository;
    private readonly ITradeRepository _tradeRepository;
    private readonly ITechnicalAnalysisService _technicalAnalysis;
    private readonly RiskAgent _riskAgent;
    private readonly bool _useWorkerHandoff;

    public ProcessSignalHandler(
        ILogger<ProcessSignalHandler> logger, 
        IMediator mediator, 
        IAiReasoningService aiService,
        IMarketDataService marketDataService,
        ISignalRepository signalRepository,
        ITradeRepository tradeRepository,
        ITechnicalAnalysisService technicalAnalysis,
        RiskAgent riskAgent,
        IConfiguration configuration)
    {
        _logger = logger;
        _mediator = mediator;
        _aiService = aiService;
        _marketDataService = marketDataService;
        _signalRepository = signalRepository;
        _tradeRepository = tradeRepository;
        _technicalAnalysis = technicalAnalysis;
        _riskAgent = riskAgent;
        _useWorkerHandoff = bool.TryParse(configuration["Execution:UseWorkerHandoff"], out var useWorkerHandoff)
            && useWorkerHandoff;
    }

    public async Task<bool> Handle(ProcessSignalCommand request, CancellationToken cancellationToken)
    {
        LogSignalStart(request.Signal);

        // 0. Persist Signal immediately (audit trail)
        await _signalRepository.AddAsync(request.Signal);

        // 0.5: Market Hours Check
        if (!await IsMarketOpenAsync(request.Signal))
            return false;

        var pipeline = ResiliencePolicies.CreateRetryPipeline(_logger);

        // STEP 1: Fetch Candle Data
        var candles = await pipeline.ExecuteAsync(async ct =>
            await _marketDataService.GetCandlesAsync(request.Signal.Symbol, 100, GetMarketType(request.Signal.Symbol)), cancellationToken);

        // STEP 1.5: Detect Market Regime — Adapt Risk Parameters
        var (regime, structure, riskParams) = AnalyzeMarketRegime(candles, request.Signal);
        
        // STEP 2: Technical Analysis — Indicators + Confluence
        var (decision, analysisSuccess) = await PerformTechnicalAnalysisAsync(request.Signal, candles, riskParams);
        if (!analysisSuccess) return false;

        // STEP 3: Swarm Orchestration Decision
        var (finalDecision, swarmApproved) = await NegotiateWithSwarmAsync(request.Signal, decision, riskParams, candles, cancellationToken);
        if (!swarmApproved) return false;

        // STEP 5: Dispatch execution (sync or worker handoff)
        return await DispatchExecutionAsync(request.Signal, finalDecision, cancellationToken);
    }

    private void LogSignalStart(Signal signal)
    {
        _logger.LogInformation("═══════════════════════════════════════════════════════");
        _logger.LogInformation("Processing Signal: {Id} | {Symbol} | {Side} | ${Price}",
            signal.Id, signal.Symbol, signal.Side, signal.Price);
    }

    private async Task<bool> IsMarketOpenAsync(Signal signal)
    {
        string market = GetMarketType(signal.Symbol);
        var session = MarketHoursScheduler.GetCurrentSession(market);
        
        if (session.CanTrade) return true;

        _logger.LogWarning("⏰ {Market} market not open: {Session}", market, session.Description);
        signal.Status = TradeStatus.Rejected;
        signal.Reasoning = $"Outside {market} trading hours: {session.Description}";
        await _signalRepository.UpdateAsync(signal);
        return false;
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
        CancellationToken ct)
    {
        var swarmRequest = new SwarmNegotiationRequest
        {
            Signal = signal,
            InitialDecision = decision,
            Risk = riskParams,
            Candles = candles
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

        _logger.LogInformation("✅ SWARM APPROVED: Confidence={Confidence}/100 | SL={SL:F2} | TP={TP:F2} | Size={Size:P1} | Action={Action}",
            adjustedDecision.Confidence, adjustedDecision.StopLoss, adjustedDecision.TakeProfit, adjustedDecision.PositionSizePercent, signal.ExecutionAction);

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

    private static string GetMarketType(string symbol)
    {
        if (symbol.Contains("=")) return "Forex";
        if (symbol.Count(c => c == '-') == 1) return "Crypto"; // e.g., BTC-USD
        if (symbol.All(c => char.IsLetterOrDigit(c))) return "BIST"; // e.g., THYAO
        return "BIST"; // Default
    }
}
