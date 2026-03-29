namespace ThatsMyQuant.Infrastructure.Persistence;

/// <summary>
/// Persists learnable meta-learning agent state (strategy weights, bandit arm values)
/// across process restarts. One row per agent, keyed by AgentId.
/// </summary>
public class MetaLearningStateRecord
{
    /// <summary>Agent identifier — e.g. "Meta.OnlineLearning", "Meta.RLBandit".</summary>
    public string AgentId { get; set; } = "";

    /// <summary>JSON-serialised agent-specific state blob.</summary>
    public string StateJson { get; set; } = "{}";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
