using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Bot;

/// <summary>
/// 2026-05-07 (Sprint 3.1 PHASE 1 — Master Plan v3) — Hierarchical Risk Parity
/// allocation computation service.
///
/// Background: Premortem #5 (Diversification Myth) showed that equal-weight
/// allocation across "different" strategies hides factor concentration. The
/// codebase already has <see cref="HierarchicalRiskParity"/> (Lopez de Prado
/// 2016) implemented but NEVER WIRED. Audit confirmed: zero callers in
/// production code, only test coverage.
///
/// PHASE 1 (THIS SERVICE): periodic compute. Every <c>HrpAllocator:IntervalHours</c>
/// (default 6), reads closed-trade daily PnL per StrategyTag, builds covariance
/// matrix, runs HRP, persists weights to
/// <c>%LOCALAPPDATA%\ThatsMyQuant\hrp_allocator.json</c>. Logs the
/// recommendation. Does NOT change actual sizing.
///
/// PHASE 2 (NEXT SPRINT): executor sizing replacement. FundingHybridExecutor
/// reads the state file at entry-sizing time and modulates BaseSizeFraction
/// per the HRP weights. Gated behind <c>HrpAllocator:EnforceSizing</c> flag
/// so we can compare HRP-recommended vs current static for several weeks of
/// trades before flipping the switch.
///
/// Why phased: replacing live sizing logic without an OBSERVATION period is
/// the kind of change that silently halves position sizes for weeks before an
/// operator notices PnL is "weirdly flat". This service exposes the
/// recommendation, makes it auditable (state file + log + Telegram on big
/// reweighting events), and lets the operator flip the wire-in deliberately.
/// </summary>
public sealed class HrpAllocatorMonitorHostedService : BackgroundService
{
    private readonly ILogger<HrpAllocatorMonitorHostedService> _logger;
    private readonly PaperPortfolio _portfolio;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly int _lookbackDays;
    private readonly int _minTradesPerStrategy;
    private readonly string _stateFilePath;

    public HrpAllocatorMonitorHostedService(
        ILogger<HrpAllocatorMonitorHostedService> logger,
        IConfiguration cfg,
        PaperPortfolio portfolio)
    {
        _logger = logger;
        _portfolio = portfolio;
        _enabled = !bool.TryParse(cfg["HrpAllocator:Enabled"], out var en) || en;
        var hours = int.TryParse(cfg["HrpAllocator:IntervalHours"], out var h) && h > 0 ? h : 6;
        _interval = TimeSpan.FromHours(hours);
        _lookbackDays = int.TryParse(cfg["HrpAllocator:LookbackDays"], out var d) && d > 0 ? d : 60;
        _minTradesPerStrategy = int.TryParse(cfg["HrpAllocator:MinTradesPerStrategy"], out var mt) && mt > 0 ? mt : 5;

        var stateDir = Environment.ExpandEnvironmentVariables(
            cfg["StateDirectory"]
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ThatsMyQuant"));
        _stateFilePath = Path.Combine(stateDir, "hrp_allocator.json");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("HrpAllocatorMonitor disabled.");
            return;
        }

        _logger.LogWarning(
            "HrpAllocatorMonitor starting (PHASE 1: compute & log only). Interval={I}h Lookback={L}d MinTrades={N}. Phase 2 wires HRP weights into executor sizing.",
            _interval.TotalHours, _lookbackDays, _minTradesPerStrategy);

        try { await Task.Delay(TimeSpan.FromMinutes(4), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                try { await RunAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "HrpAllocator tick failed."); }
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

        var groups = inWindow
            .GroupBy(p => p.StrategyTag, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= _minTradesPerStrategy)
            .ToList();

        if (groups.Count < 2)
        {
            _logger.LogInformation(
                "HRP_ALLOC only {N} strategies with N >= {Min} — HRP needs >= 2. Falling back to equal-weight {EW} for any strategies present.",
                groups.Count, _minTradesPerStrategy,
                groups.Count > 0 ? (1.0 / groups.Count).ToString("F2") : "n/a");

            await PersistAsync(new
            {
                ComputedAtUtc = DateTime.UtcNow,
                LookbackDays = _lookbackDays,
                Strategies = groups.Select(g => new
                {
                    Tag = g.Key,
                    TradeCount = g.Count(),
                    HrpWeight = groups.Count > 0 ? 1.0 / groups.Count : 0.0
                }).ToArray(),
                FellBackToEqualWeight = true,
                Verdict = groups.Count == 0 ? "no-data" : "single-strategy"
            }, ct).ConfigureAwait(false);
            return;
        }

        // Build daily PnL series per strategy.
        var dailyByStrategy = new Dictionary<string, Dictionary<DateTime, double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            var bucketed = g
                .Where(p => p.ExitTime.HasValue)
                .GroupBy(p => p.ExitTime!.Value.Date)
                .ToDictionary(grp => grp.Key, grp => (double)grp.Sum(p => p.RealizedPnL));
            dailyByStrategy[g.Key] = bucketed;
        }

        var allDates = dailyByStrategy.Values
            .SelectMany(d => d.Keys)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        if (allDates.Count < 5)
        {
            _logger.LogInformation("HRP_ALLOC insufficient daily history ({D} days) — minimum 5 needed. Equal-weight fallback.", allDates.Count);
            await PersistAsync(new
            {
                ComputedAtUtc = DateTime.UtcNow,
                LookbackDays = _lookbackDays,
                Strategies = groups.Select(g => new
                {
                    Tag = g.Key,
                    TradeCount = g.Count(),
                    HrpWeight = 1.0 / groups.Count
                }).ToArray(),
                FellBackToEqualWeight = true,
                Verdict = "insufficient-daily-history"
            }, ct).ConfigureAwait(false);
            return;
        }

        // Build covariance matrix over the aligned daily PnL series.
        var tags = dailyByStrategy.Keys.ToArray();
        int n = tags.Length;
        var series = new double[n][];
        for (int i = 0; i < n; i++)
        {
            series[i] = allDates.Select(d => dailyByStrategy[tags[i]].TryGetValue(d, out var v) ? v : 0.0).ToArray();
        }

        var means = new double[n];
        for (int i = 0; i < n; i++) means[i] = series[i].Average();

        var cov = new double[n, n];
        int m = allDates.Count;
        for (int i = 0; i < n; i++)
        {
            for (int j = i; j < n; j++)
            {
                double s = 0.0;
                for (int k = 0; k < m; k++)
                {
                    s += (series[i][k] - means[i]) * (series[j][k] - means[j]);
                }
                cov[i, j] = cov[j, i] = s / Math.Max(1, m - 1);
            }
        }

        // Run HRP.
        HierarchicalRiskParity.HrpResult hrp;
        try
        {
            hrp = HierarchicalRiskParity.Allocate(cov);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HRP_ALLOC HRP.Allocate failed — falling back to equal-weight.");
            await PersistAsync(new
            {
                ComputedAtUtc = DateTime.UtcNow,
                LookbackDays = _lookbackDays,
                Strategies = tags.Select((t, i) => new
                {
                    Tag = t,
                    HrpWeight = 1.0 / n
                }).ToArray(),
                FellBackToEqualWeight = true,
                Verdict = "hrp-failed",
                Error = ex.Message
            }, ct).ConfigureAwait(false);
            return;
        }

        // Log recommendation.
        var equalWeight = 1.0 / n;
        _logger.LogWarning(
            "HRP_ALLOC strategies={N} dates={D} HRP weights vs equal-weight {EW:F3}:",
            n, allDates.Count, equalWeight);

        for (int i = 0; i < n; i++)
        {
            var w = hrp.Weights[i];
            var dev = w - equalWeight;
            var pctDev = equalWeight > 0 ? dev / equalWeight : 0;
            _logger.LogInformation(
                "  HRP[{Tag}] w={W:F3} equalWeight={EW:F3} delta={Dev:+0.000;-0.000} ({PctDev:+0.0%;-0.0%})",
                tags[i], w, equalWeight, dev, pctDev);
        }

        await PersistAsync(new
        {
            ComputedAtUtc = DateTime.UtcNow,
            LookbackDays = _lookbackDays,
            DailyDataPoints = allDates.Count,
            Strategies = tags.Select((t, i) => new
            {
                Tag = t,
                TradeCount = groups.First(g => g.Key.Equals(t, StringComparison.OrdinalIgnoreCase)).Count(),
                HrpWeight = hrp.Weights[i],
                EqualWeight = equalWeight,
                DeviationFromEqual = hrp.Weights[i] - equalWeight
            }).ToArray(),
            QuasiDiagonalOrder = hrp.QuasiDiagonalOrder,
            FellBackToEqualWeight = false,
            Verdict = "ok"
        }, ct).ConfigureAwait(false);
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
            _logger.LogWarning(ex, "HrpAllocator: state persist failed (non-fatal).");
        }
    }
}
