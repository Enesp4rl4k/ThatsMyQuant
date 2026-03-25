namespace ThatsMyQuant.Contracts.Models;

public enum IchimokuState { Unknown, AboveCloud, InCloud, BelowCloud, TKCrossBullish, TKCrossBearish }

public record PatternSnapshot
{
    public double BollingerPercentB { get; init; }
    public bool BollingerSqueeze { get; init; }
    public decimal FibNearestLevel { get; init; }
    public double FibRetracementDepth { get; init; }
    public IchimokuState IchimokuState { get; init; } = IchimokuState.Unknown;
    public double VwapDeviationSigmas { get; init; }
    public IReadOnlyList<string> PatternTags { get; init; } = [];

    public static readonly PatternSnapshot Empty = new();
}
