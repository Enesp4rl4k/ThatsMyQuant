using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Bot;

/// <summary>
/// 2026-05-07 (Sprint 1.1 — Master Plan v3) — funding-edge decay early-warning.
///
/// Premortem #1 (Edge Decay During Shadow Validation Window) identified that
/// funding-carry edge can compress AS the validation window progresses. Sharpe
/// stays positive (lagging indicator) while the numerator (funding received) is
/// quietly collapsing because crypto funding rates are non-stationary.
///
/// The leading indicator: funding-payment-to-fee ratio per closed trade.
///   ratio = realized_funding_received / realized_fees_paid
///   healthy carry regime: > 2.0
///   decay regime:         drifts toward 1.0
///   ABORT promotion:      rolling-10 ratio < 1.5
///
/// This service runs every <c>FundingFeeRatio:IntervalMinutes</c> (default 30),
/// reads the rolling-N closed funding trades from <see cref="PaperPortfolio"/>,
/// groups by symbol, and emits per-symbol ratios. Telegram fires when ratio
/// crosses below the abort threshold. Persists state to
/// <c>%LOCALAPPDATA%\ThatsMyQuant\funding_fee_ratio.json</c> so the heartbeat /
/// dashboard can read it without re-querying.
///
/// Read-only — never blocks trades directly. The signal feeds into PromotionGate
/// later (Sprint 2) where the new SPRT decision rule consumes it as a hard veto.
/// </summary>
public sealed class FundingFeeRatioMonitorHostedService : BackgroundService
{
    private readonly ILogger<FundingFeeRatioMonitorHostedService> _logger;
    private readonly PaperPortfolio _portfolio;
    private readonly IRealtimeAlerter? _alerter;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly int _windowTrades;
    private readonly decimal _alertThreshold;
    private readonly string _stateFilePath;

    public FundingFeeRatioMonitorHostedService(
        ILogger<FundingFeeRatioMonitorHostedService> logger,
        IConfiguration cfg,
        PaperPortfolio portfolio,
        IRealtimeAlerter? alerter = null)
    {
        _logger = logger;
        _portfolio = portfolio;
        _alerter = alerter;
        _enabled = !bool.TryParse(cfg["FundingFeeRatio:Enabled"], out var en) || en;
        var minutes = int.TryParse(cfg["FundingFeeRatio:IntervalMinutes"], out var m) && m > 0 ? m : 30;
        _interval = TimeSpan.FromMinutes(minutes);
        _windowTrades = int.TryParse(cfg["FundingFeeRatio:WindowTrades"], out var w) && w > 0 ? w : 10;
        _alertThreshold = decimal.TryParse(
            cfg["FundingFeeRatio:AlertThreshold"],
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out var t) && t > 0 ? t : 1.5m;

        var stateDir = Environment.ExpandEnvironmentVariables(
            cfg["StateDirectory"]
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ThatsMyQuant"));
        _stateFilePath = Path.Combine(stateDir, "funding_fee_ratio.json");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("FundingFeeRatioMonitor disabled (FundingFeeRatio:Enabled=false).");
            return;
        }

        _logger.LogWarning(
            "FundingFeeRatioMonitor starting. Interval={I}m WindowTrades={W} AlertThreshold={T:F2} (ratio < threshold = ABORT promotion)",
            _interval.TotalMinutes, _windowTrades, _alertThreshold);

        // Initial run after 1 minute, then on the configured interval.
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                try { await RunAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "FundingFeeRatio tick failed."); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Snapshot ClosedPositions under the portfolio lock — same pattern as TCA service.
        IReadOnlyList<PaperPosition> closed;
        lock (_portfolio)
        {
            closed = _portfolio.ClosedPositions.ToList();
        }

        if (closed.Count == 0)
        {
            _logger.LogDebug("FundingFeeRatio: no closed positions yet.");
            return;
        }

        // Filter to funding-strategy trades. StrategyTag pattern (from FundingHybridExecutor):
        //   "FundingAlpha-Hybrid", "FundingAlpha-Hybrid-Long", "FundingAlpha-Hybrid-Short"
        // Pairs and EdgeLab tags excluded — different cost structure (spread + 2 legs).
        var fundingTrades = closed
            .Where(p => !string.IsNullOrEmpty(p.StrategyTag) &&
                        p.StrategyTag.StartsWith("FundingAlpha", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (fundingTrades.Count == 0)
        {
            _logger.LogDebug("FundingFeeRatio: no funding-strategy trades closed yet.");
            return;
        }

        // Group by symbol, take last N per symbol, compute ratio.
        var perSymbol = new Dictionary<string, RatioSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var symGroup in fundingTrades.GroupBy(p => p.Symbol, StringComparer.OrdinalIgnoreCase))
        {
            // Most-recent N trades for this symbol.
            var recent = symGroup
                .OrderByDescending(p => p.ExitTime ?? DateTime.UtcNow)
                .Take(_windowTrades)
                .ToList();

            decimal totalFunding = 0m;
            decimal totalFee = 0m;
            int withFundingData = 0;
            int withFeeData = 0;
            foreach (var p in recent)
            {
                // NetFunding may be 0 for a trade with no settlement crossings — still counted.
                totalFunding += p.NetFunding;
                if (p.NetFunding != 0m) withFundingData++;

                // Prefer real venue-reported fee. Fall back to assumed bps × notional only as a
                // last resort (mirrors TcaLiveAnalyzerHostedService logic but the ratio metric
                // is most informative when both numerator and denominator are venue-measured).
                var fee = p.RealFeePaid ?? 0m;
                if (fee > 0m) { totalFee += fee; withFeeData++; }
            }

            // Ratio undefined if fee total is zero (no real fees captured yet — mostly happens in
            // dry-run mode before any live fills). Report null and skip the alert path.
            decimal? ratio = totalFee > 0m ? totalFunding / totalFee : (decimal?)null;
            perSymbol[symGroup.Key] = new RatioSnapshot(
                Symbol: symGroup.Key,
                TradesInWindow: recent.Count,
                WithFundingData: withFundingData,
                WithFeeData: withFeeData,
                TotalFunding: totalFunding,
                TotalFee: totalFee,
                Ratio: ratio,
                LastTradeUtc: recent[0].ExitTime ?? DateTime.UtcNow);
        }

        // Log per-symbol summary.
        foreach (var (sym, snap) in perSymbol)
        {
            if (snap.Ratio is null)
            {
                _logger.LogInformation(
                    "FUND_FEE_RATIO {Symbol} N={N} (data: funding={Fund}/{N1} fee={Fee}/{N2}) — RATIO UNDEFINED (no real fees yet)",
                    sym, snap.TradesInWindow, snap.WithFundingData, snap.TradesInWindow, snap.WithFeeData, snap.TradesInWindow);
                continue;
            }

            var verdict = snap.Ratio >= _alertThreshold ? "OK" : "ABORT-PROMOTION";
            _logger.LogWarning(
                "FUND_FEE_RATIO {Symbol} N={N} totalFunding=${F:F4} totalFee=${C:F4} ratio={R:F2} threshold={T:F2} verdict={V}",
                sym, snap.TradesInWindow, snap.TotalFunding, snap.TotalFee, snap.Ratio, _alertThreshold, verdict);
        }

        // Persist state for heartbeat / dashboard consumption.
        try
        {
            var stateDoc = new
            {
                ComputedAtUtc = DateTime.UtcNow,
                Threshold = _alertThreshold,
                WindowTrades = _windowTrades,
                Symbols = perSymbol.Values.Select(s => new
                {
                    s.Symbol,
                    s.TradesInWindow,
                    s.WithFundingData,
                    s.WithFeeData,
                    s.TotalFunding,
                    s.TotalFee,
                    Ratio = s.Ratio,
                    s.LastTradeUtc,
                    BelowThreshold = s.Ratio is not null && s.Ratio < _alertThreshold
                }).ToArray()
            };
            var json = JsonSerializer.Serialize(stateDoc, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_stateFilePath, json, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FundingFeeRatio: state persist failed (non-fatal).");
        }

        // Telegram alert — fire ONCE per cycle aggregated, not per-symbol (anti-spam).
        var triggered = perSymbol.Values
            .Where(s => s.Ratio is not null && s.Ratio < _alertThreshold && s.TradesInWindow >= 3)
            .ToList();
        if (triggered.Count > 0 && _alerter is not null)
        {
            var lines = triggered.Select(s =>
                $"  {s.Symbol}: ratio={s.Ratio:F2} (funding=${s.TotalFunding:F4} / fees=${s.TotalFee:F4}) over N={s.TradesInWindow}");
            await _alerter.SendAsync(
                key: "funding_fee_ratio_below_threshold",
                severity: AlertSeverity.Warning,
                title: "⚠️ Funding/Fee ratio below abort threshold",
                body:
                    $"Symbols below {_alertThreshold:F2} (rolling-{_windowTrades} window):\n" +
                    string.Join("\n", lines) + "\n\n" +
                    "Implication: funding rate compressing relative to execution costs. " +
                    "Edge decaying. Recommend FREEZE promotion until ratio recovers > 2.0.",
                ct: ct).ConfigureAwait(false);
        }
    }

    private sealed record RatioSnapshot(
        string Symbol,
        int TradesInWindow,
        int WithFundingData,
        int WithFeeData,
        decimal TotalFunding,
        decimal TotalFee,
        decimal? Ratio,
        DateTime LastTradeUtc);
}
