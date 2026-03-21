using MediatR;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// A MediatR request asking the agent swarm for consensus on a potential trade execution.
/// </summary>
public class SwarmNegotiationRequest : IRequest<SwarmConsensusResult>
{
    public Signal Signal { get; set; } = null!;
    public TradeDecision InitialDecision { get; set; } = null!;
    public RiskParameters Risk { get; set; } = null!;
}

public class SwarmConsensusResult
{
    public bool Approved { get; set; }
    public string RejectionReason { get; set; } = string.Empty;
    public decimal AdjustedKellyFraction { get; set; }
    public decimal AdjustedTakeProfit { get; set; }
}

/// <summary>
/// The central Multi-Agent coordinator. 
/// Unlike linear processing, this pipelines a candidate trade through multiple independent AI agents (Risk, Sentiment).
/// Any single agent can VETO the execution, or dynamically compress the position size/margins.
/// </summary>
public class SwarmOrchestrator : IRequestHandler<SwarmNegotiationRequest, SwarmConsensusResult>
{
    private readonly RiskAgent _riskAgent;
    private readonly SentimentAgent _sentimentAgent;

    public SwarmOrchestrator(RiskAgent riskAgent, SentimentAgent sentimentAgent)
    {
        _riskAgent = riskAgent;
        _sentimentAgent = sentimentAgent;
    }

    public async Task<SwarmConsensusResult> Handle(SwarmNegotiationRequest request, CancellationToken cancellationToken)
    {
        // 1. Ask Sentiment Agent for macro/news approval
        var sentimentVote = await _sentimentAgent.EvaluateAsync(request.Signal.Symbol);
        if (!sentimentVote.Approved)
        {
            return new SwarmConsensusResult { Approved = false, RejectionReason = $"Sentiment VETO: {sentimentVote.Reason}" };
        }

        // 2. Ask Risk Agent to audit Kelly fractions and extreme tail risk
        var riskVote = await _riskAgent.EvaluateAsync(request.InitialDecision, request.Risk, request.Signal.Volume24h);
        if (!riskVote.Approved)
        {
            return new SwarmConsensusResult { Approved = false, RejectionReason = $"Risk VETO: {riskVote.Reason}" };
        }

        // Unanimous consensus
        return new SwarmConsensusResult
        {
            Approved = true,
            AdjustedKellyFraction = riskVote.SafePositionSizePercent,
            AdjustedTakeProfit = request.InitialDecision.TakeProfit // Could be modified by agents
        };
    }
}
