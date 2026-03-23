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
    private readonly IHftMarketDataService _hftMarketDataService;
    private readonly RiskAgent _riskAgent;
    private readonly StrategyExpectancyService _expectancyService;
    private readonly SymbolExpectancyService _symbolExpectancyService;
    private readonly StrategyPromotionService _promotionService;
    private readonly EdgeScoreService _edgeScoreService;
    private readonly IHftSignalGenerationService _hftSignalGenerationService;
    private readonly ExitPolicyLearningService? _exitPolicyLearningService;
    private readonly IPortfolioStateCache _portfolioStateCache;
    private readonly bool _useWorkerHandoff;

    public ProcessSignalHandler(
        ILogger<ProcessSignalHandler> logger, 
        IMediator mediator, 
        IAiReasoningService aiService,
        IMarketDataService marketDataService,
        ISignalRepository signalRepository,
        ITradeRepository tradeRepository,
        ITechnicalAnalysisService technicalAnalysis,
        IHftMarketDataService? hftMarketDataService,
        RiskAgent riskAgent,
        StrategyExpectancyService expectancyService,
        SymbolExpectancyService symbolExpectancyService,
        StrategyPromotionService promotionService,
        EdgeScoreService? edgeScoreService,
        IConfiguration configuration,
        IPortfolioStateCache portfolioStateCache,
        IHftSignalGenerationService? hftSignalGenerationService = null,
        ExitPolicyLearningService? exitPolicyLearningService = null)
    {
        _logger = logger;
        _mediator = mediator;
        _aiService = aiService;
        _marketDataService = marketDataService;
        _signalRepository = signalRepository;
        _tradeRepository = tradeRepository;
        _technicalAnalysis = technicalAnalysis;
        _hftMarketDataService = hftMarketDataService ?? new HftMarketDataService(marketDataService);
        _riskAgent = riskAgent;
        _expectancyService = expectancyService;
        _symbolExpectancyService = symbolExpectancyService;
        _promotionService = promotionService;
        _edgeScoreService = edgeScoreService ?? new EdgeScoreService();
        _hftSignalGenerationService = hftSignalGenerationService ?? new HftSignalGenerationService(technicalAnalysis);
        _exitPolicyLearningService = exitPolicyLearningService;
        _portfolioStateCache = portfolioStateCache;
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
            IsHftSignal(request.Signal)
                ? await _hftMarketDataService.GetCandlesAsync(request.Signal, market, ct)
                : await _marketDataService.GetCandlesAsync(request.Signal.Symbol, 100, market), cancellationToken);

        // 🚀 HFT PIPELINE: Zero-allocation $O(1)$ lock-free Ram pointers natively replacing EF Core querying 
        var recentTrades = _portfolioStateCache.GetRecentTrades();
        _exitPolicyLearningService?.RecordLiveState(recentTrades);

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
        request.Signal.Metadata["ExitPolicyTag"] = decision.ExitPolicyTag;

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
        request.Signal.Metadata["PromotionStage"] = promotionGuard.PromotionStage;
        request.Signal.Metadata["ExecutionMode"] = "REAL";
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

        var edgeScore = _edgeScoreService.Evaluate(
            request.Signal,
            decision,
            expectancyGuard,
            symbolGuard,
            promotionGuard,
            portfolioGuard,
            riskVote);

        request.Signal.Metadata["EdgeScore"] = decimal.Round(edgeScore.AdjustedEdgeScore, 2).ToString("F2");
        request.Signal.Metadata["EdgeBand"] = edgeScore.DecisionBand;
        request.Signal.Reasoning += $" | Edge Score: {edgeScore.AdjustedEdgeScore:F1}/100 [{edgeScore.DecisionBand}]";

        if (!edgeScore.Approved)
        {
            request.Signal.Status = TradeStatus.Rejected;
            await _signalRepository.UpdateAsync(request.Signal);
            _logger.LogWarning(
                "❌ EDGE SCORE BLOCK: Adjusted={Adjusted:F1} Execution={Execution:F1} Risk={Risk:F1}",
                edgeScore.AdjustedEdgeScore,
                edgeScore.ExecutionEdge,
                edgeScore.RiskContext);
            return false;
        }

        if (edgeScore.RecommendedSizeMultiplier < 1.0m)
        {
            decision = decision with
            {
                PositionSizePercent = Math.Max(0m, decision.PositionSizePercent * edgeScore.RecommendedSizeMultiplier)
            };
        }

        var finalAllocatorBias = _promotionService.EvaluateFinalAllocatorBias(request.Signal, decision, recentTrades);
        if (finalAllocatorBias.SizeMultiplier != 1.0m)
        {
            decision = decision with
            {
                PositionSizePercent = Math.Max(0.001m, decision.PositionSizePercent * finalAllocatorBias.SizeMultiplier)
            };
            request.Signal.Reasoning += $" | Final Allocator: {finalAllocatorBias.Reason}";
        }
        request.Signal.Metadata["FinalAllocatorBias"] = finalAllocatorBias.SizeMultiplier.ToString("F2");
        request.Signal.Metadata["FinalAllocatorScope"] = finalAllocatorBias.Scope;
        request.Signal.Metadata["VenueExecutionBias"] = finalAllocatorBias.VenueExecutionBias.ToString("F2");
        request.Signal.Metadata["VenueExecutionScope"] = finalAllocatorBias.VenueExecutionScope;
        request.Signal.Metadata["VenueImpactLabel"] = ResolveVenueImpactLabel(finalAllocatorBias.VenueExecutionBias);
        var exactBucketPressure = ResolveExactBucketPressureLabel(request.Signal.Symbol, request.Signal.StrategyTag, request.Signal.RegimeTag, recentTrades);
        request.Signal.Metadata["ExactBucketImpactLabel"] = exactBucketPressure.Impact;
        request.Signal.Metadata["ExactBucketSummary"] = exactBucketPressure.Summary;
        var concordance = ResolveConcordanceLabel(
            exactBucketPressure.Impact,
            exactBucketPressure.Summary,
            finalAllocatorBias.VenueExecutionBias,
            finalAllocatorBias.VenueExecutionScope,
            request.Signal.Symbol,
            request.Signal.StrategyTag,
            request.Signal.RegimeTag);
        request.Signal.Metadata["ConcordanceImpactLabel"] = concordance.Impact;
        request.Signal.Metadata["ConcordanceSummary"] = concordance.Summary;
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

    private static string ResolveVenueImpactLabel(decimal venueExecutionBias)
    {
        if (venueExecutionBias <= 0.93m)
        {
            return $"Venue hurt size hard ({venueExecutionBias:F2}x)";
        }

        if (venueExecutionBias <= 0.98m)
        {
            return $"Venue capped size ({venueExecutionBias:F2}x)";
        }

        if (venueExecutionBias >= 1.02m)
        {
            return $"Venue helped size ({venueExecutionBias:F2}x)";
        }

        return "Venue neutral";
    }

    private static (string Impact, string Summary) ResolveConcordanceLabel(
        string exactBucketImpact,
        string exactBucketSummary,
        decimal venueExecutionBias,
        string venueExecutionScope,
        string symbol,
        string strategyTag,
        string regimeTag)
    {
        var venueLabel = ResolveVenueImpactLabel(venueExecutionBias);
        var scopeText = string.IsNullOrWhiteSpace(venueExecutionScope) ? "default venue scope" : venueExecutionScope;
        var venueSummary = $"{venueLabel}. Scope: {scopeText}. {symbol} {strategyTag}/{regimeTag}.";
        var exactSupportsScaling = exactBucketImpact.Contains("supports scaling", StringComparison.OrdinalIgnoreCase);
        var exactNearPaper = exactBucketImpact.Contains("near Paper", StringComparison.OrdinalIgnoreCase);
        var exactLiveProbe = exactBucketImpact.Contains("LiveProbe", StringComparison.OrdinalIgnoreCase);
        var venueSupportsScaling = venueExecutionBias >= 1.02m;
        var venueWeak = venueExecutionBias <= 0.98m;
        var venueSeverelyWeak = venueExecutionBias <= 0.93m;

        if (exactSupportsScaling && venueSupportsScaling)
        {
            return (
                "Concordance supports scaling",
                $"Exact bucket edge and venue execution are aligned for scaled deployment. {exactBucketSummary} {venueSummary}");
        }

        if ((exactNearPaper || exactLiveProbe) && venueSeverelyWeak)
        {
            return (
                "Concordance is capping near Paper",
                $"Exact bucket pressure and venue execution are both soft, so live size should stay heavily capped. {exactBucketSummary} {venueSummary}");
        }

        if (exactSupportsScaling && venueWeak)
        {
            return (
                "Venue is capping real edge",
                $"The exact bucket is proving edge, but venue execution is undermining that edge live. {exactBucketSummary} {venueSummary}");
        }

        if ((exactNearPaper || exactLiveProbe) && venueSupportsScaling)
        {
            return (
                "Edge is unproven despite clean venue",
                $"Venue execution is supportive, but the exact bucket is still not proving enough edge to scale. {exactBucketSummary} {venueSummary}");
        }

        if (exactLiveProbe && venueWeak)
        {
            return (
                "Concordance is holding LiveProbe",
                $"Both edge quality and venue execution are leaning defensive, so probe sizing is still the honest path. {exactBucketSummary} {venueSummary}");
        }

        return (
            "Concordance is mixed",
            $"Edge quality and venue execution are sending mixed signals, so keep deployment disciplined. {exactBucketSummary} {venueSummary}");
    }

    private static (string Impact, string Summary) ResolveExactBucketPressureLabel(
        string symbol,
        string strategyTag,
        string regimeTag,
        IReadOnlyList<Trade> trades)
    {
        var exactTrades = trades
            .Where(trade => trade.Status == TradeStatus.Executed || trade.Status == TradeStatus.Failed)
            .Where(trade => string.Equals(trade.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            .Where(trade => string.Equals(trade.StrategyTag, strategyTag, StringComparison.OrdinalIgnoreCase))
            .Where(trade => string.Equals(trade.RegimeTag, regimeTag, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exactTrades.Count == 0)
        {
            return ("Exact bucket pressure unconfirmed", "No exact bucket pressure signal has enough stored depth yet.");
        }

        var pnls = exactTrades
            .Select(trade => trade.ExitTime.HasValue ? (trade.ExitPrice - trade.EntryPrice) * trade.Quantity * (trade.Side == TradeSide.Sell ? -1 : 1) : 0m)
            .ToList();
        var wins = pnls.Count(pnl => pnl > 0);
        var grossProfit = pnls.Where(pnl => pnl > 0).Sum();
        var grossLoss = Math.Abs(pnls.Where(pnl => pnl < 0).Sum());
        var profitFactor = grossLoss > 0 ? (double)(grossProfit / grossLoss) : grossProfit > 0 ? 99 : 0;
        var expectedValue = pnls.Count == 0 ? 0m : pnls.Average();
        var winRate = exactTrades.Count == 0 ? 0 : wins * 100.0 / exactTrades.Count;
        var highEdgePnls = exactTrades
            .Where(trade => string.Equals(trade.EdgeBand, "Full", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(trade.EdgeBand, "Reduced", StringComparison.OrdinalIgnoreCase) ||
                            trade.EdgeScore >= 70m)
            .Select(trade => trade.ExitTime.HasValue ? (trade.ExitPrice - trade.EntryPrice) * trade.Quantity * (trade.Side == TradeSide.Sell ? -1 : 1) : 0m)
            .ToList();
        var highEdgeWinRate = highEdgePnls.Count == 0 ? 0 : highEdgePnls.Count(value => value > 0) * 100.0 / highEdgePnls.Count;
        var highEdgeGrossProfit = highEdgePnls.Where(pnl => pnl > 0).Sum();
        var highEdgeGrossLoss = Math.Abs(highEdgePnls.Where(pnl => pnl < 0).Sum());
        var highEdgeProfitFactor = highEdgeGrossLoss > 0 ? (double)(highEdgeGrossProfit / highEdgeGrossLoss) : highEdgeGrossProfit > 0 ? 99 : 0;
        var highEdgeExpectedValue = highEdgePnls.Count == 0 ? 0m : highEdgePnls.Average();

        if (highEdgePnls.Count >= 3 &&
            (highEdgeExpectedValue <= 0m || highEdgeProfitFactor < 1.05 || highEdgeWinRate < 46))
        {
            return ("Exact bucket is capping near Paper", $"High-edge exact outcomes are failing: PF {highEdgeProfitFactor:F2}, WR {highEdgeWinRate:F1}%, EV {highEdgeExpectedValue:F2}.");
        }

        if (expectedValue <= 0m || profitFactor < 0.95 || winRate < 42)
        {
            return ("Exact bucket is capping near Paper", $"Exact bucket is underperforming: PF {profitFactor:F2}, WR {winRate:F1}%, EV {expectedValue:F2}.");
        }

        if (profitFactor < 1.12 || winRate < 49 || expectedValue <= 0m)
        {
            return ("Exact bucket is holding LiveProbe", $"Exact bucket is still soft: PF {profitFactor:F2}, WR {winRate:F1}%, EV {expectedValue:F2}.");
        }

        if (highEdgePnls.Count >= 4 && highEdgeExpectedValue > 0m && highEdgeProfitFactor >= 1.35 && highEdgeWinRate >= 56)
        {
            return ("Exact bucket supports scaling", $"High-edge exact outcomes support scale: PF {highEdgeProfitFactor:F2}, WR {highEdgeWinRate:F1}%, EV {highEdgeExpectedValue:F2}.");
        }

        if (expectedValue > 0m && profitFactor >= 1.22 && winRate >= 52)
        {
            return ("Exact bucket supports scaling", $"Exact bucket is healthy: PF {profitFactor:F2}, WR {winRate:F1}%, EV {expectedValue:F2}.");
        }

        return ("Exact bucket is still proving itself", $"Exact bucket is still proving itself: PF {profitFactor:F2}, WR {winRate:F1}%, EV {expectedValue:F2}.");
    }
}

