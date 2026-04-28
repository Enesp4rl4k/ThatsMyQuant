using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Crash-recovery persistence for <see cref="PaperPortfolio"/>. Writes a JSON
/// snapshot to disk (default: <c>%LOCALAPPDATA%/ThatsMyQuant/paper_portfolio.json</c>)
/// after every trade close and at graceful shutdown, and reloads it on startup so
/// host restarts don't wipe paper positions, equity curve, and daily-PnL state.
///
/// Writes are coalesced by a 10-second quiet window — high-frequency ticks that
/// only update mark-to-market don't hammer the disk. Explicit <see cref="FlushAsync"/>
/// calls (on graceful shutdown) bypass the debounce.
/// </summary>
public sealed class PaperPortfolioPersistenceService
{
    private readonly ILogger<PaperPortfolioPersistenceService> _logger;
    private readonly string _filePath;
    private readonly TimeSpan _debounce = TimeSpan.FromSeconds(10);
    private readonly object _sync = new();
    private DateTime _lastWriteUtc = DateTime.MinValue;
    private bool _pendingWrite;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string FilePath => _filePath;

    public PaperPortfolioPersistenceService(
        ILogger<PaperPortfolioPersistenceService> logger,
        IConfiguration? configuration = null)
    {
        _logger = logger;
        var configured = configuration?["Paper:Persistence:Path"];
        _filePath = !string.IsNullOrWhiteSpace(configured)
            ? configured!
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ThatsMyQuant",
                "paper_portfolio.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
    }

    /// <summary>Load prior snapshot, or null if none exists / fails to parse.</summary>
    public PaperPortfolio? TryLoad()
    {
        if (!File.Exists(_filePath)) return null;
        try
        {
            var json = File.ReadAllText(_filePath);
            var snapshot = JsonSerializer.Deserialize<PaperPortfolio>(json, SerializerOptions);
            if (snapshot is null)
            {
                _logger.LogWarning("Paper portfolio snapshot deserialized to null: {Path}", _filePath);
                return null;
            }
            _logger.LogInformation(
                "Paper portfolio RESTORED from {Path}: Cash=${Cash:N2} Open={Open} Closed={Closed} TotalPnL=${PnL:N2}",
                _filePath, snapshot.Cash, snapshot.OpenPositions.Count, snapshot.ClosedPositions.Count, snapshot.TotalPnL);
            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Paper portfolio restore failed — starting fresh. Path={Path}", _filePath);
            return null;
        }
    }

    /// <summary>Apply snapshot into a live portfolio instance. Keeps the original reference
    /// (callers rely on <c>paperService.Portfolio</c> being the same object) and replaces
    /// its contents.</summary>
    public static void ApplySnapshot(PaperPortfolio target, PaperPortfolio snapshot)
    {
        target.InitialBalance = snapshot.InitialBalance;
        target.Cash = snapshot.Cash;
        target.DailyPnL = snapshot.DailyPnL;
        target.DailyPnLResetDate = snapshot.DailyPnLResetDate;
        target.OpenPositions.Clear();
        target.OpenPositions.AddRange(snapshot.OpenPositions);
        target.ClosedPositions.Clear();
        target.ClosedPositions.AddRange(snapshot.ClosedPositions);
        target.EquityCurve.Clear();
        target.EquityCurve.AddRange(snapshot.EquityCurve);
    }

    /// <summary>Request a write. Coalesces within the debounce window; callers do not
    /// need to await disk IO.</summary>
    public void RequestWrite(PaperPortfolio portfolio)
    {
        DateTime now = DateTime.UtcNow;
        bool shouldWriteNow;
        lock (_sync)
        {
            if (now - _lastWriteUtc < _debounce)
            {
                _pendingWrite = true;
                return;
            }
            _lastWriteUtc = now;
            _pendingWrite = false;
            shouldWriteNow = true;
        }

        if (shouldWriteNow)
        {
            _ = Task.Run(() => WriteSafeAsync(portfolio))
                .ContinueWith(t => _logger.LogWarning(t.Exception!.InnerException ?? t.Exception,
                    "Paper portfolio persistence: unobserved background write failure."),
                    TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    /// <summary>Bypass debounce and flush the current state synchronously. Call from
    /// graceful-shutdown hooks.</summary>
    public async Task FlushAsync(PaperPortfolio portfolio, CancellationToken ct = default)
    {
        lock (_sync) { _lastWriteUtc = DateTime.UtcNow; _pendingWrite = false; }
        await WriteSafeAsync(portfolio, ct).ConfigureAwait(false);
    }

    private async Task WriteSafeAsync(PaperPortfolio portfolio, CancellationToken ct = default)
    {
        try
        {
            // Atomic write: tmp → rename so a crash mid-write leaves the previous snapshot intact.
            var tmp = _filePath + ".tmp";
            var json = JsonSerializer.Serialize(portfolio, SerializerOptions);
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, _filePath, overwrite: true);

            // If another write was requested during the debounce window, schedule a follow-up.
            bool followUp;
            lock (_sync) { followUp = _pendingWrite; _pendingWrite = false; }
            if (followUp)
            {
                await Task.Delay(_debounce, ct).ConfigureAwait(false);
                await WriteSafeAsync(portfolio, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Paper portfolio persistence write failed: {Path}", _filePath);
        }
    }
}
