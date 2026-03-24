using System.Threading;
using System.Threading.Tasks;
using MediatR;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using ThatsMyQuant.Domain.ValueObjects;
using ThatsMyQuant.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;

namespace ThatsMyQuant.Application.Strategy;

public class SwarmNegotiationRequest : IRequest<SwarmConsensusResult>
{
    public Signal Signal { get; set; } = null!;
    public TradeDecision InitialDecision { get; set; } = null!;
    public RiskParameters Risk { get; set; } = null!;
    public IReadOnlyList<Candle> Candles { get; set; } = null!;
}

public class SwarmConsensusResult
{
    public bool Approved { get; set; }
    public SignalReasonFlags RejectionFlags { get; set; } = SignalReasonFlags.None;
    public ExecutionActionType ExecutionAction { get; set; } = ExecutionActionType.None;
    public decimal AdjustedKellyFraction { get; set; }
    public decimal AdjustedTakeProfit { get; set; }
}

public class SwarmOrchestrator : IRequestHandler<SwarmNegotiationRequest, SwarmConsensusResult>
{
    private readonly RiskAgent _riskAgent;
    private readonly SentimentAgent _sentimentAgent;
    private readonly InferenceAgent _inferenceAgent;
    private readonly FeatureEngineeringAgent _featureAgent;
    private readonly IPortfolioStateCache _portfolioCache;
    private readonly OFIExecutionAgent _ofiAgent;
    private readonly ILogger<SwarmOrchestrator> _logger;

    private const float MlDownsideThreshold = -0.01f;
    private const float SuccessThreshold = 0.6f;

    public SwarmOrchestrator(
        RiskAgent riskAgent, 
        SentimentAgent sentimentAgent, 
        InferenceAgent inferenceAgent,
        FeatureEngineeringAgent featureAgent,
        IPortfolioStateCache portfolioCache,
        OFIExecutionAgent ofiAgent,
        ILogger<SwarmOrchestrator> logger)
    {
        _riskAgent = riskAgent;
        _sentimentAgent = sentimentAgent;
        _inferenceAgent = inferenceAgent;
        _featureAgent = featureAgent;
        _portfolioCache = portfolioCache;
        _ofiAgent = ofiAgent;
        _logger = logger;
    }

    public async Task<SwarmConsensusResult> Handle(SwarmNegotiationRequest request, CancellationToken cancellationToken)
    {
        var result = new SwarmConsensusResult { Approved = false };

        try
        {
            var regime = MarketRegimeDetector.Detect(request.Candles);
            var riskParams = ResolveRegimeAdjustedRisk(request.Risk, regime);

            var portfolioGuard = await _riskAgent.EvaluatePortfolioGuardrailsAsync(
                _portfolioCache.GetRecentTrades(),
                request.Signal,
                request.InitialDecision.PositionSizePercent,
                riskParams);

            if (!portfolioGuard.Approved)
            {
                result.RejectionFlags |= SignalReasonFlags.RiskVeto;
                return result;
            }

            var (sentimentVote, riskVote) = await RunConcurrentAgents(request, riskParams, cancellationToken);
            var mlPrediction = _inferenceAgent.PredictNextReturn(_featureAgent.GetSnapshot());

            float confidenceScore = CalculateConfidence(sentimentVote, riskVote, mlPrediction, request.InitialDecision, result);
            confidenceScore = await ApplyMicrostructureInjection(request.Signal, result, confidenceScore, cancellationToken);

            FinalizeConsensus(confidenceScore, riskVote, mlPrediction, request.InitialDecision, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Swarm Orchestration CRITICAL FAILURE for {Symbol}", request.Signal.Symbol);
            result.Approved = false;
            result.RejectionFlags |= SignalReasonFlags.SystemError;
        }

        return result;
    }

    private RiskParameters ResolveRegimeAdjustedRisk(RiskParameters current, MarketRegimeDetector.RegimeResult regime)
    {
        if (current != RiskParameters.Default) return current;
        return regime.Regime switch
        {
            MarketRegime.Volatile => RiskParameters.Default with { MaxPositionSizePercent = 2.5m },
            MarketRegime.Trending => RiskParameters.Default with { MaxPositionSizePercent = 7.5m },
            _ => RiskParameters.Default
        };
    }

    private async Task<((bool Approved, string Reason) Sentiment, (bool Approved, string Reason, decimal SafePositionSizePercent) Risk)> RunConcurrentAgents(SwarmNegotiationRequest request, RiskParameters riskParams, CancellationToken ct)
    {
        var sentimentTask = _sentimentAgent.EvaluateAsync(request.Signal.Symbol, ct);
        var riskTask = _riskAgent.EvaluateAsync(request.InitialDecision, riskParams, request.Signal.Volume24h);
        await Task.WhenAll(sentimentTask, riskTask).WaitAsync(ct);
        return (await sentimentTask, await riskTask);
    }

    private float CalculateConfidence((bool Approved, string Reason) sentiment, (bool Approved, string Reason, decimal Size) risk, PredictionResult ml, TradeDecision decision, SwarmConsensusResult result)
    {
        float score = 0;
        if (sentiment.Approved) score += 0.3f; else result.RejectionFlags |= SignalReasonFlags.SentimentVeto;
        if (risk.Approved) score += 0.4f; else result.RejectionFlags |= SignalReasonFlags.RiskVeto;

        if (ml.IsValid)
        {
            if (ml.Value < MlDownsideThreshold && decision.Side == TradeSide.Buy)
            {
                result.RejectionFlags |= SignalReasonFlags.MachineLearningVeto;
                score -= 0.5f;
            }
            else score += 0.3f;
        }
        return score;
    }

    private async Task<float> ApplyMicrostructureInjection(Signal signal, SwarmConsensusResult result, float confidence, CancellationToken ct)
    {
        if (signal.OrderFlowImbalance.HasValue)
        {
            await _ofiAgent.Handle(signal, ct);
            result.ExecutionAction = signal.ExecutionAction;
            if (result.ExecutionAction != ExecutionActionType.MarketTaker) return confidence + 0.1f;
        }
        return confidence;
    }

    private void FinalizeConsensus(float score, (bool Approved, string Reason, decimal Size) risk, PredictionResult ml, TradeDecision decision, SwarmConsensusResult result)
    {
        if (score >= SuccessThreshold && result.RejectionFlags == SignalReasonFlags.None)
        {
            result.Approved = true;
            result.AdjustedKellyFraction = risk.Size * (decimal)(1.0f + ml.Value);
            result.AdjustedTakeProfit = decision.TakeProfit;
            if (result.ExecutionAction == ExecutionActionType.None) result.ExecutionAction = ExecutionActionType.MarketTaker;
        }
    }
}
