using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Infrastructure.Persistence;

namespace ThatsMyQuant.Bot;

/// <summary>
/// [2026-05-14 unattended hardening — Bug #6 from bug-hunt round 5]
/// SQLite WAL (write-ahead log) accumulates indefinitely under continuous write
/// load. Default `wal_autocheckpoint` is 1000 pages but the bot's mostly-INSERT
/// pattern (telemetry, trade events, alerts) never crosses the read-only barrier
/// that triggers a checkpoint. Observed at boot: quant.db=430KB, quant.db-wal=4.1MB
/// after one day. 15-day unattended uptime would push this into hundreds of MB.
///
/// Periodic maintenance:
///   1. PRAGMA wal_checkpoint(TRUNCATE) every 6h — flushes WAL into main DB and
///      shrinks the WAL file. Brief lock window (~10ms typical) tolerated.
///   2. PRAGMA optimize every 24h — runs ANALYZE on tables that need it; cheap
///      after the first run since SQLite caches the analysis.
///   3. Disk-space alarm: if free space on the DB drive drops below 2GB, fire
///      a Telegram critical (we'd rather know than crash on disk-full).
///
/// Read-only fallback: failure to checkpoint is non-fatal (logged + retry next tick).
/// </summary>
public sealed class SqliteMaintenanceHostedService : BackgroundService
{
    private readonly ILogger<SqliteMaintenanceHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IRealtimeAlerter? _alerter;

    private static readonly TimeSpan WalCheckpointInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan OptimizeInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan DiskSpaceCheckInterval = TimeSpan.FromHours(1);
    private const long DiskSpaceCriticalBytes = 2L * 1024 * 1024 * 1024;  // 2 GB

    // [2026-06-27 SQLite hardening] Automated consistent backups + corruption detection.
    // A single .db file is the whole system's memory; before this there was no snapshot to
    // recover from and no detection if it silently corrupted. VACUUM INTO produces a fully
    // consistent copy (folds in committed WAL); quick_check catches page/index corruption.
    private static readonly TimeSpan BackupInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan IntegrityInterval = TimeSpan.FromHours(24);
    private const int BackupsToKeep = 24;   // 24 x 6h = 6 days of rolling snapshots

    private DateTime _lastWalCheckpointUtc = DateTime.MinValue;
    private DateTime _lastOptimizeUtc = DateTime.MinValue;
    private DateTime _lastDiskCheckUtc = DateTime.MinValue;
    private DateTime _lastBackupUtc = DateTime.MinValue;
    private DateTime _lastIntegrityUtc = DateTime.MinValue;
    private bool _diskSpaceAlertedRecently;

    public SqliteMaintenanceHostedService(
        ILogger<SqliteMaintenanceHostedService> logger,
        IServiceProvider serviceProvider,
        IRealtimeAlerter? alerter = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _alerter = alerter;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Provider guard — every operation here is SQLite-specific (PRAGMA / VACUUM /
        // SqliteConnection cast). On Postgres this service must do nothing, so the same
        // worker binary runs cleanly once Persistence:Provider is flipped to postgres.
        using (var guardScope = _serviceProvider.CreateScope())
        {
            var providerName = guardScope.ServiceProvider.GetRequiredService<QuantDbContext>().Database.ProviderName ?? "";
            if (providerName.IndexOf("Sqlite", StringComparison.OrdinalIgnoreCase) < 0)
            {
                _logger.LogInformation(
                    "SqliteMaintenance: DB provider is '{Provider}' (not SQLite); WAL/optimize/backup/integrity maintenance disabled.",
                    providerName);
                return;
            }
        }

        _logger.LogInformation(
            "SqliteMaintenanceHostedService starting. WAL checkpoint every {W}h, optimize every {O}h, backup every {B}h (keep {K}), integrity every {I}h, disk check every {D}h (critical < {C}GB).",
            WalCheckpointInterval.TotalHours, OptimizeInterval.TotalHours, BackupInterval.TotalHours, BackupsToKeep,
            IntegrityInterval.TotalHours, DiskSpaceCheckInterval.TotalHours, DiskSpaceCriticalBytes / (1024 * 1024 * 1024));

        // Initial 30s grace for boot, then settle into hourly tick that owns scheduling.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        try
        {
            do
            {
                try { await TickAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "SqliteMaintenance tick failed."); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;

        if (nowUtc - _lastWalCheckpointUtc >= WalCheckpointInterval)
        {
            await DoWalCheckpointAsync(ct).ConfigureAwait(false);
            _lastWalCheckpointUtc = nowUtc;
        }

        if (nowUtc - _lastOptimizeUtc >= OptimizeInterval)
        {
            await DoOptimizeAsync(ct).ConfigureAwait(false);
            _lastOptimizeUtc = nowUtc;
        }

        if (nowUtc - _lastBackupUtc >= BackupInterval)
        {
            await DoBackupAsync(ct).ConfigureAwait(false);
            _lastBackupUtc = nowUtc;
        }

        if (nowUtc - _lastIntegrityUtc >= IntegrityInterval)
        {
            await DoIntegrityCheckAsync(ct).ConfigureAwait(false);
            _lastIntegrityUtc = nowUtc;
        }

        if (nowUtc - _lastDiskCheckUtc >= DiskSpaceCheckInterval)
        {
            DoDiskSpaceCheck();
            _lastDiskCheckUtc = nowUtc;
        }
    }

    private async Task DoWalCheckpointAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
            var conn = (SqliteConnection)db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct).ConfigureAwait(false);

            // Capture WAL size before
            var walPathBefore = TryGetWalSizeMB(db);

            await using var cmd = conn.CreateCommand();
            // TRUNCATE = checkpoint then shrink WAL to 0 bytes. Brief shared-write lock.
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            // Result: (busy, log_pages_checkpointed, log_pages_total)
            int busy = -1, checkpointed = -1, total = -1;
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0)) busy = reader.GetInt32(0);
                if (!reader.IsDBNull(1)) checkpointed = reader.GetInt32(1);
                if (!reader.IsDBNull(2)) total = reader.GetInt32(2);
            }
            await reader.CloseAsync().ConfigureAwait(false);

            var walPathAfter = TryGetWalSizeMB(db);
            _logger.LogInformation(
                "SQLite WAL checkpoint: busy={Busy} checkpointed={Cp} total={Tot} | WAL size {Before:F2}MB → {After:F2}MB",
                busy, checkpointed, total, walPathBefore, walPathAfter);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SQLite WAL checkpoint failed (non-fatal).");
        }
    }

    private async Task DoOptimizeAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
            var conn = (SqliteConnection)db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA optimize;";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("SQLite PRAGMA optimize completed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SQLite optimize failed (non-fatal).");
        }
    }

    private void DoDiskSpaceCheck()
    {
        try
        {
            var dbDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var drive = new DriveInfo(Path.GetPathRoot(dbDir) ?? "C:\\");
            var freeBytes = drive.AvailableFreeSpace;
            var freeMB = freeBytes / (1024.0 * 1024.0);

            if (freeBytes < DiskSpaceCriticalBytes)
            {
                if (!_diskSpaceAlertedRecently)
                {
                    _logger.LogCritical(
                        "🚨 DISK SPACE CRITICAL on {Drive}: {FreeMB:F0}MB free (< {CritGB}GB threshold). Worker may fail to persist trades / logs / snapshots.",
                        drive.Name, freeMB, DiskSpaceCriticalBytes / (1024 * 1024 * 1024));

                    if (_alerter is not null)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _alerter.SendAsync(
                                    key: "disk_space_critical",
                                    severity: AlertSeverity.Critical,
                                    title: "🚨 Disk space critical",
                                    body: $"Drive {drive.Name} has only {freeMB:F0}MB free.\n" +
                                          $"Worker may fail to persist state. Free up space NOW or worker will crash.\n" +
                                          $"Likely culprits: worker_logs/, snapshots/, quant.db-wal.",
                                    ct: CancellationToken.None).ConfigureAwait(false);
                            }
                            catch { /* best-effort */ }
                        });
                    }
                    _diskSpaceAlertedRecently = true;
                }
            }
            else
            {
                if (_diskSpaceAlertedRecently)
                {
                    _logger.LogInformation("Disk space recovered to {FreeMB:F0}MB free.", freeMB);
                }
                _diskSpaceAlertedRecently = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disk space check failed.");
        }
    }

    private async Task DoBackupAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
            var conn = (SqliteConnection)db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct).ConfigureAwait(false);

            var backupDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ThatsMyQuant", "backups");
            Directory.CreateDirectory(backupDir);
            var backupPath = Path.Combine(backupDir, $"quant_{DateTime.UtcNow:yyyyMMdd_HHmmss}.db");

            // VACUUM INTO writes a fully consistent snapshot (folds in committed WAL) without
            // blocking writers for long. No parameter binding on this statement → inline the
            // path as an escaped string literal.
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"VACUUM INTO '{backupPath.Replace("'", "''")}';";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var sizeMB = new FileInfo(backupPath).Length / (1024.0 * 1024.0);

            // Rotate — keep the newest N (filenames are timestamp-sortable).
            var stale = Directory.GetFiles(backupDir, "quant_*.db")
                .OrderByDescending(f => f)
                .Skip(BackupsToKeep)
                .ToList();
            foreach (var f in stale) { try { File.Delete(f); } catch { /* best effort */ } }

            _logger.LogInformation(
                "SQLite backup written: {Path} ({SizeMB:F2}MB). Rotation removed {N} old (keep {Keep}).",
                backupPath, sizeMB, stale.Count, BackupsToKeep);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SQLite backup (VACUUM INTO) failed.");
            FireAlert("sqlite_backup_failed", AlertSeverity.Warning, "SQLite backup failed",
                $"VACUUM INTO failed: {ex.Message}. The DB has no fresh snapshot to recover from.");
        }
    }

    private async Task DoIntegrityCheckAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
            var conn = (SqliteConnection)db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            var result = (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString() ?? "<null>";

            if (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("SQLite integrity check (quick_check): ok.");
            }
            else
            {
                _logger.LogCritical("SQLite integrity check FAILED (quick_check): {Result}", result);
                FireAlert("sqlite_integrity_failed", AlertSeverity.Critical, "SQLite integrity check FAILED",
                    $"PRAGMA quick_check returned: {result}\n" +
                    "DB may be corrupt. Restore from the newest snapshot in %LOCALAPPDATA%\\ThatsMyQuant\\backups.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SQLite integrity check failed to run.");
        }
    }

    private void FireAlert(string key, AlertSeverity severity, string title, string body)
    {
        if (_alerter is null) return;
        _ = Task.Run(async () =>
        {
            try { await _alerter.SendAsync(key, severity, title, body, CancellationToken.None).ConfigureAwait(false); }
            catch { /* best-effort */ }
        });
    }

    private static double TryGetWalSizeMB(QuantDbContext db)
    {
        try
        {
            var dataSource = ((SqliteConnection)db.Database.GetDbConnection()).DataSource;
            if (string.IsNullOrEmpty(dataSource)) return 0.0;
            var walPath = dataSource + "-wal";
            if (!File.Exists(walPath)) return 0.0;
            return new FileInfo(walPath).Length / (1024.0 * 1024.0);
        }
        catch { return 0.0; }
    }
}
