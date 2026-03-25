using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Infrastructure.Ai;

public class AiAnalystService : IAiAnalystService
{
    private readonly ILogger<AiAnalystService> _logger;
    private readonly IChatCompletionService? _chatService;

    public AiAnalystService(ILogger<AiAnalystService> logger, Kernel? kernel = null)
    {
        _logger = logger;
        _chatService = kernel?.Services.GetService(typeof(IChatCompletionService)) as IChatCompletionService;
    }

    public async Task<AiAnalysisResult> AnalyzeSnapshotAsync(string symbol, IndicatorSnapshot snapshot)
    {
        _logger.LogInformation("Generating AI Executive Summary for {Symbol}...", symbol);

        string atStatus = snapshot.AlphaTrend?.IsBullish == true ? "Bullish" : "Bearish";
        string srStatus = snapshot.NearestSupport != null ? $"Support at {snapshot.NearestSupport.Price:F2}" : "No Support Context";
        var heuristicResult = BuildHeuristicOverlay(snapshot);

        var prompt = $@"
        You are a Senior Quantitative Analyst. Provide a brief, professional summary (max 2 sentences) 
        of the current market state for the asset {symbol}. 
        
        Data points to consider:
        - Price: {snapshot.CurrentPrice:F2}
        - RSI: {snapshot.RSI:F1}
        - MACD: {snapshot.MACDHistogram:F4}
        - Volume Ratio: {snapshot.VolumeRatio:F2}
        - ATR: {snapshot.ATR:F4}
        - AlphaTrend: {atStatus}
        - S/R Context: {srStatus}

        In addition to the summary, provide:
        - a Sentiment Score between -1.0 (Extreme Bearish) and 1.0 (Extreme Bullish)
        - a short trade recommendation
        - an execution readiness score from 0 to 100
        - one-line risk summary
        - a pipe-separated list of risk flags

        Your response MUST exactly follow this format:
        SUMMARY: [Your 2 sentence analysis]
        SENTIMENT: [Number between -1.0 and 1.0]
        RECOMMENDATION: [Short trade recommendation]
        READINESS: [Integer between 0 and 100]
        RISK: [One line risk summary]
        FLAGS: [flag one | flag two | flag three]
        ";

        try
        {
            if (_chatService == null)
            {
                heuristicResult.ExecutiveSummary = "AI provider is not configured, showing rule-based fallback guidance.";
                return heuristicResult;
            }

            var results = await _chatService.GetChatMessageContentsAsync(prompt);
            var responseText = results.FirstOrDefault()?.Content ?? string.Empty;
            
            _logger.LogDebug("Raw AI Output: {Output}", responseText);

            return ParseAiResponse(responseText, heuristicResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate AI Analysis.");
            heuristicResult.ExecutiveSummary = "AI analysis unavailable, showing rule-based fallback guidance.";
            return heuristicResult;
        }
    }

    public async Task<string> ReviewTradeAsync(Trade trade)
    {
        _logger.LogInformation("Generating AI Post-Trade Review for {Symbol} (Trade ID: {Id})...", trade.Symbol, trade.Id);

        var pnlStatus = trade.RealisedPnL >= 0 ? "Profit" : "Loss";
        var prompt = $@"
        You are an Institutional Trading Coach. Analyze this closed trade and provide a concise, 
        brutally honest feedback summary (max 3 sentences). 
        
        TRADE DETAILS:
        - Symbol: {trade.Symbol}
        - Side: {trade.Side}
        - Entry: {trade.EntryPrice:F2} at {trade.EntryTime:f}
        - Exit: {trade.ExitPrice:F2} at {trade.ExitTime:f}
        - PnL: {trade.RealisedPnL:F2} ({trade.PnlPercent:P2}) - {pnlStatus}
        - Strategy: {trade.StrategyTag}
        - Regime: {trade.RegimeTag}
        - Edge Score: {trade.EdgeScore:F2}
        - Promotion Stage: {trade.PromotionStage}
        - Execution: {trade.ExecutionMode} (Latency: {trade.ExecutionLatencyMs}ms)

        CONTEXT:
        - Exact Bucket Impact: {trade.ExactBucketImpactLabel} ({trade.ExactBucketSummary})
        - Concordance: {trade.ConcordanceImpactLabel} ({trade.ConcordanceSummary})
        - Venue Bias: {trade.VenueImpactLabel}

        Your feedback should address:
        1. Was the execution quality acceptable for HFT?
        2. Did the trade align with the detected Market Regime?
        3. One actionable improvement for future setups.

        Format: Just provide the raw text response.
        ";

        try
        {
            if (_chatService == null)
            {
                return "AI Coach unavailable. Rule-based review: " + 
                       (trade.RealisedPnL >= 0 ? "Positive outcome, but ensure adherence to regime filters." 
                                              : "Negative outcome. Verify if stop-loss was optimal for this regime.");
            }

            var results = await _chatService.GetChatMessageContentsAsync(prompt);
            return results.FirstOrDefault()?.Content ?? "No review generated.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate AI Trade Review.");
            return "Review generation failed due to an internal error.";
        }
    }

    private AiAnalysisResult ParseAiResponse(string responseText, AiAnalysisResult heuristicFallback)
    {
        var result = new AiAnalysisResult();
        var lines = responseText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (line.StartsWith("SUMMARY:", StringComparison.OrdinalIgnoreCase))
            {
                result.ExecutiveSummary = line.Substring("SUMMARY:".Length).Trim();
            }
            else if (line.StartsWith("SENTIMENT:", StringComparison.OrdinalIgnoreCase))
            {
                var valStr = line.Substring("SENTIMENT:".Length).Trim();
                if (double.TryParse(valStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsedScore))
                {
                    result.SentimentScore = parsedScore;
                }
            }
            else if (line.StartsWith("RECOMMENDATION:", StringComparison.OrdinalIgnoreCase))
            {
                result.TradeRecommendation = line.Substring("RECOMMENDATION:".Length).Trim();
            }
            else if (line.StartsWith("READINESS:", StringComparison.OrdinalIgnoreCase))
            {
                var valStr = line.Substring("READINESS:".Length).Trim();
                if (int.TryParse(valStr, out var readiness))
                {
                    result.ExecutionReadinessScore = Math.Clamp(readiness, 0, 100);
                }
            }
            else if (line.StartsWith("RISK:", StringComparison.OrdinalIgnoreCase))
            {
                result.RiskSummary = line.Substring("RISK:".Length).Trim();
            }
            else if (line.StartsWith("FLAGS:", StringComparison.OrdinalIgnoreCase))
            {
                result.RiskFlags = line.Substring("FLAGS:".Length)
                    .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        if (string.IsNullOrEmpty(result.ExecutiveSummary))
        {
            result.ExecutiveSummary = heuristicFallback.ExecutiveSummary;
        }

        if (result.ExecutionReadinessScore == 0)
        {
            result.ExecutionReadinessScore = heuristicFallback.ExecutionReadinessScore;
        }

        if (string.IsNullOrWhiteSpace(result.TradeRecommendation))
        {
            result.TradeRecommendation = heuristicFallback.TradeRecommendation;
        }

        if (string.IsNullOrWhiteSpace(result.RiskSummary))
        {
            result.RiskSummary = heuristicFallback.RiskSummary;
        }

        if (result.RiskFlags.Count == 0)
        {
            result.RiskFlags = heuristicFallback.RiskFlags;
        }

        if (Math.Abs(result.SentimentScore) < double.Epsilon)
        {
            result.SentimentScore = heuristicFallback.SentimentScore;
        }

        return result;
    }

    private static AiAnalysisResult BuildHeuristicOverlay(IndicatorSnapshot snapshot)
    {
        var flags = new List<string>();
        var readiness = 50;
        var sentiment = 0.0;

        if (snapshot.AlphaTrend?.IsBullish == true)
        {
            readiness += 15;
            sentiment += 0.25;
        }
        else
        {
            readiness -= 10;
            sentiment -= 0.20;
            flags.Add("Trend context is not bullish");
        }

        if ((snapshot.MACDHistogram ?? 0) > 0)
        {
            readiness += 10;
            sentiment += 0.20;
        }
        else
        {
            readiness -= 10;
            sentiment -= 0.15;
            flags.Add("Momentum confirmation is weak");
        }

        if (snapshot.RSI is >= 45 and <= 65)
        {
            readiness += 10;
            sentiment += 0.10;
        }
        else if (snapshot.RSI is > 70)
        {
            readiness -= 15;
            sentiment -= 0.20;
            flags.Add("RSI is overbought");
        }
        else if (snapshot.RSI is < 30)
        {
            readiness += 5;
            flags.Add("RSI is oversold; expect volatility");
        }

        if ((snapshot.VolumeRatio ?? 0) >= 1.5)
        {
            readiness += 10;
        }
        else
        {
            readiness -= 10;
            flags.Add("Volume confirmation is below ideal");
        }

        var atrPercent = snapshot.CurrentPrice > 0 && snapshot.ATR.HasValue
            ? snapshot.ATR.Value / (double)snapshot.CurrentPrice
            : 0;
        if (atrPercent > 0.03)
        {
            readiness -= 15;
            flags.Add("Volatility is elevated");
        }

        if (snapshot.NearestResistance != null &&
            Math.Abs(snapshot.NearestResistance.Price - snapshot.CurrentPrice) / snapshot.CurrentPrice < 0.015m)
        {
            readiness -= 10;
            flags.Add("Price is close to resistance");
        }

        if (snapshot.NearestSupport == null)
        {
            flags.Add("No nearby support level detected");
        }

        readiness = Math.Clamp(readiness, 5, 95);
        sentiment = Math.Clamp(sentiment, -1.0, 1.0);

        var recommendation = readiness switch
        {
            >= 75 => "Actionable setup with managed risk",
            >= 60 => "Cautious entry candidate",
            >= 45 => "Watchlist only until confirmation improves",
            _ => "Wait and preserve capital"
        };

        var riskSummary = flags.Count == 0
            ? "No material risk flags detected by the heuristic overlay."
            : string.Join("; ", flags.Take(3));

        return new AiAnalysisResult
        {
            ExecutiveSummary = "Rule-based overlay suggests waiting for aligned trend, momentum, and liquidity confirmation.",
            SentimentScore = sentiment,
            TradeRecommendation = recommendation,
            ExecutionReadinessScore = readiness,
            RiskSummary = riskSummary,
            RiskFlags = flags
        };
    }
}
