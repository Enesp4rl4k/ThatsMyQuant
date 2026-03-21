using ThatsMyQuant.Domain.Interfaces;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// A specialized NLP agent that queries live news, fundamental data, and macroenonomic indicators for the targeted asset.
/// Prevents the bot from firing technical longs straight into catastrophic news (e.g. CPI prints, bankruptcies).
/// </summary>
public class SentimentAgent
{
    private readonly ISentimentService _nlpService;
    private readonly NewsFeedSim _newsFeed;

    public SentimentAgent(ISentimentService nlpService, NewsFeedSim newsFeed)
    {
        _nlpService = nlpService;
        _newsFeed = newsFeed;
    }

    public async Task<(bool Approved, string Reason)> EvaluateAsync(string symbol)
    {
        // Fetch the current 'Global Headline' from the simulator
        string latestHeadline = _newsFeed.GetLatestHeadline();

        var analysis = await _nlpService.AnalyzeNewsAsync(latestHeadline);

        if (analysis.IsBlackSwan || analysis.SentimentScore < -0.6)
        {
            return (false, $"Macro VETO: {analysis.Reasoning} (Score: {analysis.SentimentScore})");
        }

        return (true, $"LLM Approved: {analysis.Reasoning} (Sentiment: {analysis.SentimentScore:F2})");
    }
}
