using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Periodically pulls account balance from the configured exchange (OKX demo) and updates the
/// shared <see cref="PaperPortfolio"/>'s Cash + InitialBalance to match. This keeps the system's
/// portfolio view in sync with the exchange's real (demo) account so the dashboard, digest, and
/// Kelly-weight basis all reflect actual capital.
///
/// Behaviour:
///   - Default interval 5 minutes (override via Paper:ExchangeSyncIntervalSeconds).
///   - On each tick: GetAccountStatusAsync → if Balance > 0, update Portfolio.Cash + InitialBalance.
///   - Logs when the balance changes by more than 0.01 to avoid spam.
///   - No-op when Paper:SyncWithExchange is false or IExchangeService is missing.
/// </summary>
public sealed class ExchangeBalanceSyncHostedService : BackgroundService
{
    private readonly ILogger<ExchangeBalanceSyncHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly PaperPortfolio _portfolio;
    private readonly TimeSpan _interval;
    private readonly bool _enabled;
    private readonly bool _liveModeAlwaysSyncCash;
    private readonly string _rawConfigValue;
    private readonly IRealtimeAlerter? _alerter;
    private decimal _lastReportedBalance;
    private DateTime _lastSuccessfulSyncUtc = DateTime.UtcNow;
    private bool _disconnectedAlertFired;

    public ExchangeBalanceSyncHostedService(
        ILogger<ExchangeBalanceSyncHostedService> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        PaperPortfolio portfolio,
        IRealtimeAlerter? alerter = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _portfolio = portfolio;
        _alerter = alerter;
        _rawConfigValue = configuration["Paper:SyncWithExchange"] ?? "(null)";
        _enabled = bool.TryParse(_rawConfigValue, out var s) && s;
        var intervalSeconds = int.TryParse(configuration["Paper:ExchangeSyncIntervalSeconds"], out var iv) && iv > 0 ? iv : 300;
        _interval = TimeSpan.FromSeconds(intervalSeconds);

        // In hybrid live mode, real OKX orders move the demo balance — Cash MUST track OKX
        // continuously, not just before the first closed position. Otherwise PaperPortfolio.Cash
        // diverges from OKX.Balance and PnL accounting becomes confused.
        var fundingMode = (configuration["Funding:HybridMode"] ?? "off").Trim().ToLowerInvariant();
        var pairsMode = (configuration["Strategy:PairsTrading:HybridMode"] ?? "off").Trim().ToLowerInvariant();
        _liveModeAlwaysSyncCash = fundingMode == "live" || pairsMode == "live";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogWarning(
                "ExchangeBalanceSyncHostedService disabled. Raw config value Paper:SyncWithExchange='{Raw}' parsed={Enabled}.",
                _rawConfigValue, _enabled);
            return;
        }

        _logger.LogInformation("ExchangeBalanceSyncHostedService starting. Interval={Interval}s.", _interval.TotalSeconds);

        // First sync runs immediately so downstream services see the real basis on their first tick.
        await SyncOnceAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await SyncOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ExchangeBalanceSyncHostedService tick failed.");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SyncOnceAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var exchange = scope.ServiceProvider.GetService<IExchangeService>();
        if (exchange is null)
        {
            _logger.LogWarning("ExchangeBalanceSync: no IExchangeService registered; nothing to sync.");
            return;
        }

        Account? account;
        try
        {
            account = await exchange.GetAccountStatusAsync().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ExchangeBalanceSync: GetAccountStatusAsync threw.");
            await CheckDisconnectAlertAsync(ct).ConfigureAwait(false);
            return;
        }

        if (account is null || account.Balance <= 0m)
        {
            _logger.LogDebug("ExchangeBalanceSync: account null or zero balance; skipping update.");
            await CheckDisconnectAlertAsync(ct).ConfigureAwait(false);
            return;
        }

        // Successful sync — reset disconnect tracking + clear stale "disconnected" alert state.
        _lastSuccessfulSyncUtc = DateTime.UtcNow;
        if (_disconnectedAlertFired && _alerter is not null)
        {
            _disconnectedAlertFired = false;
            await _alerter.SendAsync("okx_reconnected", AlertSeverity.Info,
                "OKX Reconnected",
                $"Balance sync resumed. Latest balance: ${account.Balance:N2}", ct).ConfigureAwait(false);
        }

        decimal newBalance = account.Balance;
        decimal previous;
        bool changed;
        lock (_portfolio)
        {
            previous = _portfolio.Cash;
            changed = Math.Abs(newBalance - _portfolio.InitialBalance) > 0.01m;
            if (changed)
            {
                _portfolio.InitialBalance = newBalance;
                // Cash sync rules:
                //   - LIVE hybrid mode: always mirror Cash from OKX (real orders move OKX demo,
                //     Cash MUST follow or PaperPortfolio drifts permanently from reality).
                //   - Otherwise: only seed Cash before the first closed position; afterwards let
                //     mirrored trades drive Cash so we preserve simulated PnL drift in dry-run/sim.
                if (_liveModeAlwaysSyncCash || _portfolio.ClosedPositions.Count == 0)
                {
                    _portfolio.Cash = newBalance;
                }
            }
            else if (_liveModeAlwaysSyncCash && Math.Abs(newBalance - _portfolio.Cash) > 0.01m)
            {
                // InitialBalance unchanged but Cash drifted (e.g. fees deducted by OKX after order).
                // In live mode, re-anchor Cash on every meaningful drift.
                _portfolio.Cash = newBalance;
            }
        }

        if (changed && Math.Abs(newBalance - _lastReportedBalance) > 0.01m)
        {
            _logger.LogInformation(
                "ExchangeBalanceSync: {Provider}/{Id} balance ${Balance:N2} (prev local Cash=${Prev:N2}); InitialBalance updated.",
                account.Provider, account.Id, newBalance, previous);
            _lastReportedBalance = newBalance;
        }
    }

    private async Task CheckDisconnectAlertAsync(CancellationToken ct)
    {
        if (_alerter is null) return;
        var staleness = DateTime.UtcNow - _lastSuccessfulSyncUtc;
        // Fire critical alert once per disconnect episode when staleness > 5min.
        if (!_disconnectedAlertFired && staleness > TimeSpan.FromMinutes(5))
        {
            _disconnectedAlertFired = true;
            await _alerter.SendAsync("okx_disconnected", AlertSeverity.Critical,
                "OKX Balance Sync Stalled",
                $"Last successful sync was {staleness.TotalMinutes:F1} min ago. " +
                "Hybrid execution may produce stale Kelly sizing or fail orders. " +
                "Check network + OKX status.", ct).ConfigureAwait(false);
        }
    }
}
