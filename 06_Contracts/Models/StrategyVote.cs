namespace ThatsMyQuant.Contracts.Models;

public enum VoteOutcome { Approve, Reject, Abstain }

public record StrategyVote
{
    public string StrategyTag { get; init; } = string.Empty;
    public VoteOutcome Vote { get; init; } = VoteOutcome.Abstain;
    public float ConfidenceAdjustment { get; init; }
    public decimal? SizeSuggestion { get; init; }
    public string Reasoning { get; init; } = string.Empty;

    public static StrategyVote Approve(string tag, float adjustment, string reason, decimal? size = null)
        => new() { StrategyTag = tag, Vote = VoteOutcome.Approve, ConfidenceAdjustment = adjustment, Reasoning = reason, SizeSuggestion = size };

    public static StrategyVote Reject(string tag, float adjustment, string reason)
        => new() { StrategyTag = tag, Vote = VoteOutcome.Reject, ConfidenceAdjustment = -Math.Abs(adjustment), Reasoning = reason };

    public static StrategyVote Abstain(string tag)
        => new() { StrategyTag = tag, Vote = VoteOutcome.Abstain, ConfidenceAdjustment = 0f };
}
