namespace ThatsMyQuant.Contracts.Models;

public record MetaAdaptation
{
    public IReadOnlyDictionary<string, float> StrategyWeights { get; init; }
        = new Dictionary<string, float>();
    public float RecommendedRegimeBias { get; init; } = 1f;
    public float ConfidenceScalar { get; init; } = 1f;

    public static readonly MetaAdaptation Default = new();
}
