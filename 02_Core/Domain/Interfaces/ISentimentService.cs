namespace ThatsMyQuant.Domain.Interfaces;

/// <summary>
/// Interface for macroeconomic and news sentiment analysis powered by LLMs.
/// </summary>
public interface ISentimentService
{
    /// <summary>
    /// Analyzes news text and returns a normalized sentiment score and black-swan risk flag.
    /// </summary>
    Task<(double SentimentScore, bool IsBlackSwan, string Reasoning)> AnalyzeNewsAsync(string headline);
}
