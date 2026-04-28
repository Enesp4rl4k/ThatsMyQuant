using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Heads-up funding scout — polls a configurable watchlist (default LTC/DOGE/AAVE/LINK)
/// every <c>Funding:Scout:IntervalMinutes</c> and fires a Telegram alert whenever the
/// absolute funding rate crosses the "building" threshold (default 0.5bp = half of the
/// hybrid's 1bp entry threshold). Cooldown per-symbol is 1 hour by default to avoid spam.
///
/// Purpose: gives the operator visibility BEFORE a real entry signal triggers, so they
/// see funding building up and aren't surprised when an order suddenly fires.
/// Disabled when <c>Funding:Scout:Enabled</c> is false (default true).
/// </summary>
public sealed class FundingScoutHostedService : BackgroundService
{
    private readonly ILogger<FundingScoutHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IRealtimeAlerter? _alerter;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly decimal _alertThreshold;
    private readonly int _cooldownMinutes;
    private readonly string[] _watchlist;
    private readonly Dictionary<string, DateTime> _lastAlertUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, decimal> _lastRate = new(StringComparer.OrdinalIgnoreCase);

    public FundingScoutHostedService(
        ILogger<FundingScoutHostedService> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        IRealtimeAlerter? alerter = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _alerter = alerter;
        _enabled = !bool.TryParse(configuration["Funding:Scout:Enabled"], out var en) || en;
        var minutes = int.TryParse(configuration["Funding:Scout:IntervalMinutes"], out var m) && m > 0 ? m : 5;
        _interval = TimeSpan.FromMinutes(minutes);
        _alertThreshold = decimal.TryParse(configuration["Funding:Scout:AlertThresholdRate"], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var t) && t > 0 ? t : 0.00005m;
        _cooldownMinutes = int.TryParse(configuration["Funding:Scout:CooldownMinutes"], out var cd) && cd > 0 ? cd : 60;

        var csv = configuration["Funding:Scout:WatchlistCsv"]
            ?? "LTCUSDT,DOGEUSDT,AAVEUSDT,LINKUSDT,SOLUSDT,AVAXUSDT,XRPUSDT,ADAUSDT";
        _watchlist = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("FundingScoutHostedService disabled (Funding:Scout:Enabled=false).");
            return;
        }
        if (_alerter is null)
        {
            _logger.LogWarning("FundingScout: no IRealtimeAlerter registered — alerts will only land in the log.");
        }

        _logger.LogWarning(
            "FundingScout starting. Interval={I}m AlertThreshold={T:F5} ({Bp:F1}bp) Cooldown={CD}m Watchlist={N}: {Syms}",
            _interval.TotalMinutes, _alertThreshold, _alertThreshold * 10_000m, _cooldownMinutes, _watchlist.Length, string.Join(",", _watchlist));

        // Small initial delay so we don't blast on boot.
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                try
                {
                    await ScanOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "FundingScout cycle failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task ScanOnceAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var provider = scope.ServiceProvider.GetService<IFundingRateProvider>();
        if (provider is null)
        {
            _logger.LogWarning("FundingScout: IFundingRateProvider not registered — skipping cycle.");
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var hits = new List<(string Sym, decimal Rate)>();

        foreach (var sym in _watchlist)
        {
            if (ct.IsCancellationRequested) return;
            decimal rate;
            try { rate = provider.GetRate(sym, nowUtc); }
            catch (Exception ex) { _logger.LogDebug(ex, "FundingScout: GetRate failed for {Sym}", sym); continue; }

            _lastRate[sym] = rate;
            if (Math.Abs(rate) < _alertThreshold) continue;
            hits.Add((sym, rate));
        }

        if (hits.Count == 0) return;

        // Group into one Telegram message per cycle (keeps phone quiet, but skip per-symbol cooldowned ones).
        var freshHits = new List<(string Sym, decimal Rate)>();
        foreach (var hit in hits)
        {
            if (_lastAlertUtc.TryGetValue(hit.Sym, out var last) && (nowUtc - last).TotalMinutes < _cooldownMinutes)
            {
                _logger.LogDebug("FundingScout: {Sym} suppressed (cooldown {Min:F0}m remaining)",
                    hit.Sym, _cooldownMinutes - (nowUtc - last).TotalMinutes);
                continue;
            }
            freshHits.Add(hit);
            _lastAlertUtc[hit.Sym] = nowUtc;
        }

        if (freshHits.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine($"{freshHits.Count} symbol funding building (≥ {_alertThreshold * 10_000m:F1}bp):");
        foreach (var (sym, rate) in freshHits.OrderByDescending(h => Math.Abs(h.Rate)))
        {
            var bp = rate * 10_000m;
            var dir = rate > 0 ? "📈 LONGS PAY" : "📉 SHORTS PAY";
            sb.AppendLine($"• `{sym}` {bp:+0.0;-0.0}bp  {dir}");
        }
        sb.AppendLine();
        sb.AppendLine($"_Hybrid entry threshold: {_alertThreshold * 2 * 10_000m:F1}bp (2× scout). Trade auto-fires if backtest runner confirms._");

        _logger.LogWarning("FundingScout alert: {N} symbols building", freshHits.Count);
        if (_alerter is not null)
        {
            await _alerter.SendAsync(
                key: "funding_scout",
                severity: AlertSeverity.Info,
                title: "Funding building — heads up",
                body: sb.ToString(),
                ct: ct).ConfigureAwait(false);
        }
    }
}
