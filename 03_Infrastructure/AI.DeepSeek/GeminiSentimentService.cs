using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ThatsMyQuant.Domain.Interfaces;

namespace ThatsMyQuant.Infrastructure.Ai;

/// <summary>
/// A high-intelligence NLP service utilizing Google Gemini 1.5 Pro to categorize 
/// macroeconomic news into structural sentiment vectors.
/// </summary>
public class GeminiSentimentService : ISentimentService
{
    private readonly ILogger<GeminiSentimentService> _logger;
    private readonly IChatCompletionService _chatService;

    public GeminiSentimentService(ILogger<GeminiSentimentService> logger, Kernel kernel)
    {
        _logger = logger;
        _chatService = kernel.GetRequiredService<IChatCompletionService>();
    }

    /// <summary>
    /// Analyzes a news headline or tweet and returns a sentiment score between -1.0 and 1.0.
    /// Also flags "Black Swan" events that should trigger an immediate Swarm Veto.
    /// </summary>
    public async Task<(double SentimentScore, bool IsBlackSwan, string Reasoning)> AnalyzeNewsAsync(string headline)
    {
        _logger.LogInformation("LLM Analyzing Sentiment for: {Headline}", headline);

        var prompt = $@"
        You are a specialized Macro-Economic Intelligence Agent for an HFT Trading Bot.
        Analyze the following news headline and provide three key metrics:
        1. Sentiment Score: -1.0 (Panic/Bearish) to 1.0 (Euphoria/Bullish). 
        2. Black Swan Flag: True if this event represents a catastrophic failure (Exchange Hack, Bankruptcies, War, CPI Spike > 2%).
        3. Reasoning: A 1-sentence explanation.

        HEADLINE: {headline}

        OUTPUT FORMAT (JSON):
        {{
            ""score"": 0.0,
            ""isBlackSwan"": false,
            ""reasoning"": ""...""
        }}
        ";

        try
        {
            var result = await _chatService.GetChatMessageContentsAsync(prompt);
            var response = result.FirstOrDefault()?.Content;

            if (string.IsNullOrEmpty(response)) return (0, false, "No response from Gemini.");

            // Simple cleanup if LLM wraps in markdown
            response = response.Replace("```json", "").Replace("```", "").Trim();

            // Native System.Text.Json parsing
            using var doc = System.Text.Json.JsonDocument.Parse(response);
            var score = doc.RootElement.GetProperty("score").GetDouble();
            var isBlackSwan = doc.RootElement.GetProperty("isBlackSwan").GetBoolean();
            var reasoning = doc.RootElement.GetProperty("reasoning").GetString() ?? "Analyzed.";

            return (score, isBlackSwan, reasoning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gemini Sentiment Analysis failed. Defaulting to Neutral Risk.");
            return (0, false, "LLM Connection Error.");
        }
    }
}
