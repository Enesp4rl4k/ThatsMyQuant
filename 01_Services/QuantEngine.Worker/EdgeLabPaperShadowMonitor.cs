using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Strategy.EdgeLab;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Periodic paper-shadow Sharpe / cumulative-PnL emitter for ALL EdgeLab strategies
/// (canonical + AdditionalStrategies). Reads each strategy's reconciliation ledger
/// (jsonl, one row per cycle = per timeframe bar) and emits one summary log line per
/// strategy on each tick, so the operator can watch real Sharpe converge over the
/// 30-day promotion window WITHOUT having to grep raw jsonl files.
///
/// Specifically aimed at the new 2026-05-03 strategies (vol_of_vol / funding_percentile /
/// vol_regime) whose reported backtest Sharpes (5.27 / 7.68 / 3.13) are implausibly
/// high — paper-shadow will reveal the realistic-execution distribution. The promotion
/// gate per memory funding_universe_rebuild_2026_05_03 is N≥30 + Sharpe≥0.5 + DSR p>0.7.
///
/// Cadence: emit at startup (cold telemetry) + every PaperShadowEmitIntervalHours
/// (default 6h — matches Quantlab signal cadence). Read-only; never writes ledgers.
/// </summary>
public sealed class EdgeLabPaperShadowMonitor : BackgroundService
{
    private readonly ILogger<EdgeLabPaperShadowMonitor> _logger;
    private readonly IConfiguration _configuration;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _initialDelay;
    private readonly string _dataDir;

    public EdgeLabPaperShadowMonitor(
        ILogger<EdgeLabPaperShadowMonitor> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        var hours = double.TryParse(configuration["EdgeLab:PaperShadowEmitIntervalHours"], out var h) && h > 0
            ? h : 6.0;
        _interval = TimeSpan.FromHours(hours);
        _initialDelay = TimeSpan.FromMinutes(5);  // give other services time to boot + first cycle
        _dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ThatsMyQuant");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogWarning(
            "EdgeLabPaperShadowMonitor starting. Interval={Interval}h DataDir={Dir}",
            _interval.TotalHours, _dataDir);

        try { await Task.Delay(_initialDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                try { EmitSummaries(); }
                catch (Exception ex) { _logger.LogError(ex, "EdgeLabPaperShadowMonitor tick failed."); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private void EmitSummaries()
    {
        // Canonical strategy uses unsuffixed ledger; additional strategies use per-strategy paths.
        var strategies = new List<(string Name, string LedgerPath)>
        {
            (_configuration["EdgeLab:Strategy"] ?? "realized_vol_4h_xs_reverse",
             Path.Combine(_dataDir, "edgelab_dryrun_pnl.jsonl")),
        };
        var additional = _configuration.GetSection("EdgeLab:AdditionalStrategies").Get<string[]>()
            ?? Array.Empty<string>();
        foreach (var s in additional)
            strategies.Add((s, Path.Combine(_dataDir, $"edgelab_dryrun_pnl_{s}.jsonl")));

        foreach (var (name, path) in strategies)
        {
            try { EmitSummaryFor(name, path); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Paper-shadow summary read failed for {Strategy} at {Path}", name, path);
            }
        }
    }

    private void EmitSummaryFor(string strategy, string ledgerPath)
    {
        if (!File.Exists(ledgerPath))
        {
            _logger.LogInformation(
                "[paper-shadow] {Strategy}: no ledger yet (waiting for first reconciliation cycle)", strategy);
            return;
        }

        var pnls = new List<decimal>();
        decimal cumulative = 0m;
        DateTime? firstTs = null;
        DateTime? lastTs = null;
        foreach (var line in File.ReadLines(ledgerPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            EdgeLabReconciliationEntry? entry;
            try { entry = JsonSerializer.Deserialize<EdgeLabReconciliationEntry>(line); }
            catch { continue; }
            if (entry is null) continue;
            pnls.Add(entry.PeriodPnLUsd);
            cumulative = entry.CumulativePnLUsd;  // last wins
            firstTs ??= entry.TimestampUtc;
            lastTs = entry.TimestampUtc;
        }

        if (pnls.Count < 2)
        {
            _logger.LogInformation(
                "[paper-shadow] {Strategy}: N={N} (need ≥2 for Sharpe; ≥30 for promotion gate)",
                strategy, pnls.Count);
            return;
        }

        // Per-cycle metrics (cycle = 1 timeframe bar, e.g. 4h for these strategies).
        // Annualization assumes 4h cycle: 6 cycles/day × 365 = 2190 cycles/year ⇒ √2190 ≈ 46.8.
        // Tunable: we use this constant since all current EdgeLab strategies are 4h.
        // If a future strategy is 1h, add per-strategy timeframe lookup; today this approximation is fine.
        decimal mean = pnls.Sum() / pnls.Count;
        double variance = 0.0;
        foreach (var p in pnls)
        {
            var d = (double)(p - mean);
            variance += d * d;
        }
        variance /= Math.Max(1, pnls.Count - 1);
        double stdev = Math.Sqrt(variance);
        double sharpePerCycle = stdev > 1e-9 ? (double)mean / stdev : 0.0;
        double sharpeAnnualized = sharpePerCycle * Math.Sqrt(2190.0);

        // Verdict band — matches the promotion-gate language operator already uses.
        string verdict = pnls.Count < 30 ? "INSUFFICIENT-N"
            : sharpeAnnualized >= 0.5 ? "PROMOTE-CANDIDATE"
            : sharpeAnnualized >= 0.0 ? "WEAK"
            : "NEGATIVE";

        var spanDays = lastTs.HasValue && firstTs.HasValue
            ? (lastTs.Value - firstTs.Value).TotalDays : 0.0;

        _logger.LogInformation(
            "[paper-shadow] {Strategy}: N={N} cycles span={Days:F1}d | meanPnL=${MeanPnL:F3} stdPnL=${StdPnL:F3} | Sharpe(annual)={Sharpe:F2} cumPnL=${Cum:F2} | verdict={Verdict}",
            strategy, pnls.Count, spanDays, mean, (decimal)stdev, sharpeAnnualized, cumulative, verdict);
    }
}
