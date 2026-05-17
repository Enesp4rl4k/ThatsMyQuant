using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Bot;

/// <summary>
/// [2026-05-14 unattended hardening] At every worker boot, snapshot the active
/// configuration to a timestamped JSON file under
/// <c>%LOCALAPPDATA%\ThatsMyQuant\config_snapshots\</c>. Also writes a stable hash
/// to a single index file so 15 days later we can answer "did the config change
/// during the unattended period?" by looking at the index.
///
/// Captures only keys we care about (Risk:*, OrderExecution:*, Paper:*, EdgeLab:*,
/// Telegram:DigestTimes, etc.) — NOT the full config tree which would leak credentials.
/// Credentials are explicitly skipped (BotToken, ApiKey, Secret, Passphrase).
///
/// Retention: keeps last 30 snapshot files; older are pruned at boot.
/// </summary>
public sealed class ConfigSnapshotHostedService : IHostedService
{
    private readonly ILogger<ConfigSnapshotHostedService> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _snapshotDir;
    private readonly string _indexPath;

    private const int RetentionCount = 30;

    // Keys to capture. Credentials and connection strings are EXCLUDED.
    private static readonly string[] CapturePrefixes =
    [
        "Risk:",
        "OrderExecution:",
        "Paper:",
        "Edge:",
        "EdgeLab:",
        "Promotion:",
        "FundingHybrid:",
        "PairsHybrid:",
        "PairwiseCorrelation:",
        "Hrp:",
        "Telegram:DigestTimes",
        "Telegram:DailyDigestTime",
        "Reconciliation:",
        "RiskGovernor:",
        "Inference:",
        "Backtest:",
    ];

    private static readonly string[] ForbiddenKeySubstrings =
    [
        "BotToken", "ApiKey", "Secret", "Passphrase", "ConnectionString", "Token",
    ];

    public ConfigSnapshotHostedService(
        ILogger<ConfigSnapshotHostedService> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;

        var stateDir = Environment.ExpandEnvironmentVariables(
            configuration["StateDirectory"] ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ThatsMyQuant"));
        _snapshotDir = Path.Combine(stateDir, "config_snapshots");
        _indexPath = Path.Combine(_snapshotDir, "_index.json");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_snapshotDir);

            // Capture filtered config dictionary
            var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);
            CollectConfig(_configuration, "", snapshot);

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            var hash = ComputeHash(json);
            var ts = DateTime.UtcNow;
            var fileName = $"snapshot_{ts:yyyyMMdd_HHmmss}_{hash[..8]}.json";
            var filePath = Path.Combine(_snapshotDir, fileName);
            File.WriteAllText(filePath, json);

            // Update index
            var index = TryLoadIndex();
            index.Add(new IndexEntry
            {
                Timestamp = ts.ToString("o"),
                FileName = fileName,
                Hash = hash,
                ChangedFromPrevious = index.Count == 0 || index[^1].Hash != hash,
            });
            File.WriteAllText(_indexPath, JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));

            // Prune old snapshots
            PruneOld();

            var changedNote = index.Count > 1 && index[^1].ChangedFromPrevious
                ? "CONFIG CHANGED since previous boot"
                : "config unchanged since previous boot";
            _logger.LogInformation(
                "ConfigSnapshot wrote {Count} keys to {File} (hash={Hash}, {Status}). Index has {N} snapshots.",
                snapshot.Count, fileName, hash[..12], changedNote, index.Count);
        }
        catch (Exception ex)
        {
            // Non-fatal: snapshot is observability, not safety
            _logger.LogWarning(ex, "ConfigSnapshot at boot failed.");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void CollectConfig(IConfiguration cfg, string prefix, SortedDictionary<string, string> sink)
    {
        foreach (var child in cfg.GetChildren())
        {
            var key = string.IsNullOrEmpty(prefix) ? child.Key : $"{prefix}:{child.Key}";

            // Skip forbidden patterns (credentials)
            if (ForbiddenKeySubstrings.Any(f => key.Contains(f, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Only capture under known prefixes (avoids dumping internal framework keys)
            var matchesPrefix = CapturePrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal) || key == p.TrimEnd(':'));
            if (matchesPrefix)
            {
                if (child.Value is not null)
                {
                    sink[key] = child.Value;
                }
            }

            // Recurse regardless so we capture nested keys under matching prefixes
            CollectConfig(child, key, sink);
        }
    }

    private static string ComputeHash(string content)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private List<IndexEntry> TryLoadIndex()
    {
        try
        {
            if (!File.Exists(_indexPath)) return new List<IndexEntry>();
            return JsonSerializer.Deserialize<List<IndexEntry>>(File.ReadAllText(_indexPath))
                ?? new List<IndexEntry>();
        }
        catch
        {
            return new List<IndexEntry>();
        }
    }

    private void PruneOld()
    {
        try
        {
            var snapshots = Directory.GetFiles(_snapshotDir, "snapshot_*.json")
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                .ToList();
            if (snapshots.Count <= RetentionCount) return;

            foreach (var old in snapshots.Skip(RetentionCount))
            {
                try { File.Delete(old); } catch { /* best-effort */ }
            }
        }
        catch { /* non-fatal */ }
    }

    private sealed class IndexEntry
    {
        public string Timestamp { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public bool ChangedFromPrevious { get; set; }
    }
}
