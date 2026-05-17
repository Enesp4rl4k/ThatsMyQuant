using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Daily CSV dumper for the realized-PnL feedback channel from TMQ → Quantlab.
/// Added 2026-05-08 for the $2K capital cycle (Quantlab handoff:
/// HANDOFF_TMQ_2K_PIVOT_2026_05_08.md §5).
///
/// Reads each EdgeLab strategy's append-only reconciliation JSONL ledger
/// (cycle-level hypothetical PnL) and writes a daily CSV at:
///   {QuantlabRoot}/data/realized_pnl/{strategy}/{YYYY-MM-DD}.csv
///
/// The CSV is what Quantlab's <c>scripts/sanity_realized_vs_backtest.py</c>
/// (planned, week 1-2 of master plan) consumes to compare realized PnL vs
/// backtest claims. Critical for the $2K cycle because compound velocity is
/// fragile to backtest/realized gap.
///
/// Schema (per-cycle row, NOT per-trade):
///   timestamp_utc,as_of_bar,prev_as_of_bar,period_pnl_usd,cumulative_pnl_usd,
///   basis_usd,positions_held,per_symbol_pnl_json
///
/// A future iteration can add per-trade CSV (entry/exit dt + px + size + fees)
/// once the per-trade ledger is wired; for now per-cycle is sufficient for
/// realized-vs-backtest comparison at the strategy level.
///
/// Cadence: emits once per day at 23:30 UTC by default. Idempotent — re-running
/// rewrites the same daily CSV; downstream Quantlab consumer reads the latest.
/// </summary>
public sealed class EdgeLabRealizedPnLDumperHostedService : BackgroundService
{
    private readonly ILogger<EdgeLabRealizedPnLDumperHostedService> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _ledgerDir;
    private readonly string _outputRoot;
    private readonly TimeSpan _runDailyAtUtc;
    private readonly bool _enabled;

    public EdgeLabRealizedPnLDumperHostedService(
        ILogger<EdgeLabRealizedPnLDumperHostedService> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _ledgerDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ThatsMyQuant");
        _outputRoot = configuration["EdgeLabRealizedPnLDumper:OutputRoot"]
                      ?? "C:/Users/parlak/Downloads/Quantlab/data/realized_pnl";
        var hour = int.TryParse(configuration["EdgeLabRealizedPnLDumper:RunHourUtc"], out var h) ? h : 23;
        var min = int.TryParse(configuration["EdgeLabRealizedPnLDumper:RunMinuteUtc"], out var m) ? m : 30;
        _runDailyAtUtc = new TimeSpan(hour, min, 0);
        _enabled = bool.TryParse(configuration["EdgeLabRealizedPnLDumper:Enabled"], out var e) ? e : true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("EdgeLabRealizedPnLDumper disabled.");
            return;
        }

        _logger.LogInformation(
            "EdgeLabRealizedPnLDumper starting. LedgerDir={Ledger} OutputRoot={Out} RunAt={At} UTC",
            _ledgerDir, _outputRoot, _runDailyAtUtc);

        // First emit immediately so we don't wait 24h for first dump
        try { DumpAllStrategies(); }
        catch (Exception ex) { _logger.LogError(ex, "EdgeLabRealizedPnLDumper initial dump failed."); }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var nextRun = now.Date + _runDailyAtUtc;
                if (nextRun <= now) nextRun = nextRun.AddDays(1);
                var delay = nextRun - now;
                _logger.LogDebug(
                    "EdgeLabRealizedPnLDumper: next dump in {Delay}", delay);
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                DumpAllStrategies();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EdgeLabRealizedPnLDumper iteration failed.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private void DumpAllStrategies()
    {
        if (!Directory.Exists(_ledgerDir))
        {
            _logger.LogWarning(
                "EdgeLabRealizedPnLDumper: ledger dir does not exist yet: {Dir}", _ledgerDir);
            return;
        }
        // Pattern: edgelab_dryrun_pnl.jsonl (canonical) or edgelab_dryrun_pnl_<strategy>.jsonl
        var files = Directory.GetFiles(_ledgerDir, "edgelab_dryrun_pnl*.jsonl");
        if (files.Length == 0)
        {
            _logger.LogInformation("EdgeLabRealizedPnLDumper: no ledger files found, idle.");
            return;
        }
        Directory.CreateDirectory(_outputRoot);

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var totalRows = 0;
        foreach (var ledgerPath in files)
        {
            var name = Path.GetFileNameWithoutExtension(ledgerPath);
            // edgelab_dryrun_pnl => "" (canonical), edgelab_dryrun_pnl_<strategy> => <strategy>
            var prefix = "edgelab_dryrun_pnl";
            var strategy = name.Length > prefix.Length && name.StartsWith(prefix + "_")
                ? name[(prefix.Length + 1)..]
                : "realized_vol_4h_xs_reverse"; // canonical ledger

            try
            {
                var rows = DumpStrategyToCsv(ledgerPath, strategy, today);
                totalRows += rows;
                _logger.LogInformation(
                    "EdgeLabRealizedPnLDumper: wrote {Rows} rows for strategy={Strategy}",
                    rows, strategy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "EdgeLabRealizedPnLDumper: dump failed for ledger={Ledger}", ledgerPath);
            }
        }
        _logger.LogInformation(
            "EdgeLabRealizedPnLDumper completed. {Total} total rows across {Strategies} strategies.",
            totalRows, files.Length);
    }

    private int DumpStrategyToCsv(string ledgerPath, string strategy, string dateStr)
    {
        var stratDir = Path.Combine(_outputRoot, strategy);
        Directory.CreateDirectory(stratDir);
        var csvPath = Path.Combine(stratDir, $"{dateStr}.csv");

        // Read ALL rows from JSONL (small files; ledgers cap at ~30d typical)
        var lines = File.ReadAllLines(ledgerPath);
        if (lines.Length == 0)
            return 0;

        var sb = new StringBuilder();
        sb.AppendLine("timestamp_utc,as_of_bar,prev_as_of_bar,period_pnl_usd," +
                      "cumulative_pnl_usd,basis_usd,positions_held,per_symbol_pnl_json");

        var rowCount = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var ts = root.GetProperty("TimestampUtc").GetDateTime();
                var asOf = root.GetProperty("ThisCycleAsOfBar").GetDateTimeOffset();
                var prevAsOf = root.TryGetProperty("PrevCycleAsOfBar", out var p) && p.ValueKind != JsonValueKind.Null
                    ? p.GetDateTimeOffset().ToString("o", CultureInfo.InvariantCulture) : "";
                var periodPnl = root.GetProperty("PeriodPnLUsd").GetDecimal();
                var cumPnl = root.GetProperty("CumulativePnLUsd").GetDecimal();
                var basis = root.GetProperty("Basis").GetDecimal();
                var nPositions = root.GetProperty("PositionsHeld").GetInt32();
                var perSym = root.TryGetProperty("PerSymbolPnL", out var ps)
                    ? ps.GetRawText().Replace("\"", "\"\"")
                    : "{}";

                sb.AppendLine(string.Join(",",
                    ts.ToString("o", CultureInfo.InvariantCulture),
                    asOf.ToString("o", CultureInfo.InvariantCulture),
                    prevAsOf,
                    periodPnl.ToString(CultureInfo.InvariantCulture),
                    cumPnl.ToString(CultureInfo.InvariantCulture),
                    basis.ToString(CultureInfo.InvariantCulture),
                    nPositions.ToString(CultureInfo.InvariantCulture),
                    "\"" + perSym + "\""));
                rowCount++;
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex,
                    "EdgeLabRealizedPnLDumper: skipping malformed JSONL line in {Path}", ledgerPath);
            }
        }

        File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);
        return rowCount;
    }
}
