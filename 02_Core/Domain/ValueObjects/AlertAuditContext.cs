namespace ThatsMyQuant.Domain.ValueObjects;

public sealed class AlertAuditContext
{
    public string ExecutionMode { get; init; } = string.Empty;
    public string PromotionStage { get; init; } = string.Empty;
    public string StageExplanation { get; init; } = string.Empty;
    public string ExactBucketImpact { get; init; } = string.Empty;
    public string ExactBucketSummary { get; init; } = string.Empty;
    public string EdgeSummary { get; init; } = string.Empty;
    public string ExitSummary { get; init; } = string.Empty;
    public string VenueImpact { get; init; } = string.Empty;
    public string AllocatorImpact { get; init; } = string.Empty;
    public string Reasoning { get; init; } = string.Empty;
}
