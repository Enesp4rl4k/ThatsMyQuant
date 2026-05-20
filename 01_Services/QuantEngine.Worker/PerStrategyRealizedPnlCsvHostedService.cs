using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Bot;

/// <summary>
/// [2026-05-17 Quantlab handoff §1] Per-strategy per-trade realized PnL CSV
/// emitter. Quantlab's <c>live_monitor.py</c> / <c>track_realized.py</c> consume
/// one CSV per strategy per day to drive kill-switch decisions
/// (realized 30c Sharpe → demote/hard-kill) and the
/// <c>TMQ_validated_execution_pending</c> watch-tier promotion/demotion.
///
/// SOURCE OF TRUTH:
/// In paper mode, real fills are recorded in <c>paper_portfolio.json</c>
/// (<see cref="PaperPortfolio.ClosedPositions"/>), NOT the SQLite Trades table
/// — that table is essentially dormant for paper/EdgeLab fills, and an earlier
/// iteration of this service that read from Trades found 0 closed rows while
/// the JSON had 126. We always read the JSON. The Trades table can be plumbed
/// back in later if/when live-mode fills land there.
///
/// Output: <c>{OutputRoot}/{strategy}/{YYYY-MM-DD}.csv</c>
/// Schema (matches handoff spec):
///   <c>entry_ts,exit_ts,symbol,side,qty,entry_px,exit_px,realized_pnl_pct,fees_bps,maker_or_taker,clOrdId</c>
///
/// Notes:
///   - <c>exit_px</c> comes from <see cref="PaperPosition.CurrentPrice"/> (which
///     in PaperPortfolio is the value at close-time — there is no explicit ExitPrice).
///   - <c>fees_bps</c> uses <see cref="PaperPosition.RealFeePaid"/> when populated
///     (real OKX fill); otherwise the entry-side maker/taker assumed-rate. This is
///     consistent with the Trade.cs convention.
///   - <c>maker_or_taker</c> reflects ENTRY side only
///     (<see cref="PaperPosition.IsMakerEntry"/>). Exit-side maker flag isn't
///     persisted yet; revisit when ExchangeFillSnapshot exit captures land.
///   - <c>clOrdId</c> is empty for paper-portfolio rows — PaperPosition has no
///     SignalId. Quantlab joins on (strategy, entry_ts, symbol) as fallback key.
///   - Idempotent: re-running overwrites the day's CSV with the latest snapshot
///     of closed positions. Cheap (dataset small, ~hundreds of rows).
/// </summary>
public sealed class PerStrategyRealizedPnlCsvHostedService : BackgroundService
{
    private readonly ILogger<PerStrategyRealizedPnlCsvHostedService> _logger;
    private readonly string _portfolioPath;
    private readonly string _outputRoot;
    private readonly TimeSpan _interval;
    private readonly int _lookbackDays;
    private readonly bool _enabled;

    public PerStrategyRealizedPnlCsvHostedService(
        ILogger<PerStrategyRealizedPnlCsvHostedService> logger,
        IConfiguration configuration)
    {
        _logger = logger;

        var defaultPortfolioPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ThatsMyQuant",
            "paper_portfolio.json");
        _portfolioPath = configuration["PerStrategyRealizedPnlCsv:PortfolioPath"]
                         ?? defaultPortfolioPath;

        _outputRoot = configuration["PerStrategyRealizedPnlCsv:OutputRoot"]
                      ?? "C:/Users/parlak/Downloads/Quantlab/data/realized_pnl";

        var minutes = int.TryParse(
            configuration["PerStrategyRealizedPnlCsv:IntervalMinutes"], out var m) && m > 0
            ? m
            : 60;
        _interval = TimeSpan.FromMinutes(minutes);

        _lookbackDays = int.TryParse(
            configuration["PerStrategyRealizedPnlCsv:LookbackDays"], out var d) && d > 0
            ? d
            : 30;

        _enabled = !bool.TryParse(
            configuration["PerStrategyRealizedPnlCsv:Enabled"], out var e) || e;

        // Exclude pseudo-strategies that are venue-mirror artifacts, not real
        // strategy fills (FundingHybridExecutor mirrors live OKX positions into
        // PaperPortfolio so the dashboard sees them; those carry tags like
        // "OkxLive" / "OkxMirror" and would pollute the per-strategy realized-PnL
        // buckets Quantlab's monitors consume). Configurable so a future mirror
        // tag can be added without redeploy.
        var excludeRaw = configuration["PerStrategyRealizedPnlCsv:ExcludeStrategies"];
        _excludedStrategies = string.IsNullOrWhiteSpace(excludeRaw)
            ? new HashSet<string>(new[] { "OkxLive", "OkxMirror" }, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                excludeRaw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
    }

    private readonly HashSet<string> _excludedStrategies;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("PerStrategyRealizedPnlCsv disabled.");
            return;
        }

        _logger.LogInformation(
            "PerStrategyRealizedPnlCsv starting. Source={Src} OutputRoot={Out} Interval={I} LookbackDays={D}",
            _portfolioPath, _outputRoot, _interval, _lookbackDays);

        try { await RunOnceAsync(stoppingToken).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "PerStrategyRealizedPnlCsv initial dump failed."); }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PerStrategyRealizedPnlCsv iteration failed.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (!File.Exists(_portfolioPath))
        {
            _logger.LogWarning(
                "PerStrategyRealizedPnlCsv: portfolio file not found at {Path}; skipping run.",
                _portfolioPath);
            return 0;
        }

        PaperPortfolio? portfolio;
        try
        {
            using var fs = File.Open(_portfolioPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            portfolio = await JsonSerializer.DeserializeAsync<PaperPortfolio>(fs, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "PerStrategyRealizedPnlCsv: failed to read/parse {Path}", _portfolioPath);
            return 0;
        }

        var closed = portfolio?.ClosedPositions;
        if (closed is null || closed.Count == 0)
        {
            _logger.LogDebug("PerStrategyRealizedPnlCsv: no closed positions in portfolio.");
            return 0;
        }

        var since = DateTime.UtcNow.AddDays(-_lookbackDays);
        var inWindow = closed
            .Where(p => p.ExitTime.HasValue
                        && p.ExitTime.Value >= since
                        && p.EntryPrice > 0m
                        && !string.IsNullOrWhiteSpace(p.StrategyTag)
                        && !_excludedStrategies.Contains(p.StrategyTag))
            .ToList();

        if (inWindow.Count == 0)
        {
            _logger.LogDebug(
                "PerStrategyRealizedPnlCsv: no closed positions in last {Days} days.",
                _lookbackDays);
            return 0;
        }

        Directory.CreateDirectory(_outputRoot);

        // Group by (strategy, exit-day). Re-emit the entire day's CSV each run
        // so corrections (RealFeePaid landing post-fill, exit-reason updates)
        // propagate to Quantlab on next read.
        var grouped = inWindow.GroupBy(p => new
        {
            Strategy = p.StrategyTag,
            Day = p.ExitTime!.Value.Date,
        });

        var totalRows = 0;
        var totalFiles = 0;
        foreach (var bucket in grouped)
        {
            if (ct.IsCancellationRequested) break;

            var stratDir = Path.Combine(_outputRoot, SanitizeForPath(bucket.Key.Strategy));
            Directory.CreateDirectory(stratDir);
            var csvPath = Path.Combine(stratDir,
                $"{bucket.Key.Day:yyyy-MM-dd}.csv");

            var sb = new StringBuilder();
            sb.AppendLine("entry_ts,exit_ts,symbol,side,qty,entry_px,exit_px,realized_pnl_pct,fees_bps,maker_or_taker,clOrdId");

            var n = 0;
            foreach (var p in bucket.OrderBy(x => x.EntryTime))
            {
                var qty = p.Quantity > 0m ? p.Quantity : p.OpenedQuantity;
                if (qty <= 0m) continue;

                var notional = p.EntryPrice * qty;
                if (notional <= 0m) continue;

                // Realized PnL % of notional (signed). Mirrors handoff spec
                // ("realized_pnl_pct" is gross-PnL-over-notional, not net-of-fees;
                // Quantlab subtracts fees_bps on its side).
                var pnlPct = p.RealizedPnL / notional;

                var feesBps = p.RealFeePaid is decimal rf && rf > 0m && notional > 0m
                    ? rf / notional * 10_000m
                    : 0m;

                var makerOrTaker = p.IsMakerEntry ? "maker" : "taker";

                var entryTs = DateTime.SpecifyKind(p.EntryTime, DateTimeKind.Utc);
                var exitTs = DateTime.SpecifyKind(p.ExitTime!.Value, DateTimeKind.Utc);

                sb.Append(entryTs.ToString("o", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(exitTs.ToString("o", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(p.Symbol).Append(',');
                sb.Append(p.Side.ToString()).Append(',');
                sb.Append(qty.ToString("0.################", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(p.EntryPrice.ToString("0.################", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(p.CurrentPrice.ToString("0.################", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(pnlPct.ToString("0.########", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(feesBps.ToString("0.##", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(makerOrTaker).Append(',');
                // clOrdId — PaperPosition carries no SignalId; leave blank.
                sb.AppendLine();
                n++;
            }

            try
            {
                await File.WriteAllTextAsync(csvPath, sb.ToString(), Encoding.UTF8, ct)
                    .ConfigureAwait(false);
                totalRows += n;
                totalFiles++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "PerStrategyRealizedPnlCsv: write failed for {Path}", csvPath);
            }
        }

        _logger.LogInformation(
            "PerStrategyRealizedPnlCsv wrote {Rows} rows across {Files} per-strategy/day files (window={Days}d).",
            totalRows, totalFiles, _lookbackDays);
        return totalRows;
    }

    private static string SanitizeForPath(string strategy)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buf = new char[strategy.Length];
        for (var i = 0; i < strategy.Length; i++)
        {
            var c = strategy[i];
            buf[i] = Array.IndexOf(invalid, c) >= 0 ? '_' : c;
        }
        return new string(buf);
    }
}
