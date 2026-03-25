using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Interfaces;

public interface IAiAnalystService
{
    Task<AiAnalysisResult> AnalyzeSnapshotAsync(string symbol, IndicatorSnapshot snapshot);
    Task<string> ReviewTradeAsync(Trade trade);
}

public class AiAnalysisResult
{
    public string ExecutiveSummary { get; set; } = string.Empty;
    public double SentimentScore { get; set; }
    public string TradeRecommendation { get; set; } = "Wait";
    public int ExecutionReadinessScore { get; set; }
    public string RiskSummary { get; set; } = string.Empty;
    public List<string> RiskFlags { get; set; } = new();
}
