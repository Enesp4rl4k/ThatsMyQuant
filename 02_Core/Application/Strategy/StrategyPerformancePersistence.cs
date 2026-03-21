using System.Text.Json;
using ThatsMyQuant.Domain.Entities;

namespace ThatsMyQuant.Application.Strategy;

public interface IStrategyPerformanceStore
{
    StrategyPerformanceState Load();
    void Save(StrategyPerformanceState state);
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

    public StrategyPerformanceState Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new StrategyPerformanceState();
            }

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<StrategyPerformanceState>(json, JsonOptions)
                   ?? new StrategyPerformanceState();
        }
        catch
        {
            return new StrategyPerformanceState();
        }
    }

    public void Save(StrategyPerformanceState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(_filePath, json);
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
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
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
    public decimal AverageEdgeScore { get; set; }
    public int HighEdgeTrades { get; set; }
    public double HighEdgeWinRate { get; set; }
    public double HighEdgeProfitFactor { get; set; }
    public decimal HighEdgeExpectedValue { get; set; }
    public decimal HighEdgeTotalPnL { get; set; }
    public decimal AverageEntrySlippagePercent { get; set; }
    public double AverageExecutionLatencyMs { get; set; }
    public bool BacktestPass { get; set; }
    public bool PaperPass { get; set; }
    public bool LiveProbePass { get; set; }
    public bool ScaledLiveEligible { get; set; }
    public bool PromotionReady { get; set; }
    public string PromotionStage { get; set; } = "Backtest";
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
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
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
}

public readonly record struct PromotionGuardResult(
    bool Approved,
    decimal SizeMultiplier,
    string Reason,
    bool PromotionReady,
    double DriftScore,
    string PromotionStage);
