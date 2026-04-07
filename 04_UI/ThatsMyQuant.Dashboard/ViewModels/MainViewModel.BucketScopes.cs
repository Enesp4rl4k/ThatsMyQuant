using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class MainViewModel
{
    private static StrategyBucketKey CreateSessionBucketKey(Trade trade)
        => CreateStrategyBucketKey(trade, ResolveSessionBucketForTrade(trade));

    private static StrategyBucketKey CreateCrossSessionBucketKey(Trade trade)
        => CreateStrategyBucketKey(trade, "All Sessions");

    private static StrategyBucketKey CreateScopedSessionBucketKey(Trade trade)
        => CreateStrategyBucketKey(
            trade,
            ResolveSessionBucketForTrade(trade),
            string.IsNullOrWhiteSpace(trade.FinalAllocatorScope) ? "unknown scope" : trade.FinalAllocatorScope,
            NormalizeExecutionMode(trade.ExecutionMode),
            string.IsNullOrWhiteSpace(trade.PromotionStage) ? "UNKNOWN" : trade.PromotionStage);

    private static StrategyBucketKey CreateStrategyBucketKey(
        Trade trade,
        string sessionTag,
        string scope = "Default",
        string? executionMode = null,
        string? promotionStage = null)
    {
        return new StrategyBucketKey(
            string.IsNullOrWhiteSpace(trade.Symbol) ? "UNKNOWN" : trade.Symbol,
            string.IsNullOrWhiteSpace(trade.StrategyTag) ? "Unlabeled" : trade.StrategyTag,
            string.IsNullOrWhiteSpace(trade.RegimeTag) ? "Unknown" : trade.RegimeTag,
            sessionTag,
            scope,
            executionMode ?? "REAL",
            promotionStage ?? "UNKNOWN");
    }
}
