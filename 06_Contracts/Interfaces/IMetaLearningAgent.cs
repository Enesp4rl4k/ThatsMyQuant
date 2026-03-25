using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Interfaces;

public interface IMetaLearningAgent
{
    string AgentId { get; }
    Task<MetaAdaptation> AdaptAsync(
        IReadOnlyList<Trade> recentHistory,
        MarketRegime regime,
        CancellationToken ct = default);
}
