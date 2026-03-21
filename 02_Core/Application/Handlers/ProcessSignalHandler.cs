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
    private readonly StrategyExpectancyService _expectancyService;
    private readonly SymbolExpectancyService _symbolExpectancyService;
    private readonly StrategyPromotionService _promotionService;
    private readonly IHftSignalGenerationService _hftSignalGenerationService;
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
        StrategyExpectancyService expectancyService,
        SymbolExpectancyService symbolExpectancyService,
        StrategyPromotionService promotionService,
        IConfiguration configuration,
        IHftSignalGenerationService? hftSignalGenerationService = null)
    {
        _logger = logger;
        _mediator = mediator;
        _aiService = aiService;
        _marketDataService = marketDataService;
        _signalRepository = signalRepository;
        _tradeRepository = tradeRepository;
        _technicalAnalysis = technicalAnalysis;
        _riskAgent = riskAgent;
        _expectancyService = expectancyService;
        _symbolExpectancyService = symbolExpectancyService;
        _promotionService = promotionService;
        _hftSignalGenerationService = hftSignalGenerationService ?? new HftSignalGenerationService(technicalAnalysis);
        _useWorkerHandoff = bool.TryParse(configuration["Execution:UseWorkerHandoff"], out var useWorkerHandoff)
            && useWorkerHandoff;
    }

    public async Task<bool> Handle(ProcessSignalCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("═══════════════════════════════════════════════════════");
        _logger.LogInformation("Processing Signal: {Id} | {Symbol} | {Side} | ${Price}",
            request.Signal.Id, request.Signal.Symbol, request.Signal.Side, request.Signal.Price);

        // 0. Persist Signal immediately (audit trail)
        await _signalRepository.AddAsync(request.Signal);

        var pipeline = ResiliencePolicies.CreateRetryPipeline(_logger);

        // 0.5: Market Hours Check
        string market = GetMarketType(request.Signal.Symbol);

        var session = MarketHoursScheduler.GetCurrentSession(market);
        if (!session.CanTrade)
        {
            _logger.LogWarning("⏰ {Market} market not open: {Session}", market, session.Description);
            request.Signal.Status = TradeStatus.Rejected;
            request.Signal.Reasoning = $"Outside {market} trading hours: {session.Description}";
            await _signalRepository.UpdateAsync(request.Signal);
            return false;
        }

        // ═══════════════════════════════════════════════════════
        // STEP 1: Fetch Candle Data for Indicator Computation
        // ═══════════════════════════════════════════════════════
        var candles = await pipeline.ExecuteAsync(async ct =>
            await _marketDataService.GetCandlesAsync(request.Signal.Symbol, 100, market), cancellationToken);

        // ═══════════════════════════════════════════════════════
        // STEP 1.5: Detect Market Regime — Adapt Risk Parameters
        // ═══════════════════════════════════════════════════════
        var regime = MarketRegimeDetector.Detect(candles);
        var structure = StructuralPatternDetector.Analyze(candles); // SAHOL 4h Pattern Detection
        
        _logger.LogInformation("📊 Market Regime: {Regime} | Structure: {Structure}", regime.Regime, structure.Structure);
        _logger.LogInformation("📝 Description: {Desc}", structure.Description);

        request.Signal.RegimeTag = regime.Regime.ToString();
        request.Signal.Metadata["MarketStructure"] = structure.Structure.ToString();
        request.Signal.Metadata["StructureDescription"] = structure.Description;

        // Adapt risk parameters to the current regime
        var riskParams = regime.Regime switch
        {
            MarketRegime.Volatile  => RiskParameters.Default with { MaxPositionSizePercent = 2.5m }, // Smaller size in volatile markets
            MarketRegime.Trending  => RiskParameters.Default with { MaxPositionSizePercent = 7.5m }, // Larger size when trend is clear
            _                      => RiskParameters.Default
        };

        // ═══════════════════════════════════════════════════════
        // STEP 2: Technical Analysis — Indicators + Confluence
        // ═══════════════════════════════════════════════════════
        TradeDecision decision;

        try
        {
            decision = IsHftSignal(request.Signal)
                ? await _hftSignalGenerationService.AnalyzeAsync(request.Signal, candles, riskParams)
                : await _technicalAnalysis.AnalyzeAsync(request.Signal, candles, riskParams);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Technical analysis failed for {Symbol}", request.Signal.Symbol);
            request.Signal.Status = TradeStatus.Failed;
            request.Signal.Reasoning = $"Analysis error: {ex.Message}";
            await _signalRepository.UpdateAsync(request.Signal);
            return false;
        }

        // Store confidence and reasoning on the signal
        request.Signal.Confidence = decision.Confidence;
        request.Signal.Reasoning = decision.Reasoning;
        request.Signal.StrategyTag = decision.StrategyTag;
        request.Signal.RegimeTag = decision.RegimeTag;

        // ═══════════════════════════════════════════════════════
        // STEP 3: Decision Gate — Execute or Reject
        // ═══════════════════════════════════════════════════════
        if (!decision.ShouldTrade)
        {
            request.Signal.Status = TradeStatus.Rejected;
            await _signalRepository.UpdateAsync(request.Signal);
            _logger.LogWarning("❌ REJECTED: {Reasoning}", decision.Reasoning);
            _logger.LogInformation("═══════════════════════════════════════════════════════");
            return false;
        }

        // Override signal side with the strategy's recommendation
        request.Signal.Side = decision.Side;

        // Portfolio-wide risk guardrails (drawdown kill-switch / open-trade cap).
        var recentTrades = await _tradeRepository.GetAllAsync(500);
        var expectancyGuard = _expectancyService.Evaluate(recentTrades, request.Signal, decision);
        if (!expectancyGuard.Approved)
        {
            request.Signal.Status = TradeStatus.Rejected;
            request.Signal.Reasoning += $" | Expectancy Guard: {expectancyGuard.Reason}";
            await _signalRepository.UpdateAsync(request.Signal);
            _logger.LogWarning("❌ EXPECTANCY BLOCK: {Reason}", expectancyGuard.Reason);
            return false;
        }

        if (expectancyGuard.SizeMultiplier < 1.0m)
        {
            var resizedPosition = Math.Max(0m, decision.PositionSizePercent * expectancyGuard.SizeMultiplier);
            decision = decision with { PositionSizePercent = resizedPosition };
            request.Signal.Reasoning += $" | Expectancy Guard: {expectancyGuard.Reason}";
        }

        var symbolGuard = _symbolExpectancyService.Evaluate(recentTrades, request.Signal);
        if (!symbolGuard.Approved)
        {
            request.Signal.Status = TradeStatus.Rejected;
            request.Signal.Reasoning += $" | Symbol Guard: {symbolGuard.Reason}";
            await _signalRepository.UpdateAsync(request.Signal);
            _logger.LogWarning("❌ SYMBOL BLOCK: {Reason}", symbolGuard.Reason);
            return false;
        }

        if (symbolGuard.SizeMultiplier < 1.0m)
        {
            var symbolSized = Math.Max(0m, decision.PositionSizePercent * symbolGuard.SizeMultiplier);
            decision = decision with { PositionSizePercent = symbolSized };
            request.Signal.Reasoning += $" | Symbol Guard: {symbolGuard.Reason}";
        }

        var promotionGuard = _promotionService.Evaluate(request.Signal, decision, recentTrades);
        if (!promotionGuard.Approved)
        {
            request.Signal.Status = TradeStatus.Rejected;
            request.Signal.Reasoning += $" | Promotion Guard: {promotionGuard.Reason}";
            await _signalRepository.UpdateAsync(request.Signal);
            _logger.LogWarning("❌ PROMOTION BLOCK: {Reason}", promotionGuard.Reason);
            return false;
        }

        if (promotionGuard.SizeMultiplier < 1.0m)
        {
            var promotedSize = Math.Max(0m, decision.PositionSizePercent * promotionGuard.SizeMultiplier);
            decision = decision with { PositionSizePercent = promotedSize };
            request.Signal.Reasoning += $" | Promotion Guard: {promotionGuard.Reason}";
        }

        var portfolioGuard = await _riskAgent.EvaluatePortfolioGuardrailsAsync(
            recentTrades,
            request.Signal,
            decision.PositionSizePercent,
            riskParams);
        if (!portfolioGuard.Approved)
        {
            request.Signal.Status = TradeStatus.Rejected;
            request.Signal.Reasoning += $" | Risk Guard: {portfolioGuard.Reason}";
            await _signalRepository.UpdateAsync(request.Signal);
            _logger.LogWarning("❌ PORTFOLIO RISK BLOCK: {Reason}", portfolioGuard.Reason);
            return false;
        }

        // Per-trade risk sizing audit.
        var riskVote = await _riskAgent.EvaluateAsync(decision, riskParams, request.Signal.Volume24h);
        if (!riskVote.Approved)
        {
            request.Signal.Status = TradeStatus.Rejected;
            request.Signal.Reasoning += $" | Risk Veto: {riskVote.Reason}";
            await _signalRepository.UpdateAsync(request.Signal);
            _logger.LogWarning("❌ RISK VETO: {Reason}", riskVote.Reason);
            return false;
        }
        decision = decision with { PositionSizePercent = riskVote.SafePositionSizePercent };

        _logger.LogInformation("✅ APPROVED: Confidence={Confidence}/100 | SL={SL:F2} | TP={TP:F2} | Size={Size:P1}",
            decision.Confidence, decision.StopLoss, decision.TakeProfit, decision.PositionSizePercent);

        // ═══════════════════════════════════════════════════════
        // STEP 4: Optional AI Reasoning Double-Check
        // ═══════════════════════════════════════════════════════
        try
        {
            var aiApproved = await pipeline.ExecuteAsync(async ct =>
                await _aiService.ShouldExecuteAsync(request.Signal), cancellationToken);

            if (!aiApproved)
            {
                var aiReasoning = await _aiService.GetReasoningAsync(request.Signal);
                _logger.LogWarning("⚠️ AI override — REJECTED despite indicators. Reason: {Reason}", aiReasoning);
                request.Signal.Status = TradeStatus.Rejected;
                request.Signal.Reasoning += $" | AI Override: {aiReasoning}";
                await _signalRepository.UpdateAsync(request.Signal);
                return false;
            }
        }
        catch (Exception ex)
        {
            // AI failure is non-fatal — indicators already approved
            _logger.LogWarning(ex, "AI service unavailable. Proceeding with indicator approval only.");
        }

        // ═══════════════════════════════════════════════════════
        // STEP 5: Dispatch execution (sync or worker handoff)
        // ═══════════════════════════════════════════════════════
        if (_useWorkerHandoff)
        {
            request.Signal.Status = TradeStatus.Queued;
            request.Signal.Reasoning += " | Execution queued for worker.";
            await _signalRepository.UpdateAsync(request.Signal);
            _logger.LogInformation("📥 Trade queued for execution worker: {Symbol} ({Side})", request.Signal.Symbol, request.Signal.Side);
        }
        else
        {
            var executedTrade = await _mediator.Send(new ExecuteTradeCommand(request.Signal), cancellationToken);
            if (executedTrade == null)
            {
                request.Signal.Status = TradeStatus.Failed;
                request.Signal.Reasoning += " | Execution returned no trade result.";
                await _signalRepository.UpdateAsync(request.Signal);
                _logger.LogWarning("Trade execution returned no trade result for {Symbol} ({Side})", request.Signal.Symbol, request.Signal.Side);
                return false;
            }

            request.Signal.Status = executedTrade.Status;
            if (executedTrade.Status == TradeStatus.Failed)
            {
                request.Signal.Reasoning += " | Execution failed.";
                await _signalRepository.UpdateAsync(request.Signal);
                _logger.LogWarning("Trade execution failed for {Symbol} ({Side})", request.Signal.Symbol, request.Signal.Side);
                return false;
            }

            await _signalRepository.UpdateAsync(request.Signal);
            _logger.LogInformation("🚀 Trade dispatched for {Symbol} ({Side})", request.Signal.Symbol, request.Signal.Side);
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

    private static bool IsHftSignal(Signal signal)
        => signal.Source.Contains("HFT", StringComparison.OrdinalIgnoreCase) ||
           (signal.Metadata.TryGetValue("ExecutionMode", out var mode) &&
            mode.Contains("HFT", StringComparison.OrdinalIgnoreCase)) ||
           string.Equals(signal.StrategyTag, "HftMicroEdge", StringComparison.OrdinalIgnoreCase);
}
