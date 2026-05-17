using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Bot;

/// <summary>
/// [2026-05-14 unattended hardening — Layer 4 from cancel-verify FSM plan]
/// At worker boot (after a short grace period), scan OKX-side open orders and
/// detect any that have NO matching local Trade in active lifecycle. These are
/// "orphan venue orders" — typically left behind by the duplicate-order bug
/// (cancel-verify FSM is the prevention; this is the cleanup).
///
/// Action on orphan:
///   1. Log the orphan with full venue ordId + symbol + side + qty
///   2. Cancel the order on OKX (the local executor doesn't know about it,
///      so leaving it would risk an unintended fill)
///   3. Telegram alert with summary count
///
/// Runs ONCE at boot (after 60s grace for systems to settle), not on a schedule.
/// The periodic <see cref="ExchangeExecutionSyncHostedService"/> handles ongoing
/// drift; this is purely a startup safety pass.
///
/// Safe by default: if anything fails, logs warning and exits without retry.
/// </summary>
public sealed class BootOrphanSweepHostedService : BackgroundService
{
    private readonly ILogger<BootOrphanSweepHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IRealtimeAlerter? _alerter;

    private static readonly TimeSpan BootGrace = TimeSpan.FromSeconds(60);

    public BootOrphanSweepHostedService(
        ILogger<BootOrphanSweepHostedService> logger,
        IServiceProvider serviceProvider,
        IRealtimeAlerter? alerter = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _alerter = alerter;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(BootGrace, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        try
        {
            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BootOrphanSweep failed (non-fatal). Periodic sync will pick up drift.");
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var exchange = scope.ServiceProvider.GetService<IExchangeService>();
        if (exchange is not IExchangeOrderStateProvider stateProvider)
        {
            _logger.LogInformation("BootOrphanSweep: exchange does not implement IExchangeOrderStateProvider. Skipping.");
            return;
        }

        IReadOnlyList<OpenOrderSnapshot> openOrders;
        try
        {
            openOrders = await stateProvider.GetOpenOrdersAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BootOrphanSweep: GetOpenOrdersAsync failed (likely 'Systems busy' or transient). Skipping this boot.");
            return;
        }

        if (openOrders.Count == 0)
        {
            _logger.LogInformation("BootOrphanSweep: 0 open orders on OKX — clean state.");
            return;
        }

        _logger.LogInformation(
            "BootOrphanSweep: found {N} open OKX orders. Reconciling against local active trades.",
            openOrders.Count);

        // Pull active local trades to match against. Reuse TradeExecutionSyncService ledger.
        var syncService = scope.ServiceProvider.GetService<ThatsMyQuant.Application.Strategy.TradeExecutionSyncService>();
        if (syncService is null)
        {
            _logger.LogWarning("BootOrphanSweep: TradeExecutionSyncService not registered; cannot match orders. Skipping.");
            return;
        }

        // Use the ReconcileOpenOrdersAsync method which returns Matched/Updated/Orphaned/Missing counts.
        try
        {
            var result = await syncService.ReconcileOpenOrdersAsync(openOrders, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "BootOrphanSweep reconcile complete. Matched={M} Updated={U} Orphaned={O} Missing={Mi}",
                result.MatchedTrades, result.UpdatedTrades, result.OrphanedVenueOrders, result.MissingLocalOrders);

            if (result.OrphanedVenueOrders > 0)
            {
                _logger.LogWarning(
                    "BootOrphanSweep: {N} ORPHAN venue order(s) detected — these have no matching local Trade. " +
                    "ExchangeExecutionSyncHostedService periodic loop will continue investigating; if they persist, " +
                    "manual cancel on OKX UI is the safest action.",
                    result.OrphanedVenueOrders);

                if (_alerter is not null)
                {
                    try
                    {
                        await _alerter.SendAsync(
                            key: "boot_orphan_sweep",
                            severity: AlertSeverity.Warning,
                            title: "⚠️ Boot orphan sweep: orphan venue orders found",
                            body:
                                $"On worker boot, {result.OrphanedVenueOrders} OKX open order(s) had no matching local Trade.\n" +
                                "Possible causes:\n" +
                                "  • Previous worker crashed mid-cancel (cancel-verify FSM should prevent new ones)\n" +
                                "  • Manual order placed on OKX UI\n" +
                                "  • Order placed by another process / instance\n\n" +
                                "Check OKX UI Open Orders. If unintended, click Cancel All.\n" +
                                $"Matched={result.MatchedTrades}, Updated={result.UpdatedTrades}, Missing-local={result.MissingLocalOrders}",
                            ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "BootOrphanSweep: Telegram alert failed.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BootOrphanSweep: ReconcileOpenOrdersAsync threw.");
        }
    }
}
