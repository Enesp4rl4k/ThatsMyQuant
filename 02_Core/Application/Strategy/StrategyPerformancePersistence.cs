using System.Text.Json;
using System.Linq;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

public interface IStrategyPerformanceStore
{
    Task<StrategyPerformanceState> LoadAsync();
    Task SaveAsync(StrategyPerformanceState state);
}

public sealed class JsonStrategyPerformanceStore : IStrategyPerformanceStore
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public JsonStrategyPerformanceStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ThatsMyQuant");
        Directory.CreateDirectory(root);
        _filePath = Path.Combine(root, "engine-strategy-performance.json");
    }

    public async Task<StrategyPerformanceState> LoadAsync()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new StrategyPerformanceState();
            }

            var json = await File.ReadAllTextAsync(_filePath);
            return JsonSerializer.Deserialize<StrategyPerformanceState>(json, JsonOptions)
                   ?? new StrategyPerformanceState();
        }
        catch
        {
            return new StrategyPerformanceState();
        }
    }

    public async Task SaveAsync(StrategyPerformanceState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, JsonOptions);
            await File.WriteAllTextAsync(_filePath, json);
        }
        catch
        {
            // Best-effort persistence. Engine should keep running if local storage fails.
        }
    }
}


public sealed class StrategyPerformanceState
{
    public List<StrategyBucketPerformanceSnapshot> BacktestBuckets { get; set; } = new();
    public List<StrategyBucketPerformanceSnapshot> LiveBuckets { get; set; } = new();
    public List<StrategyBucketPerformanceSnapshot> PaperBuckets { get; set; } = new();
    public List<ExitPolicyPerformanceSnapshot> ExitPolicyBuckets { get; set; } = new();
    public bool PortfolioValidationReady { get; set; }
    public int PortfolioReadySymbols { get; set; }
    public int PortfolioRequiredSymbols { get; set; }
    public decimal PortfolioPooledPnL { get; set; }
    public double PortfolioAverageSharpe { get; set; }
    public double PortfolioAverageProfitFactor { get; set; }
    public decimal PortfolioAverageExpectancyPerTrade { get; set; }
    public string PortfolioValidationSummary { get; set; } = string.Empty;
    public DateTime PortfolioValidationRecordedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;

    public StrategyPerformanceState Clone()
    {
        return new StrategyPerformanceState
        {
            BacktestBuckets = BacktestBuckets.Select(b => b.Clone()).ToList(),
            LiveBuckets = LiveBuckets.Select(b => b.Clone()).ToList(),
            PaperBuckets = PaperBuckets.Select(b => b.Clone()).ToList(),
            ExitPolicyBuckets = ExitPolicyBuckets.Select(b => b.Clone()).ToList(),
            PortfolioValidationReady = PortfolioValidationReady,
            PortfolioReadySymbols = PortfolioReadySymbols,
            PortfolioRequiredSymbols = PortfolioRequiredSymbols,
            PortfolioPooledPnL = PortfolioPooledPnL,
            PortfolioAverageSharpe = PortfolioAverageSharpe,
            PortfolioAverageProfitFactor = PortfolioAverageProfitFactor,
            PortfolioAverageExpectancyPerTrade = PortfolioAverageExpectancyPerTrade,
            PortfolioValidationSummary = PortfolioValidationSummary,
            PortfolioValidationRecordedAtUtc = PortfolioValidationRecordedAtUtc,
            RecordedAtUtc = RecordedAtUtc
        };
    }
}

public sealed class StrategyBucketPerformanceSnapshot
{
    public string Symbol { get; set; } = string.Empty;
    public string StrategyTag { get; set; } = string.Empty;
    public string RegimeTag { get; set; } = string.Empty;
    public string SessionTag { get; set; } = string.Empty;
    public int Trades { get; set; }
    public double WinRate { get; set; }
    public double ProfitFactor { get; set; }
    public decimal ExpectedValue { get; set; }
    public decimal TotalPnL { get; set; }
    public double UlcerIndex { get; set; }
    public double SortinoRatio { get; set; }
    public decimal AverageEdgeScore { get; set; }
    public int HighEdgeTrades { get; set; }
    public double HighEdgeWinRate { get; set; }
    public double HighEdgeProfitFactor { get; set; }
    public decimal HighEdgeExpectedValue { get; set; }
    public decimal HighEdgeTotalPnL { get; set; }
    public decimal AverageFinalAllocatorBias { get; set; }
    public int BoostedAllocatorTrades { get; set; }
    public double BoostedAllocatorWinRate { get; set; }
    public double BoostedAllocatorProfitFactor { get; set; }
    public decimal BoostedAllocatorExpectedValue { get; set; }
    public int TrimmedAllocatorTrades { get; set; }
    public double TrimmedAllocatorWinRate { get; set; }
    public double TrimmedAllocatorProfitFactor { get; set; }
    public decimal TrimmedAllocatorExpectedValue { get; set; }
    public decimal AverageVenueExecutionBias { get; set; }
    public int BoostedVenueExecutionTrades { get; set; }
    public double BoostedVenueExecutionWinRate { get; set; }
    public double BoostedVenueExecutionProfitFactor { get; set; }
    public decimal BoostedVenueExecutionExpectedValue { get; set; }
    public int TrimmedVenueExecutionTrades { get; set; }
    public double TrimmedVenueExecutionWinRate { get; set; }
    public double TrimmedVenueExecutionProfitFactor { get; set; }
    public decimal TrimmedVenueExecutionExpectedValue { get; set; }
    public decimal AverageEntrySlippagePercent { get; set; }
    public double AverageExecutionLatencyMs { get; set; }
    public string DominantMlModelVersion { get; set; } = string.Empty;
    public int FeatureDriftTrades { get; set; }
    public int CriticalFeatureDriftTrades { get; set; }
    public int SizeTrimmedByDriftTrades { get; set; }
    public double FeatureDriftRate { get; set; }
    public double CriticalFeatureDriftRate { get; set; }
    public double SizeTrimmedByDriftRate { get; set; }
    public decimal AverageFeatureDriftMaxZScore { get; set; }
    public double SharpeRatio { get; set; }
    public double MaxDrawdownPercent { get; set; }
    public double AverageRR { get; set; }
    public int MaxConsecutiveLosses { get; set; }
    public double PboScore { get; set; } = -1;
    public double StochasticDominance { get; set; }
    public decimal StartingCapital { get; set; }
    public decimal FinalEquity { get; set; }
    public string BacktestVersion { get; set; } = string.Empty;
    public string BacktestHash { get; set; } = string.Empty;
    public bool BacktestPass { get; set; }
    public bool PaperPass { get; set; }
    public bool LiveProbePass { get; set; }
    public bool ScaledLiveEligible { get; set; }
    public bool PromotionReady { get; set; }
    public string PromotionStage { get; set; } = "Backtest";
    public string PaperPromotionSummary { get; set; } = string.Empty;
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;

    public StrategyBucketPerformanceSnapshot Clone()
    {
        return (StrategyBucketPerformanceSnapshot)this.MemberwiseClone();
    }
}

public sealed class ExitPolicyPerformanceSnapshot
{
    public string Symbol { get; set; } = string.Empty;
    public string StrategyTag { get; set; } = string.Empty;
    public string RegimeTag { get; set; } = string.Empty;
    public string SessionTag { get; set; } = string.Empty;
    public string ExitPolicyTag { get; set; } = string.Empty;
    public string ExecutionMode { get; set; } = "SIMULATED";
    public int Trades { get; set; }
    public double WinRate { get; set; }
    public double ProfitFactor { get; set; }
    public decimal ExpectedValue { get; set; }
    public decimal TotalPnL { get; set; }
    public double UlcerIndex { get; set; }
    public double SortinoRatio { get; set; }
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;

    public ExitPolicyPerformanceSnapshot Clone()
    {
        return (ExitPolicyPerformanceSnapshot)this.MemberwiseClone();
    }
}

public readonly record struct PromotionGuardResult(
    bool Approved,
    decimal SizeMultiplier,
    string Reason,
    bool PromotionReady,
    double DriftScore,
    string PromotionStage);

public readonly record struct FinalAllocatorBiasResult(
    decimal SizeMultiplier,
    string Reason,
    string Scope,
    decimal VenueExecutionBias = 1.00m,
    string VenueExecutionScope = "");
