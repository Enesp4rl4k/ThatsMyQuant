using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Bot;

/// <summary>
/// 2026-05-07 (Sprint 1.3 — Master Plan v3) — diversification-myth detector.
///
/// Premortem #5 (Diversification Myth) identified the most-dangerous failure
/// mode: operator promotes 4 "different" strategies (LTC funding, AAVE funding,
/// BTC funding, realized-vol mean-rev), each passing pre-registration
/// independently with Sharpe 0.8-1.2. October 2026 vol regime crash hits, ALL
/// FOUR drawdown in the same 18-day window because they're all short-vol carry
/// expressions wearing different costumes. Portfolio -11%.
///
/// The leading indicator: pairwise daily-PnL correlation matrix across all
/// active strategies. If any off-diagonal > 0.4, "diversification" is illusory
/// and the book is a single-factor concentration. Trigger HRP rebalance
/// (Sprint 3.1) instead of equal-weight.
///
/// This service runs every <c>PairwiseCorrelation:IntervalHours</c> (default 6),
/// reads closed PaperPortfolio positions, groups by StrategyTag, builds daily
/// PnL series per strategy, and computes Pearson correlation over the rolling
/// <c>LookbackDays</c> (default 60) window. Persists matrix to
/// <c>%LOCALAPPDATA%\ThatsMyQuant\pairwise_correlation.json</c>.
///
/// Read-only — never blocks trades. Sprint 3.1 HRP allocator consumes the state
/// file as input to position-weighting; Sprint 5+ edge-hunting uses it as a
/// filter (don't add a 5th strategy if it has > 0.4 correlation to existing 4).
/// </summary>
public sealed class PairwiseCorrelationMonitorHostedService : BackgroundService
{
    private readonly ILogger<PairwiseCorrelationMonitorHostedService> _logger;
    private readonly PaperPortfolio _portfolio;
    private readonly IRealtimeAlerter? _alerter;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly int _lookbackDays;
    private readonly int _minTradesPerStrategy;
    private readonly decimal _alertThreshold;
    private readonly string _stateFilePath;

    public PairwiseCorrelationMonitorHostedService(
        ILogger<PairwiseCorrelationMonitorHostedService> logger,
        IConfiguration cfg,
        PaperPortfolio portfolio,
        IRealtimeAlerter? alerter = null)
    {
        _logger = logger;
        _portfolio = portfolio;
        _alerter = alerter;
        _enabled = !bool.TryParse(cfg["PairwiseCorrelation:Enabled"], out var en) || en;
        var hours = int.TryParse(cfg["PairwiseCorrelation:IntervalHours"], out var h) && h > 0 ? h : 6;
        _interval = TimeSpan.FromHours(hours);
        _lookbackDays = int.TryParse(cfg["PairwiseCorrelation:LookbackDays"], out var d) && d > 0 ? d : 60;
        _minTradesPerStrategy = int.TryParse(cfg["PairwiseCorrelation:MinTradesPerStrategy"], out var mt) && mt > 0 ? mt : 5;
        _alertThreshold = decimal.TryParse(
            cfg["PairwiseCorrelation:AlertThreshold"],
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out var t) && t > 0 ? t : 0.4m;

        var stateDir = Environment.ExpandEnvironmentVariables(
            cfg["StateDirectory"]
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ThatsMyQuant"));
        _stateFilePath = Path.Combine(stateDir, "pairwise_correlation.json");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("PairwiseCorrelationMonitor disabled.");
            return;
        }

        _logger.LogWarning(
            "PairwiseCorrelationMonitor starting. Interval={I}h Lookback={L}d MinTrades={N} AlertThreshold={T:F2} (off-diagonal > threshold = factor concentration risk)",
            _interval.TotalHours, _lookbackDays, _minTradesPerStrategy, _alertThreshold);

        // Initial delay 3 minutes — let the bot finish boot.
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                try { await RunAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "PairwiseCorrelation tick failed."); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        IReadOnlyList<PaperPosition> closed;
        lock (_portfolio)
        {
            closed = _portfolio.ClosedPositions.ToList();
        }

        var since = DateTime.UtcNow.AddDays(-_lookbackDays);
        var inWindow = closed
            .Where(p => (p.ExitTime ?? p.EntryTime) >= since)
            .Where(p => !string.IsNullOrEmpty(p.StrategyTag))
            .ToList();

        if (inWindow.Count == 0)
        {
            _logger.LogInformation("PAIRWISE_CORR no closed positions in lookback {L}d.", _lookbackDays);
            await PersistAsync(new
            {
                ComputedAtUtc = DateTime.UtcNow,
                LookbackDays = _lookbackDays,
                Threshold = _alertThreshold,
                Strategies = Array.Empty<object>(),
                CorrelationMatrix = new Dictionary<string, Dictionary<string, decimal>>(),
                Verdict = "no-data"
            }, ct).ConfigureAwait(false);
            return;
        }

        // Group by StrategyTag (or fallback by symbol if tag is generic). For funding alpha,
        // tags look like "FundingAlpha-Hybrid-Long" / "-Short". For pairs: "Pairs-Hybrid-Long".
        // Use full tag — the goal is to detect when "different" tags are actually correlated.
        var strategyGroups = inWindow
            .GroupBy(p => p.StrategyTag, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= _minTradesPerStrategy)
            .ToList();

        if (strategyGroups.Count < 2)
        {
            _logger.LogInformation(
                "PAIRWISE_CORR only {N} strategies with >= {Min} trades — correlation matrix needs ≥2. (Currently: {Tags})",
                strategyGroups.Count, _minTradesPerStrategy,
                string.Join(",", strategyGroups.Select(g => $"{g.Key}({g.Count()})")));
            await PersistAsync(new
            {
                ComputedAtUtc = DateTime.UtcNow,
                LookbackDays = _lookbackDays,
                Threshold = _alertThreshold,
                Strategies = strategyGroups.Select(g => new { Tag = g.Key, TradeCount = g.Count() }).ToArray(),
                CorrelationMatrix = new Dictionary<string, Dictionary<string, decimal>>(),
                Verdict = strategyGroups.Count == 0 ? "no-strategies" : "single-strategy-only"
            }, ct).ConfigureAwait(false);
            return;
        }

        // Build daily-bucketed PnL series per strategy.
        var dailyByStrategy = new Dictionary<string, Dictionary<DateTime, decimal>>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in strategyGroups)
        {
            var bucketed = g
                .Where(p => p.ExitTime.HasValue)
                .GroupBy(p => p.ExitTime!.Value.Date)
                .ToDictionary(grp => grp.Key, grp => grp.Sum(p => p.RealizedPnL));
            dailyByStrategy[g.Key] = bucketed;
        }

        // Build union of all dates in window so each strategy series is aligned.
        var allDates = dailyByStrategy.Values
            .SelectMany(d => d.Keys)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        // [2026-05-13] Pearson correlation needs ≥5 data points to be meaningful — with 2-3
        // points the result is always ±1 by construction (degenerate fit), producing
        // noise alerts on every fresh worker boot. Skip if insufficient data.
        const int MinDatesForCorrelation = 5;
        if (allDates.Count < MinDatesForCorrelation)
        {
            _logger.LogInformation(
                "PAIRWISE_CORR insufficient daily data points ({D} < {Min}) — Pearson degenerate (always ±1). Skipping correlation alert.",
                allDates.Count, MinDatesForCorrelation);
            await PersistAsync(new
            {
                ComputedAtUtc = DateTime.UtcNow,
                LookbackDays = _lookbackDays,
                Threshold = _alertThreshold,
                Strategies = strategyGroups.Select(g => new { Tag = g.Key, TradeCount = g.Count() }).ToArray(),
                CorrelationMatrix = new Dictionary<string, Dictionary<string, decimal>>(),
                Verdict = "insufficient-data"
            }, ct).ConfigureAwait(false);
            return;
        }

        // Compute pairwise Pearson correlation.
        var tags = dailyByStrategy.Keys.ToList();
        var matrix = new Dictionary<string, Dictionary<string, decimal>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tags) matrix[t] = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        var alerts = new List<(string A, string B, decimal Corr)>();
        for (int i = 0; i < tags.Count; i++)
        {
            for (int j = i; j < tags.Count; j++)
            {
                var ta = tags[i]; var tb = tags[j];
                if (i == j) { matrix[ta][tb] = 1.0m; continue; }
                var seriesA = allDates.Select(d => dailyByStrategy[ta].TryGetValue(d, out var v) ? v : 0m).ToArray();
                var seriesB = allDates.Select(d => dailyByStrategy[tb].TryGetValue(d, out var v) ? v : 0m).ToArray();
                var corr = PearsonCorrelation(seriesA, seriesB);
                matrix[ta][tb] = corr;
                matrix[tb][ta] = corr;
                // [2026-05-13] Bug fix: was `Math.Abs(corr) >= threshold` which alerted on
                // anti-correlation too. Anti-correlation is a HEDGE, not factor concentration —
                // mirror long/short strategies (e.g. FundingAlpha-Hybrid ↔ FundingAlpha-Hybrid-Short)
                // are -1.0 correlated by construction; that's the design, not a risk.
                // The diversification-myth premortem (#5) is about strategies all being LONG the
                // same factor (vol carry, funding carry, momentum) and crashing together — only
                // POSITIVE correlation indicates this risk.
                if (corr >= _alertThreshold) alerts.Add((ta, tb, corr));
            }
        }

        // Log summary.
        var verdict = alerts.Count == 0 ? "DIVERSIFIED" : "FACTOR-CONCENTRATION-RISK";
        _logger.LogWarning(
            "PAIRWISE_CORR strategies={N} dates={D} highCorrPairs={H} threshold={T:F2} verdict={V}",
            tags.Count, allDates.Count, alerts.Count, _alertThreshold, verdict);

        foreach (var (a, b, c) in alerts)
        {
            _logger.LogWarning("  PAIR {A} ↔ {B}: corr={C:F2} (|c| >= {T:F2})", a, b, c, _alertThreshold);
        }

        // Persist.
        await PersistAsync(new
        {
            ComputedAtUtc = DateTime.UtcNow,
            LookbackDays = _lookbackDays,
            Threshold = _alertThreshold,
            Strategies = tags.Select(t => new
            {
                Tag = t,
                TradeCount = strategyGroups.First(g => g.Key.Equals(t, StringComparison.OrdinalIgnoreCase)).Count(),
                DailyDataPoints = dailyByStrategy[t].Count
            }).ToArray(),
            CorrelationMatrix = matrix,
            HighCorrelationPairs = alerts.Select(a => new { A = a.A, B = a.B, Correlation = a.Corr }).ToArray(),
            Verdict = verdict
        }, ct).ConfigureAwait(false);

        // Telegram alert.
        if (alerts.Count > 0 && _alerter is not null)
        {
            var lines = alerts.Select(a => $"  {a.A} ↔ {a.B}: {a.Corr:F2}");
            await _alerter.SendAsync(
                key: "pairwise_correlation_high",
                severity: AlertSeverity.Warning,
                title: "⚠️ Strategy correlation above threshold",
                body:
                    $"Pairwise daily-PnL correlation > {_alertThreshold:F2} (rolling-{_lookbackDays}d):\n" +
                    string.Join("\n", lines) + "\n\n" +
                    "Implication: 'diversification' is illusory — these strategies share factor exposure. " +
                    "When that factor mean-reverts (vol regime shift, funding compression, etc.) all positions " +
                    "drawdown together. Cap aggregate gross at 1.0× the factor exposure, or trigger HRP rebalance.",
                ct: ct).ConfigureAwait(false);
        }
    }

    private static decimal PearsonCorrelation(decimal[] x, decimal[] y)
    {
        if (x.Length != y.Length || x.Length == 0) return 0m;
        var n = x.Length;
        var meanX = x.Average();
        var meanY = y.Average();
        decimal num = 0m, denomX = 0m, denomY = 0m;
        for (int i = 0; i < n; i++)
        {
            var dx = x[i] - meanX;
            var dy = y[i] - meanY;
            num += dx * dy;
            denomX += dx * dx;
            denomY += dy * dy;
        }
        if (denomX == 0m || denomY == 0m) return 0m;  // zero-variance series
        return num / (decimal)Math.Sqrt((double)(denomX * denomY));
    }

    private async Task PersistAsync(object stateDoc, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(stateDoc, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_stateFilePath, json, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PairwiseCorrelation: state persist failed (non-fatal).");
        }
    }
}
