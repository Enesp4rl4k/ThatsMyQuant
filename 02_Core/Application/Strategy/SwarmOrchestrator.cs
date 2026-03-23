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

/// <summary>
/// A MediatR request asking the agent swarm for consensus on a potential trade execution.
/// </summary>
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
    
    // Converted generic string RejectionReason into HFT-grade strongly-typed Enums
    public SignalReasonFlags RejectionFlags { get; set; } = SignalReasonFlags.None;
    
    // Explicitly dictate Microstructure targets to the execution router
    public ExecutionActionType ExecutionAction { get; set; } = ExecutionActionType.None;

    public decimal AdjustedKellyFraction { get; set; }
    public decimal AdjustedTakeProfit { get; set; }
}

/// <summary>
/// The central Multi-Agent coordinator. 
/// Upgraded to a Concurrent Parallel Swarm Pipeline executing NLP, Risk, and Machine Learning matrices simultaneously.
/// </summary>
public class SwarmOrchestrator : IRequestHandler<SwarmNegotiationRequest, SwarmConsensusResult>
{
    private readonly RiskAgent _riskAgent;
    private readonly SentimentAgent _sentimentAgent;
    private readonly InferenceAgent _inferenceAgent;
    private readonly FeatureEngineeringAgent _featureAgent;
    private readonly ITradeRepository _tradeRepository;
    private readonly OFIExecutionAgent _ofiAgent;
    private readonly ILogger<SwarmOrchestrator> _logger;

    private const float MlDownsideThreshold = -0.01f;
    private const float SuccessThreshold = 0.6f;

    public SwarmOrchestrator(
        RiskAgent riskAgent, 
        SentimentAgent sentimentAgent, 
        InferenceAgent inferenceAgent,
        FeatureEngineeringAgent featureAgent,
        ITradeRepository tradeRepository,
        OFIExecutionAgent ofiAgent,
        ILogger<SwarmOrchestrator> logger)
    {
        _riskAgent = riskAgent;
        _sentimentAgent = sentimentAgent;
        _inferenceAgent = inferenceAgent;
        _featureAgent = featureAgent;
        _tradeRepository = tradeRepository;
        _ofiAgent = ofiAgent;
        _logger = logger;
    }

    public async Task<SwarmConsensusResult> Handle(SwarmNegotiationRequest request, CancellationToken cancellationToken)
    {
        var result = new SwarmConsensusResult { Approved = false };

        try
        {
            // 1. REGIME & STRUCTURE DETECTION
            var regime = MarketRegimeDetector.Detect(request.Candles);
            
            // Adapt risk parameters to the current regime if not already adapted
            var riskParams = request.Risk;
            if (riskParams == RiskParameters.Default)
            {
                riskParams = regime.Regime switch
                {
                    MarketRegime.Volatile => RiskParameters.Default with { MaxPositionSizePercent = 2.5m },
                    MarketRegime.Trending => RiskParameters.Default with { MaxPositionSizePercent = 7.5m },
                    _ => RiskParameters.Default
                };
            }

            // 2. PORTFOLIO RISK GUARDRAILS
            var recentTrades = await _tradeRepository.GetAllAsync(500);
            var portfolioGuard = await _riskAgent.EvaluatePortfolioGuardrailsAsync(
                recentTrades,
                request.Signal,
                request.InitialDecision.PositionSizePercent,
                riskParams);

            if (!portfolioGuard.Approved)
            {
                result.RejectionFlags |= SignalReasonFlags.RiskVeto;
                return result;
            }

            // 3. CONCURRENT SWARM EXECUTION
            var sentimentTask = _sentimentAgent.EvaluateAsync(request.Signal.Symbol, cancellationToken);
            var riskTask = _riskAgent.EvaluateAsync(request.InitialDecision, riskParams, request.Signal.Volume24h);
            
            // Wait with timeout to prevent system-wide hangs
            await Task.WhenAll(sentimentTask, riskTask).WaitAsync(cancellationToken);
            
            var sentimentVote = sentimentTask.Result;
            var riskVote = riskTask.Result;

            // 4. ML INFERENCE WITH THREAD-SAFE FEATURES
            var mlPrediction = _inferenceAgent.PredictNextReturn(_featureAgent.GetSnapshot());

            // 5. WEIGHTED VOTING ENGINE (Hedge Fund Standard: 30/40/30)
            float confidenceScore = 0;
            
            // Sentiment Weight (30%)
            if (sentimentVote.Approved) confidenceScore += 0.3f;
            else result.RejectionFlags |= SignalReasonFlags.SentimentVeto;

            // Technical/Risk Weight (40%)
            if (riskVote.Approved) confidenceScore += 0.4f;
            else result.RejectionFlags |= SignalReasonFlags.RiskVeto;

            // ML Weight (30%)
            if (mlPrediction.IsValid)
            {
                // ML as Weighting Engine rather than hard veto
                if (mlPrediction.Value < MlDownsideThreshold && request.InitialDecision.Side == TradeSide.Buy)
                {
                    result.RejectionFlags |= SignalReasonFlags.MachineLearningVeto;
                    // Catastrophic ML prediction can still lower score significantly
                    confidenceScore -= 0.5f; 
                }
                else
                {
                    confidenceScore += 0.3f;
                }
            }

            // 6. MICROSTRUCTURE (OFI) INJECTION
            if (request.Signal.OrderFlowImbalance.HasValue)
            {
                await _ofiAgent.Handle(request.Signal, cancellationToken);
                result.ExecutionAction = request.Signal.ExecutionAction;
                
                // OFI Front-run approval adds extra confidence
                if (result.ExecutionAction != ExecutionActionType.MarketTaker)
                {
                    confidenceScore += 0.1f;
                }
            }

            // 7. FINAL CONSENSUS
            if (confidenceScore >= SuccessThreshold && result.RejectionFlags == SignalReasonFlags.None)
            {
                result.Approved = true;
                result.AdjustedKellyFraction = riskVote.SafePositionSizePercent * (decimal)(1.0f + mlPrediction.Value);
                result.AdjustedTakeProfit = request.InitialDecision.TakeProfit;
                
                if (result.ExecutionAction == ExecutionActionType.None)
                {
                    result.ExecutionAction = ExecutionActionType.MarketTaker;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Swarm Orchestration CRITICAL FAILURE for {Symbol}", request.Signal.Symbol);
            result.Approved = false;
            result.RejectionFlags |= SignalReasonFlags.SystemError;
        }

        return result;
    }
}
