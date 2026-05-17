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
    // [Sprint 5 — 2026-05-10] Config-defined basis cap. Honors operator intent
    // (e.g. "$2K cycle") even when the OKX demo account is over-funded ($96K from a
    // pre-pivot funding event). When > 0, the synced basis is min(OKX, cap).
    // 0 = no cap (legacy behaviour: always use OKX demo balance directly).
    private readonly decimal _configBasisCap;
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

        // [Sprint 5 — 2026-05-10] Read Paper:InitialBalance as a CAP (not a strict assignment).
        // Paper-trade demo OKX accounts are typically over-funded relative to the operator's
        // intended experimental capital — e.g. our demo account has $96K because it was
        // provisioned for the original $100K cycle, but the current operator's intent is a
        // $2K experimental cycle (see appsettings.Paper.json _InitialBalance_note_2026_05_08).
        // Without honoring the cap, EdgeLab strategies size off $96K, blowing past the
        // operator's risk budget by ~48× and making realized PnL incomparable with the
        // backtest expectations that assumed $2K basis.
        _configBasisCap = decimal.TryParse(configuration["Paper:InitialBalance"],
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var cap) && cap > 0m ? cap : 0m;
        if (_configBasisCap > 0m)
        {
            _logger.LogInformation(
                "ExchangeBalanceSync: Paper:InitialBalance cap = ${Cap:N2}. Synced basis will be min(OKX_demo_balance, cap).",
                _configBasisCap);
        }
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

        // [Sprint 5 — 2026-05-10] Apply config basis cap before mutating the portfolio.
        // The capped value is what every downstream service should see as "the basis".
        decimal effectiveBalance = account.Balance;
        if (_configBasisCap > 0m && account.Balance > _configBasisCap)
        {
            effectiveBalance = _configBasisCap;
        }
        decimal newBalance = effectiveBalance;
        decimal previous;
        bool changed;
        bool forcedCashReanchor = false;
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
                //   - Stale-snapshot defence (Sprint 5, 2026-05-10): when current Cash differs from
                //     the OKX demo balance by more than 50% AND the gap is > $1K, the
                //     PaperPortfolio is almost certainly carrying state from a pre-config-pivot
                //     snapshot (e.g. the $100K → $2K cycle pivot left $96K Cash on disk while OKX
                //     demo only has $5K). Force a re-anchor in this case to prevent EdgeLab from
                //     sizing 20× too large — the historical B1 deferred bug.
                //   - Otherwise: only seed Cash before the first closed position; afterwards let
                //     mirrored trades drive Cash so we preserve simulated PnL drift in dry-run/sim.
                var cashGap = Math.Abs(newBalance - _portfolio.Cash);
                var cashRatio = _portfolio.Cash > 0m ? newBalance / _portfolio.Cash : 1m;
                var staleSnapshot = cashGap > 1_000m && (cashRatio < 0.5m || cashRatio > 2m);

                if (_liveModeAlwaysSyncCash || _portfolio.ClosedPositions.Count == 0 || staleSnapshot)
                {
                    _portfolio.Cash = newBalance;
                    forcedCashReanchor = staleSnapshot;
                }
            }
            else if (_liveModeAlwaysSyncCash && Math.Abs(newBalance - _portfolio.Cash) > 0.01m)
            {
                // InitialBalance unchanged but Cash drifted (e.g. fees deducted by OKX after order).
                // In live mode, re-anchor Cash on every meaningful drift.
                _portfolio.Cash = newBalance;
            }
        }

        if (forcedCashReanchor)
        {
            _logger.LogWarning(
                "ExchangeBalanceSync: STALE SNAPSHOT detected — re-anchored Cash from ${Prev:N2} to ${New:N2} " +
                "(OKX demo balance is the source of truth; PaperPortfolio snapshot was carrying " +
                "pre-config-pivot state). All EdgeLab strategies will now size off the correct basis.",
                previous, newBalance);
            if (_alerter is not null)
            {
                try
                {
                    await _alerter.SendAsync("basis_reanchored", AlertSeverity.Warning,
                        "Paper basis re-anchored to OKX demo",
                        $"Stale snapshot Cash=${previous:N2} → OKX ${newBalance:N2}. " +
                        "EdgeLab basis is now correct.", ct).ConfigureAwait(false);
                }
                catch { /* best-effort */ }
            }
        }

        if (changed && Math.Abs(newBalance - _lastReportedBalance) > 0.01m)
        {
            var capNote = _configBasisCap > 0m && account.Balance > _configBasisCap
                ? $" (CAPPED from OKX ${account.Balance:N2} to config ${_configBasisCap:N2})"
                : string.Empty;
            _logger.LogInformation(
                "ExchangeBalanceSync: {Provider}/{Id} balance ${Balance:N2}{Cap} (prev local Cash=${Prev:N2}); InitialBalance updated.",
                account.Provider, account.Id, newBalance, capNote, previous);
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
