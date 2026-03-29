namespace ThatsMyQuant.Contracts.Models;

public enum VoteOutcome { Approve, Reject, Abstain }

public record StrategyVote
{
    public string StrategyTag { get; init; } = string.Empty;
    public VoteOutcome Vote { get; init; } = VoteOutcome.Abstain;
    public float ConfidenceAdjustment { get; init; }
    public decimal? SizeSuggestion { get; init; }
    public string Reasoning { get; init; } = string.Empty;

    /// <summary>
    /// The agent's static SwarmWeight, carried on the vote so the orchestrator can use it
    /// as a Bayesian prior when the RL bandit hasn't yet learned a data-driven weight.
    /// </summary>
    public int PriorWeight { get; init; } = 1;

    public static StrategyVote Approve(string tag, float adjustment, string reason, decimal? size = null, int priorWeight = 1)
        => new() { StrategyTag = tag, Vote = VoteOutcome.Approve, ConfidenceAdjustment = adjustment, Reasoning = reason, SizeSuggestion = size, PriorWeight = priorWeight };

    public static StrategyVote Reject(string tag, float adjustment, string reason, int priorWeight = 1)
        => new() { StrategyTag = tag, Vote = VoteOutcome.Reject, ConfidenceAdjustment = -Math.Abs(adjustment), Reasoning = reason, PriorWeight = priorWeight };

    public static StrategyVote Abstain(string tag)
        => new() { StrategyTag = tag, Vote = VoteOutcome.Abstain, ConfidenceAdjustment = 0f };
}
