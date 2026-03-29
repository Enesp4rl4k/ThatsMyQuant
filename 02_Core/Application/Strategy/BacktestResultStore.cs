using System.Text.Json;
using System.Text.Json.Serialization;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Persists backtest and walk-forward results to JSON files for comparison across runs.
/// Results are stored in a configurable directory with timestamped filenames.
/// </summary>
public sealed class BacktestResultStore
{
    private readonly string _baseDir;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public BacktestResultStore(string baseDir = "backtest_results")
    {
        _baseDir = baseDir;
        Directory.CreateDirectory(_baseDir);
    }

    /// <summary>Saves a backtest result and returns the file path.</summary>
    public async Task<string> SaveBacktestAsync(BacktestResult result, string? tag = null)
    {
        var envelope = new BacktestEnvelope
        {
            RunId = Guid.NewGuid().ToString("N")[..8],
            Timestamp = DateTime.UtcNow,
            Tag = tag ?? "",
            Symbol = result.Symbol,
            Result = result
        };

        string fileName = $"{envelope.Timestamp:yyyyMMdd_HHmmss}_{result.Symbol}_{envelope.RunId}.json";
        string path = Path.Combine(_baseDir, fileName);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(envelope, JsonOptions)).ConfigureAwait(false);
        return path;
    }

    /// <summary>Saves a walk-forward result and returns the file path.</summary>
    public async Task<string> SaveWalkForwardAsync(WalkForwardResult result, string? tag = null)
    {
        var envelope = new WalkForwardEnvelope
        {
            RunId = Guid.NewGuid().ToString("N")[..8],
            Timestamp = DateTime.UtcNow,
            Tag = tag ?? "",
            Symbol = result.Symbol,
            Result = result
        };

        string fileName = $"wf_{envelope.Timestamp:yyyyMMdd_HHmmss}_{result.Symbol}_{envelope.RunId}.json";
        string path = Path.Combine(_baseDir, fileName);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(envelope, JsonOptions)).ConfigureAwait(false);
        return path;
    }

    /// <summary>Lists all saved backtest results, newest first.</summary>
    public List<BacktestEnvelope> ListBacktests(string? symbol = null)
    {
        var results = new List<BacktestEnvelope>();
        foreach (var file in Directory.GetFiles(_baseDir, "*.json").Where(f => !Path.GetFileName(f).StartsWith("wf_")))
        {
            try
            {
                var json = File.ReadAllText(file);
                var envelope = JsonSerializer.Deserialize<BacktestEnvelope>(json, JsonOptions);
                if (envelope != null && (symbol == null || envelope.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)))
                    results.Add(envelope);
            }
            catch (Exception ex)
            {
                // Log but skip corrupt/unreadable files
                System.Diagnostics.Debug.WriteLine($"[BacktestResultStore] Failed to parse {file}: {ex.Message}");
            }
        }
        return results.OrderByDescending(r => r.Timestamp).ToList();
    }

    /// <summary>Lists all saved walk-forward results, newest first.</summary>
    public List<WalkForwardEnvelope> ListWalkForwards(string? symbol = null)
    {
        var results = new List<WalkForwardEnvelope>();
        foreach (var file in Directory.GetFiles(_baseDir, "wf_*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var envelope = JsonSerializer.Deserialize<WalkForwardEnvelope>(json, JsonOptions);
                if (envelope != null && (symbol == null || envelope.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)))
                    results.Add(envelope);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BacktestResultStore] Failed to parse {file}: {ex.Message}");
            }
        }
        return results.OrderByDescending(r => r.Timestamp).ToList();
    }

    /// <summary>Prints a comparison table of recent backtest results.</summary>
    public static void PrintComparison(IReadOnlyList<BacktestEnvelope> runs)
    {
        if (runs.Count == 0) { Console.WriteLine("No results to compare."); return; }

        Console.WriteLine($"{"RunId",-10} {"Date",-12} {"Symbol",-10} {"Trades",7} {"WR%",6} {"PnL",12} {"PF",6} {"Sharpe",7} {"MaxDD%",7} {"Tag"}");
        Console.WriteLine(new string('-', 90));

        foreach (var r in runs)
        {
            var b = r.Result;
            Console.WriteLine($"{r.RunId,-10} {r.Timestamp:yyyy-MM-dd} {b.Symbol,-10} {b.TotalTrades,7} {b.WinRate,5:F1}% {b.TotalPnL,11:C} {b.ProfitFactor,5:F2}x {b.SharpeRatio,6:F2} {b.MaxDrawdownPercent,6:F2}% {r.Tag}");
        }
    }
}

public record BacktestEnvelope
{
    public string RunId { get; init; } = "";
    public DateTime Timestamp { get; init; }
    public string Tag { get; init; } = "";
    public string Symbol { get; init; } = "";
    public BacktestResult Result { get; init; } = new();
}

public record WalkForwardEnvelope
{
    public string RunId { get; init; } = "";
    public DateTime Timestamp { get; init; }
    public string Tag { get; init; } = "";
    public string Symbol { get; init; } = "";
    public WalkForwardResult Result { get; init; } = new();
}
