using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Daily snapshot of critical state files to a rotating backup folder. Disk failure
/// or corruption mid-trade is the single risk that can zero the account history
/// overnight (Risk #5 from the 2026-05-01 risk playbook). Postgres migration is
/// "ready" but un-flipped; until then SQLite + paper_portfolio.json + funding/pairs
/// state files are the canonical record.
///
/// Runs every 24h (default 03:30 UTC, off-cycle from settlement boundaries).
/// Backups rotated: keep last 30 daily snapshots in `%LOCALAPPDATA%/ThatsMyQuant/backups/YYYYMMDD/`.
/// Total disk: ~30 × 100 MB worst-case = 3 GB. Old backups auto-deleted.
///
/// What's backed up:
///   - quant.db (SQLite — Trade rows, ServiceHeartbeats, MlSamples)
///   - paper_portfolio.json
///   - funding_long_state.json, funding_short_state.json, funding_hybrid_state.json
///   - pairs_long_state.json, pairs_short_state.json, pairs_hybrid_state.json
///   - spread_snapshots.csv (organic historical spread series)
/// </summary>
public sealed class DailyBackupHostedService : BackgroundService
{
    private readonly ILogger<DailyBackupHostedService> _logger;
    private readonly IRealtimeAlerter? _alerter;
    private readonly string _stateDir;
    private readonly string _backupRoot;
    private readonly TimeSpan _runOffsetUtc;
    private readonly int _retentionDays;

    public DailyBackupHostedService(
        ILogger<DailyBackupHostedService> logger,
        IConfiguration cfg,
        IRealtimeAlerter? alerter = null)
    {
        _logger = logger;
        _alerter = alerter;
        _stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThatsMyQuant");
        _backupRoot = Path.Combine(_stateDir, "backups");
        Directory.CreateDirectory(_backupRoot);

        var hh = int.TryParse(cfg["Backup:RunHourUtc"], out var h) && h is >= 0 and <= 23 ? h : 3;
        var mm = int.TryParse(cfg["Backup:RunMinuteUtc"], out var m) && m is >= 0 and <= 59 ? m : 30;
        _runOffsetUtc = new TimeSpan(hh, mm, 0);
        _retentionDays = int.TryParse(cfg["Backup:RetentionDays"], out var r) && r > 0 ? r : 30;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "DailyBackupHostedService started. Run at {H:D2}:{M:D2} UTC daily. Retention {R} days. Root: {Root}",
            _runOffsetUtc.Hours, _runOffsetUtc.Minutes, _retentionDays, _backupRoot);

        // First run: 60 seconds after startup so a freshly-started worker has at least one
        // backup before its first trading day. Subsequent runs hit the configured time.
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        await SafeRunOnce("startup-snapshot").ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            var nowUtc = DateTime.UtcNow;
            var todayRunUtc = nowUtc.Date + _runOffsetUtc;
            var nextRunUtc = nowUtc < todayRunUtc ? todayRunUtc : todayRunUtc.AddDays(1);
            var sleep = nextRunUtc - nowUtc;
            try { await Task.Delay(sleep, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            await SafeRunOnce("daily").ConfigureAwait(false);
            PruneOldBackups();
        }
    }

    private async Task SafeRunOnce(string label)
    {
        try
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var dir = Path.Combine(_backupRoot, stamp);
            Directory.CreateDirectory(dir);

            string[] candidates =
            {
                "quant.db",
                "paper_portfolio.json",
                "funding_long_state.json",
                "funding_short_state.json",
                "funding_hybrid_state.json",
                "pairs_long_state.json",
                "pairs_short_state.json",
                "pairs_hybrid_state.json",
                "spread_snapshots.csv"
            };

            int copied = 0;
            long totalBytes = 0;
            foreach (var name in candidates)
            {
                var src = Path.Combine(_stateDir, name);
                if (!File.Exists(src)) continue;
                try
                {
                    var dest = Path.Combine(dir, name);
                    await using (var s = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    await using (var d = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await s.CopyToAsync(d).ConfigureAwait(false);
                    }
                    totalBytes += new FileInfo(dest).Length;
                    copied++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Backup copy failed for {File}", name);
                }
            }

            _logger.LogInformation(
                "Backup [{Label}] done: {Files} files, {Bytes:N0} bytes → {Dir}",
                label, copied, totalBytes, dir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DailyBackup run failed.");
            _ = _alerter?.SendAsync("backup_fail", AlertSeverity.Warning,
                "Daily backup failed",
                $"Backup run threw — check worker logs. {ex.Message}",
                CancellationToken.None);
        }
    }

    private void PruneOldBackups()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
            foreach (var d in Directory.EnumerateDirectories(_backupRoot))
            {
                var info = new DirectoryInfo(d);
                if (info.CreationTimeUtc < cutoff)
                {
                    try { Directory.Delete(d, recursive: true); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Prune skipped {Dir}", d); }
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Backup prune failed"); }
    }
}
