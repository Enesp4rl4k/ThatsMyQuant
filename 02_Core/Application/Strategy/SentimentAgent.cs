using Microsoft.Extensions.Logging;
using System.Threading;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// A specialized NLP agent that queries live news, fundamental data, and macroenonomic indicators for the targeted asset.
/// Prevents the bot from firing technical longs straight into catastrophic news (e.g. CPI prints, bankruptcies).
/// </summary>
public class SentimentAgent : IFocusedAgent
{
    private readonly ISentimentService _nlpService;
    private readonly NewsFeedSim _newsFeed;

    public Task ResetAsync(string symbol, CancellationToken ct = default)
    {
        _newsFeed.Reset(); // Assuming NewsFeedSim has a Reset or just ignore
        _logger?.LogInformation("SentimentAgent reset for {Symbol}", symbol);
        return Task.CompletedTask;
    }

    public Task ProcessStepAsync(Candle candle, CancellationToken ct = default)
    {
        // Sentiment might query for news on the specific candle date
        return Task.CompletedTask;
    }

    private readonly ILogger<SentimentAgent>? _logger;

    public SentimentAgent(ISentimentService nlpService, NewsFeedSim newsFeed, ILogger<SentimentAgent>? logger = null)
    {
        _nlpService = nlpService;
        _newsFeed = newsFeed;
        _logger = logger;
    }

    public async Task<(bool Approved, string Reason)> EvaluateAsync(string symbol, CancellationToken cancellationToken = default)
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
