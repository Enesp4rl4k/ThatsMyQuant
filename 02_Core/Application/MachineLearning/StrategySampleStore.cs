using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Application.MachineLearning;

/// <summary>
/// Append-only JSONL sample store for strategy-specific ML training data. Decoupled from
/// the legacy <see cref="ModelTrainingPipeline"/> (which has a hardcoded 5-element
/// directional tensor) so funding-alpha and pairs-trading samples — with completely
/// different feature spaces (z-score, half-life, funding momentum, etc.) — can accumulate
/// alongside live paper testing WITHOUT contaminating the existing model.
///
/// Format: one JSON object per line in `%LOCALAPPDATA%/ThatsMyQuant/ml_samples/{StrategyTag}_v1.jsonl`.
/// Each record is self-describing — feature names + values + label + outcome metadata.
/// Schema-on-read: when training time comes (post-Tier-A, ~6 months), the trainer
/// loads N records, decides on the feature subset to use, and trains.
///
/// Why JSONL instead of SQLite/CSV:
///   • Append-only thread-safe (single FileStream lock per strategy)
///   • Git-friendly (text-diffable for sample-set audits)
///   • No schema migration when feature set evolves
///   • Stream-readable for big-data scenarios (line-by-line)
///   • One file per strategy = isolated sample boundaries (no cross-contamination
///     between funding-alpha and pairs labels in the same training pool)
///
/// Disk: ~500 bytes/sample × 1000 samples = 500 KB/strategy. Trivial.
/// </summary>
public interface IStrategySampleStore
{
    /// <summary>Append a labeled sample to the strategy's per-tag JSONL file.</summary>
    void Append(StrategySample sample);

    /// <summary>Total count of samples currently stored for this strategy tag.</summary>
    int Count(string strategyTag);

    /// <summary>Read all samples for a strategy (training-time use; lazy line-by-line).</summary>
    IEnumerable<StrategySample> ReadAll(string strategyTag);
}

public sealed class StrategySampleStore : IStrategySampleStore
{
    private readonly ILogger<StrategySampleStore> _logger;
    private readonly string _rootPath;
    private readonly Dictionary<string, object> _writeLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _countCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _registryLock = new();

    public StrategySampleStore(ILogger<StrategySampleStore> logger, string? rootPath = null)
    {
        _logger = logger;
        _rootPath = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ThatsMyQuant",
            "ml_samples");
        Directory.CreateDirectory(_rootPath);
    }

    public void Append(StrategySample sample)
    {
        if (string.IsNullOrWhiteSpace(sample.StrategyTag))
        {
            _logger.LogWarning("StrategySampleStore.Append: empty StrategyTag — sample dropped");
            return;
        }

        var fileName = SanitizeFileName(sample.StrategyTag) + "_v1.jsonl";
        var path = Path.Combine(_rootPath, fileName);
        var writeLock = GetOrCreateLock(sample.StrategyTag);

        try
        {
            var line = JsonSerializer.Serialize(sample, JsonOptions);
            lock (writeLock)
            {
                File.AppendAllText(path, line + Environment.NewLine);
                _countCache[sample.StrategyTag] = _countCache.TryGetValue(sample.StrategyTag, out var n) ? n + 1 : 1;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategySampleStore.Append failed for {Strategy}", sample.StrategyTag);
        }
    }

    public int Count(string strategyTag)
    {
        if (string.IsNullOrWhiteSpace(strategyTag)) return 0;
        lock (_registryLock)
        {
            if (_countCache.TryGetValue(strategyTag, out var cached)) return cached;
        }
        var fileName = SanitizeFileName(strategyTag) + "_v1.jsonl";
        var path = Path.Combine(_rootPath, fileName);
        if (!File.Exists(path)) return 0;
        // Cold load: count lines once, cache.
        var n = File.ReadLines(path).Count();
        lock (_registryLock) { _countCache[strategyTag] = n; }
        return n;
    }

    public IEnumerable<StrategySample> ReadAll(string strategyTag)
    {
        var fileName = SanitizeFileName(strategyTag) + "_v1.jsonl";
        var path = Path.Combine(_rootPath, fileName);
        if (!File.Exists(path)) yield break;
        using var reader = new StreamReader(path);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            StrategySample? parsed = null;
            try { parsed = JsonSerializer.Deserialize<StrategySample>(line, JsonOptions); }
            catch { /* swallow corrupted line */ }
            if (parsed is not null) yield return parsed;
        }
    }

    private object GetOrCreateLock(string strategyTag)
    {
        lock (_registryLock)
        {
            if (!_writeLocks.TryGetValue(strategyTag, out var l))
            {
                l = new object();
                _writeLocks[strategyTag] = l;
            }
            return l;
        }
    }

    private static string SanitizeFileName(string raw)
    {
        var safe = raw;
        foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
        return safe;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

/// <summary>
/// Per-trade ML sample with strategy-specific features. The Features dictionary is
/// schema-on-read — different strategy tags emit different feature sets, and the
/// trainer at the other end picks which subset matters. Outcome fields are normalized
/// across strategies so cross-strategy meta-learning is possible later.
/// </summary>
public sealed record StrategySample
{
    /// <summary>Strategy tag (FundingAlpha-Hybrid-Long etc.) — also the file partition key.</summary>
    public string StrategyTag { get; init; } = "";

    /// <summary>Symbol / pair-key / candidate-id — for attribution and grouping.</summary>
    public string Symbol { get; init; } = "";

    /// <summary>Strategy-specific feature vector (name → value). Examples:
    /// funding: { funding_rate, funding_zscore_30d, oi_change_4h, settlement_distance_min, ... }
    /// pairs:   { z_entry, half_life_bars, hedge_ratio, adf_t_stat, hurst, correlation_60bar, ... }
    /// </summary>
    public Dictionary<string, double> Features { get; init; } = new();

    /// <summary>Binary outcome label (NetPnl &gt; 0 by default; a future trainer may
    /// re-derive labels with cost-aware thresholds).</summary>
    public bool Label { get; init; }

    /// <summary>Numerical outcome — quote-currency net PnL, signed.</summary>
    public decimal NetPnl { get; init; }

    /// <summary>Real fee paid (when measured by venue) — null in dry-run / pure simulation.</summary>
    public decimal? RealFeePaid { get; init; }

    /// <summary>Real slippage paid (when mark prices captured) — null otherwise.</summary>
    public decimal? RealSlippagePaid { get; init; }

    public DateTime EntryTimeUtc { get; init; }
    public DateTime ExitTimeUtc { get; init; }
    public string ExitReason { get; init; } = "";
    public string ExecutionMode { get; init; } = "";
    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Schema version of the feature dictionary — bump when features change semantics.</summary>
    public int FeatureSchemaVersion { get; init; } = 1;
}
