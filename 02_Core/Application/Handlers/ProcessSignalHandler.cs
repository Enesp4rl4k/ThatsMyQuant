using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Application.Resilience;
using ThatsMyQuant.Application.Observability;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;

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
    private readonly FeatureEngineeringAgent? _featureAgent;
    private readonly OperationalTelemetry _telemetry;
    private readonly bool _useWorkerHandoff;
    private readonly IConfiguration _configuration;
    private readonly bool _edgeEnabled;
    private readonly EdgeScoreService? _edgeScore;
    private readonly StrategyExpectancyService? _expectancy;
    private readonly SymbolExpectancyService? _symbolExpectancy;
    private readonly StrategyPromotionService? _promotion;
    private readonly IPortfolioStateCache? _portfolioCache;
    private readonly RiskAgent? _riskAgent;
    private readonly ProfitLeakAuditService? _profitLeakAudit;

    public ProcessSignalHandler(
        ILogger<ProcessSignalHandler> logger,
        IMediator mediator,
        IMarketDataService marketDataService,
        ISignalRepository signalRepository,
        ITechnicalAnalysisService technicalAnalysis,
        IEnumerable<ISignalValidator> validators,
        IAgentCoordinator agentCoordinator,
        IConfiguration configuration,
        FeatureEngineeringAgent? featureAgent = null,
        OperationalTelemetry? telemetry = null,
        EdgeScoreService? edgeScoreService = null,
        StrategyExpectancyService? expectancyService = null,
        SymbolExpectancyService? symbolExpectancyService = null,
        StrategyPromotionService? promotionService = null,
        IPortfolioStateCache? portfolioCache = null,
        RiskAgent? riskAgent = null,
        ProfitLeakAuditService? profitLeakAudit = null)
    {
        _logger = logger;
        _mediator = mediator;
        _marketDataService = marketDataService;
        _signalRepository = signalRepository;
        _technicalAnalysis = technicalAnalysis;
        _validators = validators.OrderBy(v => v.Order).ToArray();
        _agentCoordinator = agentCoordinator;
        _configuration = configuration;
        _telemetry = telemetry ?? new OperationalTelemetry();
        _featureAgent = featureAgent;
        _edgeScore = edgeScoreService;
        _expectancy = expectancyService;
        _symbolExpectancy = symbolExpectancyService;
        _promotion = promotionService;
        _portfolioCache = portfolioCache;
        _riskAgent = riskAgent;
        _profitLeakAudit = profitLeakAudit;
        _edgeEnabled = bool.TryParse(configuration["Edge:Enabled"], out var edgeOn) && edgeOn;
        _useWorkerHandoff = bool.TryParse(configuration["Execution:UseWorkerHandoff"], out var useWorkerHandoff)
            && useWorkerHandoff;
    }

    public async Task<bool> Handle(ProcessSignalCommand request, CancellationToken cancellationToken)
    {
        using var logScope = TradingLogScope.Begin(_logger, request.Signal);
        LogSignalStart(request.Signal);

        // 0. Persist Signal immediately (audit trail)
        await _signalRepository.AddAsync(request.Signal);

        // 0.5: Modular Validation Chain (Volume, Price, Market Hours, etc.)
        foreach (var validator in _validators)
        {
            var (isValid, failureReason) = await validator.ValidateAsync(request.Signal);
            if (!isValid)
            {
                request.Signal.Status = TradeStatus.Rejected;
                request.Signal.Reasoning = failureReason;
                await _signalRepository.UpdateAsync(request.Signal);
                _telemetry.RecordSignalRejected(request.Signal.Symbol);
                _logger.LogWarning("❌ Signal Validation Failed: {Reason}", failureReason);
                return false;
            }
        }

        var pipeline = ResiliencePolicies.CreateRetryPipeline(_logger);

        // STEP 1: Fetch Candle Data
        var market = request.Signal.Symbol.Contains('-') || request.Signal.MarketType == MarketType.Crypto
            ? "Crypto"
            : request.Signal.MarketType.ToString();
        var candles = await pipeline.ExecuteAsync(async ct =>
            await _marketDataService.GetCandlesAsync(request.Signal.Symbol, 100, market), cancellationToken);

        // Warm FeatureEngineeringAgent with historical closes so ML tensor isn't zero on first inference
        if (_featureAgent != null && candles.Count > 0)
        {
            foreach (var c in candles)
                _featureAgent.ProcessTickNatively((double)c.Close);
        }

        // STEP 1.5: Detect Market Regime — Adapt Risk Parameters
        var (regime, structure, riskParams) = AnalyzeMarketRegime(candles, request.Signal);

        // STEP 1.7: Build Foundation (Tier 1) and Pattern (Tier 2) snapshots
        var foundation = await _agentCoordinator.BuildFoundationAsync([], candles, cancellationToken);
        var patterns = await _agentCoordinator.BuildPatternAsync(request.Signal, foundation, candles, cancellationToken);
        
        // STEP 2: Technical Analysis — Indicators + Confluence
        var (decision, analysisSuccess) = await PerformTechnicalAnalysisAsync(request.Signal, candles, riskParams);
        if (!analysisSuccess) return false;

        // STEP 3: Swarm Orchestration Decision
        var (finalDecision, swarmApproved, riskParamsForEdge) = await NegotiateWithSwarmAsync(
            request.Signal, decision, riskParams, candles, foundation, patterns, regime, cancellationToken);
        if (!swarmApproved) return false;

        if (_edgeEnabled)
        {
            var edgeOutcome = await ApplyEdgeScoreMetadataAsync(request.Signal, finalDecision, riskParamsForEdge);
            if (!edgeOutcome.ShouldContinue)
                return false;
            finalDecision = edgeOutcome.Decision;
        }

        // STEP 5: Dispatch execution (sync or worker handoff)
        return await DispatchExecutionAsync(request.Signal, finalDecision, cancellationToken);
    }

    private void LogSignalStart(Signal signal)
    {
        _logger.LogInformation("═══════════════════════════════════════════════════════");
        _logger.LogInformation("Processing Signal: {Id} | {Symbol} | {Side} | ${Price}",
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
            _telemetry.RecordTradeFailed(signal.Symbol);
            return (null!, false);
        }
    }

    private async Task<(TradeDecision decision, bool approved, RiskParameters riskParams)> NegotiateWithSwarmAsync(
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
            _telemetry.RecordSignalRejected(signal.Symbol);
            _logger.LogWarning("❌ SWARM REJECTED: {Flags}", swarmResult.RejectionFlags);
            _logger.LogInformation("═══════════════════════════════════════════════════════");
            return (decision, false, riskParams);
        }

        var adjustedDecision = decision with 
        { 
            PositionSizePercent = swarmResult.AdjustedKellyFraction,
            TakeProfit = swarmResult.AdjustedTakeProfit
        };
        signal.ExecutionAction = swarmResult.ExecutionAction;
        signal.Metadata["RequestedPositionFraction"] = adjustedDecision.PositionSizePercent.ToString(CultureInfo.InvariantCulture);
        signal.Metadata["StopLoss"] = adjustedDecision.StopLoss.ToString(CultureInfo.InvariantCulture);
        signal.Metadata["TakeProfit"] = adjustedDecision.TakeProfit.ToString(CultureInfo.InvariantCulture);
        signal.Metadata["PartialTakeProfit"] = adjustedDecision.PartialTakeProfit.ToString(CultureInfo.InvariantCulture);
        signal.Metadata["PartialExitFraction"] = adjustedDecision.PartialExitFraction.ToString(CultureInfo.InvariantCulture);
        signal.Metadata["BreakevenTriggerFraction"] = adjustedDecision.BreakevenTriggerFraction.ToString(CultureInfo.InvariantCulture);
        signal.Metadata["MaxHoldingBars"] = adjustedDecision.MaxHoldingBars.ToString(CultureInfo.InvariantCulture);
        signal.Metadata["ExitPolicyTag"] = adjustedDecision.ExitPolicyTag;
        signal.Metadata["RequestedQuantity"] = signal.Quantity.ToString(CultureInfo.InvariantCulture);

        if (signal.Price > 0)
        {
            signal.Quantity = Math.Round((riskParams.StartingEquity * adjustedDecision.PositionSizePercent) / signal.Price, 4);
            signal.Metadata["RequestedQuantity"] = signal.Quantity.ToString(CultureInfo.InvariantCulture);
        }

        _logger.LogInformation("✅ SWARM APPROVED: Confidence={Confidence}/100 | SL={SL:F2} | TP={TP:F2} | Size={Size:P1} | Action={Action} | Qty={Qty}",
            adjustedDecision.Confidence, adjustedDecision.StopLoss, adjustedDecision.TakeProfit, adjustedDecision.PositionSizePercent, signal.ExecutionAction, signal.Quantity);

        return (adjustedDecision, true, riskParams);
    }

    private readonly record struct EdgeApplyOutcome(bool ShouldContinue, TradeDecision Decision);

    private async Task<EdgeApplyOutcome> ApplyEdgeScoreMetadataAsync(
        Signal signal,
        TradeDecision decision,
        RiskParameters riskParams)
    {
        if (_edgeScore == null || _portfolioCache == null || _riskAgent == null || _expectancy == null || _symbolExpectancy == null || _promotion == null)
        {
            throw new InvalidOperationException(
                "Edge:Enabled=true requires EdgeScoreService, IPortfolioStateCache, RiskAgent, StrategyExpectancyService, SymbolExpectancyService, and StrategyPromotionService.");
        }

        var recentTrades = _portfolioCache.GetRecentTrades();
        var portfolioGuard = await _riskAgent.EvaluatePortfolioGuardrailsAsync(
            recentTrades, signal, decision.PositionSizePercent, riskParams);
        var riskVote = await _riskAgent.EvaluateAsync(decision, riskParams, signal.Volume24h, recentTrades, signal);

        var expectancy = _expectancy.Evaluate(recentTrades, signal, decision);
        var symbol = _symbolExpectancy.Evaluate(recentTrades, signal);
        var promotion = _promotion.Evaluate(signal, decision, recentTrades);

        var guards = new EvaluationGuards(expectancy, symbol, promotion, portfolioGuard, riskVote);
        var pipelineKind = ParseEdgePipelineKind(_configuration);
        var options = new EdgeScoreEvaluationOptions(pipelineKind, GetProbeSimulatedMultiplier());

        var breakdown = _edgeScore.Evaluate(signal, decision, guards, options);

        signal.Metadata["EdgeFinalScore"] = breakdown.FinalEdgeScore.ToString(CultureInfo.InvariantCulture);
        signal.Metadata["EdgeAdjustedScore"] = breakdown.AdjustedEdgeScore.ToString(CultureInfo.InvariantCulture);
        signal.Metadata["EdgeScore"] = breakdown.AdjustedEdgeScore.ToString(CultureInfo.InvariantCulture);
        signal.Metadata["EdgeBand"] = breakdown.DecisionBand;
        signal.Metadata["EdgeReasons"] = string.Join(" | ", breakdown.Reasons);
        signal.Metadata["EdgeRecommendedSizeMultiplier"] = breakdown.RecommendedSizeMultiplier.ToString(CultureInfo.InvariantCulture);
        signal.Metadata["EdgePipelineKind"] = pipelineKind.ToString();
        if (decision.Indicators?.LinearRegression is { } regression)
        {
            signal.Metadata["LinearRegressionLookback"] = regression.Lookback.ToString(CultureInfo.InvariantCulture);
            signal.Metadata["LinearRegressionSlope"] = regression.Slope.ToString(CultureInfo.InvariantCulture);
            signal.Metadata["LinearRegressionNormalizedSlope"] = regression.NormalizedSlope.ToString(CultureInfo.InvariantCulture);
            signal.Metadata["LinearRegressionRSquared"] = regression.RSquared.ToString(CultureInfo.InvariantCulture);
            signal.Metadata["LinearRegressionSigmaDistance"] = regression.SigmaDistance.ToString(CultureInfo.InvariantCulture);
            signal.Metadata["LinearRegressionRegressionValue"] = regression.RegressionValue.ToString(CultureInfo.InvariantCulture);
            signal.Metadata["LinearRegressionResidual"] = regression.Residual.ToString(CultureInfo.InvariantCulture);
            signal.Metadata["LinearRegressionOverextended"] = regression.IsOverextendedAbove.ToString(CultureInfo.InvariantCulture);
        }

        if (!breakdown.Approved && pipelineKind == EdgePipelineKind.Live)
        {
            signal.Status = TradeStatus.Rejected;
            signal.Reasoning += $" | Edge gate: {breakdown.DecisionBand} (adj {breakdown.AdjustedEdgeScore:F1}).";
            await _signalRepository.UpdateAsync(signal);
            _telemetry.RecordSignalRejected(signal.Symbol);
            _logger.LogWarning(
                "Edge gate rejected {Symbol}: band={Band}, adj={Adj:F1}",
                signal.Symbol, breakdown.DecisionBand, breakdown.AdjustedEdgeScore);
            return new EdgeApplyOutcome(false, decision);
        }

        var scaled = ApplyApprovedSizeCaps(signal, decision, riskParams, breakdown, riskVote.SafePositionSizePercent);
        var profitLeakOutcome = await ApplyProfitLeakAuditAsync(signal, decision, scaled, riskParams, recentTrades);
        if (!profitLeakOutcome.ShouldContinue)
            return new EdgeApplyOutcome(false, profitLeakOutcome.Decision);
        scaled = profitLeakOutcome.Decision;

        _logger.LogInformation(
            "Edge score: band={Band}, adj={Adj:F1}, approved={Approved}, pipeline={Pipeline}",
            breakdown.DecisionBand, breakdown.AdjustedEdgeScore, breakdown.Approved, pipelineKind);

        return new EdgeApplyOutcome(true, scaled);
    }

    private readonly record struct ProfitLeakApplyOutcome(bool ShouldContinue, TradeDecision Decision);

    private async Task<ProfitLeakApplyOutcome> ApplyProfitLeakAuditAsync(
        Signal signal,
        TradeDecision originalDecision,
        TradeDecision currentDecision,
        RiskParameters riskParams,
        IReadOnlyList<Trade> recentTrades)
    {
        if (_profitLeakAudit is null)
            return new ProfitLeakApplyOutcome(true, currentDecision);

        var audit = _profitLeakAudit.Evaluate(signal, originalDecision, currentDecision, recentTrades);
        var worstBucket = ResolveWorstProfitLeakBucket(audit);

        signal.Metadata["ProfitLeakAuditSummary"] = audit.Summary;
        signal.Metadata["ProfitLeakWorstBucketLabel"] = worstBucket.Label;
        signal.Metadata["ProfitLeakWorstBucketBeforeLiveChange"] = BuildProfitLeakBucketDisplay(worstBucket);
        signal.Metadata["ProfitLeakWorstBucketAfterLiveChange"] = BuildProfitLeakAfterLiveChangeDisplay(audit, worstBucket);
        signal.Metadata["ProfitLeakExactNetExpectancy"] = audit.ExactBucket.ExpectedValue.ToString(CultureInfo.InvariantCulture);
        signal.Metadata["ProfitLeakExactProfitFactor"] = audit.ExactBucket.ProfitFactor.ToString(CultureInfo.InvariantCulture);
        signal.Metadata["ProfitLeakExitPolicyNetExpectancy"] = audit.ExitPolicyBucket.ExpectedValue.ToString(CultureInfo.InvariantCulture);
        signal.Metadata["ProfitLeakRegimeNetExpectancy"] = audit.RegimeBucket.ExpectedValue.ToString(CultureInfo.InvariantCulture);
        signal.Metadata["ProfitLeakVenueNetExpectancy"] = audit.VenueBucket.ExpectedValue.ToString(CultureInfo.InvariantCulture);
        signal.Metadata["ProfitLeakReducedSizeNetExpectancy"] = audit.ReducedSizeBucket.ExpectedValue.ToString(CultureInfo.InvariantCulture);
        signal.Metadata["ProfitLeakReducedSizeNoise"] = (!audit.Approved && audit.CurrentTradeIsReducedSize && audit.ReducedSizeBucket.SampleSize >= 4).ToString(CultureInfo.InvariantCulture);
        signal.Metadata["ProfitLeakLoserClusterRate"] = audit.LoserCluster.DegradedExecutionLossRate.ToString(CultureInfo.InvariantCulture);
        signal.Metadata["ProfitLeakDominantLosingExitPolicy"] = audit.LoserCluster.DominantExitPolicy;

        if (!audit.Approved)
        {
            signal.Status = TradeStatus.Rejected;
            signal.Reasoning += $" | Profit leak audit: {audit.Summary}";
            await _signalRepository.UpdateAsync(signal);
            _telemetry.RecordSignalRejected(signal.Symbol);
            _logger.LogWarning("Profit leak audit rejected {Symbol}: {Summary}", signal.Symbol, audit.Summary);
            return new ProfitLeakApplyOutcome(false, currentDecision with { ShouldTrade = false, Reasoning = $"{currentDecision.Reasoning} | {audit.Summary}" });
        }

        if (audit.SizeMultiplier is > 0m and < 1m)
        {
            return new ProfitLeakApplyOutcome(true, ApplyTradeDecisionSizeMultiplier(signal, currentDecision, riskParams, audit.SizeMultiplier, audit.Summary));
        }

        return new ProfitLeakApplyOutcome(true, currentDecision);
    }

    private static TradeDecision ApplyApprovedSizeCaps(
        Signal signal,
        TradeDecision decision,
        RiskParameters riskParams,
        EdgeScoreBreakdown breakdown,
        decimal safePositionSizePercent)
    {
        if (!breakdown.Approved)
            return decision;

        var originalSize = decision.PositionSizePercent;
        var cappedSize = originalSize;

        if (safePositionSizePercent > 0m)
            cappedSize = cappedSize <= 0m ? safePositionSizePercent : Math.Min(cappedSize, safePositionSizePercent);

        if (breakdown.RecommendedSizeMultiplier is > 0m and < 1m && originalSize > 0m)
            cappedSize = Math.Min(cappedSize, originalSize * breakdown.RecommendedSizeMultiplier);

        if (cappedSize == originalSize)
            return decision;

        var scaled = decision with { PositionSizePercent = cappedSize };
        signal.Metadata["RequestedPositionFraction"] = scaled.PositionSizePercent.ToString(CultureInfo.InvariantCulture);
        signal.Metadata["RiskSafePositionFraction"] = safePositionSizePercent.ToString(CultureInfo.InvariantCulture);

        if (signal.Price > 0)
        {
            signal.Quantity = Math.Round((riskParams.StartingEquity * scaled.PositionSizePercent) / signal.Price, 4);
            signal.Metadata["RequestedQuantity"] = signal.Quantity.ToString(CultureInfo.InvariantCulture);
        }

        return scaled;
    }

    private static ProfitLeakBucketSummary ResolveWorstProfitLeakBucket(ProfitLeakAuditResult audit)
    {
        return new[]
        {
            audit.ExactBucket,
            audit.ExitPolicyBucket,
            audit.RegimeBucket,
            audit.VenueBucket,
            audit.ReducedSizeBucket
        }
        .OrderBy(bucket => bucket.SampleSize == 0 ? decimal.MaxValue : bucket.ExpectedValue)
        .ThenBy(bucket => bucket.SampleSize == 0 ? double.MaxValue : bucket.ProfitFactor)
        .ThenByDescending(bucket => bucket.SampleSize)
        .First();
    }

    private static string BuildProfitLeakBucketDisplay(ProfitLeakBucketSummary bucket)
    {
        if (bucket.SampleSize <= 0)
            return $"{bucket.Label}: no closed samples yet.";

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}: EV {1:F2}, PF {2:F2}, WR {3:F1}%, n={4}",
            bucket.Label,
            bucket.ExpectedValue,
            bucket.ProfitFactor,
            bucket.WinRate,
            bucket.SampleSize);
    }

    private static string BuildProfitLeakAfterLiveChangeDisplay(ProfitLeakAuditResult audit, ProfitLeakBucketSummary worstBucket)
    {
        var before = BuildProfitLeakBucketDisplay(worstBucket);

        if (!audit.Approved)
            return $"Blocked after live change. {before}";

        if (audit.SizeMultiplier is > 0m and < 1m)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Reduced to {0:F2}x after live change. {1}",
                audit.SizeMultiplier,
                before);
        }

        return $"No leak-driven live change applied. {before}";
    }

    private static TradeDecision ApplyTradeDecisionSizeMultiplier(
        Signal signal,
        TradeDecision decision,
        RiskParameters riskParams,
        decimal multiplier,
        string summary)
    {
        var scaledSize = Math.Max(0.001m, decision.PositionSizePercent * multiplier);
        if (scaledSize >= decision.PositionSizePercent)
            return decision;

        var scaled = decision with
        {
            PositionSizePercent = scaledSize,
            Reasoning = $"{decision.Reasoning} | Profit leak overlay {multiplier:F2}x: {summary}"
        };

        signal.Metadata["RequestedPositionFraction"] = scaled.PositionSizePercent.ToString(CultureInfo.InvariantCulture);
        signal.Metadata["ProfitLeakAuditSizeMultiplier"] = multiplier.ToString(CultureInfo.InvariantCulture);
        if (signal.Price > 0)
        {
            signal.Quantity = Math.Round((riskParams.StartingEquity * scaled.PositionSizePercent) / signal.Price, 4);
            signal.Metadata["RequestedQuantity"] = signal.Quantity.ToString(CultureInfo.InvariantCulture);
        }

        return scaled;
    }

    private static EdgePipelineKind ParseEdgePipelineKind(IConfiguration configuration)
    {
        var raw = configuration["Edge:PipelineKind"];
        if (string.IsNullOrWhiteSpace(raw))
            return EdgePipelineKind.Live;
        return Enum.TryParse<EdgePipelineKind>(raw, ignoreCase: true, out var k) ? k : EdgePipelineKind.Live;
    }

    private decimal GetProbeSimulatedMultiplier()
    {
        if (decimal.TryParse(_configuration["Edge:ProbeSimulatedMultiplier"], NumberStyles.Any, CultureInfo.InvariantCulture, out var m) && m > 0m && m <= 1m)
            return m;
        return 0.45m;
    }

    private async Task<bool> DispatchExecutionAsync(Signal signal, TradeDecision decision, CancellationToken ct)
    {
        if (_useWorkerHandoff)
        {
            signal.Status = TradeStatus.Queued;
            signal.Reasoning += " | Execution queued for worker.";
            await _signalRepository.UpdateAsync(signal);
            _telemetry.RecordSignalQueued(signal.Symbol);
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
                _telemetry.RecordTradeFailed(signal.Symbol);
                _logger.LogWarning("Trade execution failed or empty for {Symbol} ({Side})", signal.Symbol, signal.Side);
                return false;
            }

            signal.Status = executedTrade.Status;
            await _signalRepository.UpdateAsync(signal);
            if (executedTrade.ExecutionLatencyMs > 0)
            {
                _telemetry.RecordTradeSucceeded(executedTrade.ExecutionLatencyMs, signal.Symbol);
            }
            _logger.LogInformation("🚀 Trade dispatched for {Symbol} ({Side})", signal.Symbol, signal.Side);
        }
        _logger.LogInformation("═══════════════════════════════════════════════════════");
        return true;
    }
}
