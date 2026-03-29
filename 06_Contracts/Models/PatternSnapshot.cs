namespace ThatsMyQuant.Contracts.Models;

public enum IchimokuState { Unknown, AboveCloud, InCloud, BelowCloud, TKCrossBullish, TKCrossBearish }

public record PatternSnapshot
{
    /// <summary>NaN means "no agent reported this field" — distinct from 0.0 (price at midline).</summary>
    public double BollingerPercentB { get; init; } = double.NaN;
    public bool BollingerSqueeze { get; init; }
    public decimal FibNearestLevel { get; init; }
    public double FibRetracementDepth { get; init; }
    public IchimokuState IchimokuState { get; init; } = IchimokuState.Unknown;
    /// <summary>NaN means "no agent reported this field" — distinct from 0.0 (price at VWAP).</summary>
    public double VwapDeviationSigmas { get; init; } = double.NaN;
    public IReadOnlyList<string> PatternTags { get; init; } = [];

    public static readonly PatternSnapshot Empty = new();
}
