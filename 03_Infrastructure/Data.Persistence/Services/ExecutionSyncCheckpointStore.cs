using System.Text.Json;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Persists execution-report checkpoints to protect against duplicate/out-of-order replays across reconnects/restarts.
/// </summary>
public sealed class ExecutionSyncCheckpointStore
{
    private readonly string _checkpointPath;
    private readonly object _sync = new();
    private Dictionary<string, CheckpointEntry> _entries;

    public ExecutionSyncCheckpointStore(string checkpointPath)
    {
        _checkpointPath = checkpointPath;
        _entries = Load();
    }

    public CheckpointDecision Evaluate(ExecutionReport report)
    {
        if (string.IsNullOrWhiteSpace(report.ClientOrderId))
        {
            return CheckpointDecision.Apply;
        }

        lock (_sync)
        {
            if (!_entries.TryGetValue(report.ClientOrderId, out var existing))
            {
                return CheckpointDecision.Apply;
            }

            var incomingTicks = report.TimestampUtc == default
                ? 0L
                : report.TimestampUtc.ToUniversalTime().Ticks;

            if (incomingTicks < existing.LastTimestampTicks)
            {
                return CheckpointDecision.OutOfOrder;
            }

            if (incomingTicks == existing.LastTimestampTicks &&
                string.Equals(report.Status, existing.LastStatus, StringComparison.OrdinalIgnoreCase) &&
                report.FilledQuantity == existing.LastFilledQuantity &&
                report.RemainingQuantity == existing.LastRemainingQuantity)
            {
                return CheckpointDecision.Duplicate;
            }

            // Same timestamp but regressing filled quantity/remaining progression is treated as stale.
            if (incomingTicks == existing.LastTimestampTicks &&
                (report.FilledQuantity < existing.LastFilledQuantity ||
                 report.RemainingQuantity > existing.LastRemainingQuantity))
            {
                return CheckpointDecision.OutOfOrder;
            }

            return CheckpointDecision.Apply;
        }
    }

    public bool ShouldApply(ExecutionReport report)
        => Evaluate(report) == CheckpointDecision.Apply;

    public void Commit(ExecutionReport report)
    {
        if (string.IsNullOrWhiteSpace(report.ClientOrderId))
        {
            return;
        }

        lock (_sync)
        {
            _entries[report.ClientOrderId] = new CheckpointEntry(
                report.TimestampUtc == default ? DateTime.UtcNow.Ticks : report.TimestampUtc.ToUniversalTime().Ticks,
                report.Status ?? string.Empty,
                report.FilledQuantity,
                report.RemainingQuantity);

            // Keep size bounded so this stays lightweight.
            if (_entries.Count > 10_000)
            {
                _entries = _entries
                    .OrderByDescending(kvp => kvp.Value.LastTimestampTicks)
                    .Take(10_000)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
            }

            Save(_entries);
        }
    }

    private Dictionary<string, CheckpointEntry> Load()
    {
        try
        {
            if (!File.Exists(_checkpointPath))
            {
                return new Dictionary<string, CheckpointEntry>(StringComparer.Ordinal);
            }

            var json = File.ReadAllText(_checkpointPath);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, CheckpointEntry>>(json);
            return parsed is null
                ? new Dictionary<string, CheckpointEntry>(StringComparer.Ordinal)
                : new Dictionary<string, CheckpointEntry>(parsed, StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, CheckpointEntry>(StringComparer.Ordinal);
        }
    }

    private void Save(Dictionary<string, CheckpointEntry> entries)
    {
        try
        {
            var dir = Path.GetDirectoryName(_checkpointPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(entries);
            File.WriteAllText(_checkpointPath, json);
        }
        catch
        {
            // Never crash execution sync because checkpoint persistence fails.
        }
    }

    public sealed record CheckpointEntry(
        long LastTimestampTicks,
        string LastStatus,
        decimal LastFilledQuantity,
        decimal LastRemainingQuantity);

    public enum CheckpointDecision
    {
        Apply = 0,
        Duplicate = 1,
        OutOfOrder = 2
    }
}
