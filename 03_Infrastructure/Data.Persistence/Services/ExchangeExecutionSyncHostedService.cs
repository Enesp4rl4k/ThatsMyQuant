using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Observability;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Persistence;

namespace ThatsMyQuant.Infrastructure.Services;

public sealed class ExchangeExecutionSyncHostedService : IHostedService, IObserver<ExecutionReport>
{
    private static readonly TimeSpan ReplayIntervalWithDrift = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReplayIntervalStable = TimeSpan.FromSeconds(45);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ExchangeExecutionSyncHostedService> _logger;
    private readonly VenueKind _venue;
    private readonly ExecutionSyncCheckpointStore _checkpointStore;
    private readonly ExecutionReportDedupeCache _dedupeCache;
    private CancellationTokenSource? _periodicReplayCts;
    private Task? _periodicReplayTask;
    private IExchangeExecutionSync? _executionSync;
    private IDisposable? _subscription;

    public ExchangeExecutionSyncHostedService(
        IServiceProvider serviceProvider,
        ILogger<ExchangeExecutionSyncHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _checkpointStore = new ExecutionSyncCheckpointStore(
            Path.Combine(AppContext.BaseDirectory, "logs", "execution_sync_checkpoint.json"));
        _dedupeCache = new ExecutionReportDedupeCache(10_000);

        using var scope = _serviceProvider.CreateScope();
        var configuration = scope.ServiceProvider.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
        if (configuration is null)
        {
            _venue = VenueKind.Binance;
            return;
        }

        var mode = SharedExchangeConfiguration.GetSelectedExchangeMode(configuration);
        _venue = mode.Equals("OKX", StringComparison.OrdinalIgnoreCase) ? VenueKind.Okx : VenueKind.Binance;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _executionSync = _serviceProvider.GetService<IExchangeExecutionSync>();
        if (_executionSync is null)
        {
            _logger.LogInformation("Execution sync host disabled because no IExchangeExecutionSync implementation is registered.");
            return;
        }

        try
        {
            await ReplayOpenOrdersAsync(cancellationToken);
            StartPeriodicReplayLoop();
            _subscription = _executionSync.ExecutionReports.Subscribe(this);
            await _executionSync.ConnectAsync(cancellationToken);
            _logger.LogInformation("Execution report synchronization connected.");
        }
        catch (Exception ex)
        {
            _subscription?.Dispose();
            _subscription = null;
            _logger.LogWarning(ex, "Execution report synchronization disabled because startup failed.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_periodicReplayCts is not null)
        {
            await _periodicReplayCts.CancelAsync();
            if (_periodicReplayTask is not null)
            {
                try
                {
                    await _periodicReplayTask.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Expected during stop.
                }
            }

            _periodicReplayCts.Dispose();
            _periodicReplayCts = null;
            _periodicReplayTask = null;
        }

        _subscription?.Dispose();
        _subscription = null;

        if (_executionSync is not null)
        {
            await _executionSync.DisconnectAsync(cancellationToken);
            await _executionSync.DisposeAsync();
            _executionSync = null;
        }
    }

    public void OnCompleted()
        => _logger.LogInformation("Execution report synchronization completed.");

    public void OnError(Exception error)
    {
        _logger.LogError(error, "Execution report synchronization stream faulted.");
        _ = ReplayOpenOrdersAsync(CancellationToken.None);
    }

    public void OnNext(ExecutionReport value)
        => _ = ApplyExecutionReportAsync(value);

    private async Task ApplyExecutionReportAsync(ExecutionReport report)
    {
        try
        {
            var telemetry = _serviceProvider.GetService<OperationalTelemetry>();
            telemetry?.RecordExecutionReportReceived(report.Symbol);

            if (!_dedupeCache.ShouldProcess(report))
            {
                telemetry?.RecordExecutionReportDedupedMemory(report.Symbol);
                _logger.LogDebug(
                    "Execution report skipped by in-memory dedupe for {ExchangeOrderId} ({Symbol}/{Status}, LastFill={LastFillQuantity}).",
                    report.ExchangeOrderId,
                    report.Symbol,
                    report.Status,
                    report.LastFillQuantity);
                return;
            }

            var checkpointDecision = _checkpointStore.Evaluate(report);
            if (checkpointDecision != ExecutionSyncCheckpointStore.CheckpointDecision.Apply)
            {
                if (checkpointDecision == ExecutionSyncCheckpointStore.CheckpointDecision.OutOfOrder)
                {
                    telemetry?.RecordExecutionReportOutOfOrder(report.Symbol);
                }
                else
                {
                    telemetry?.RecordExecutionReportDedupedCheckpoint(report.Symbol);
                }

                _logger.LogDebug(
                    "Execution report skipped by checkpoint store for {Symbol}/{ClientOrderId} at {Timestamp}. Decision={Decision}.",
                    report.Symbol,
                    report.ClientOrderId,
                    report.TimestampUtc,
                    checkpointDecision);
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var syncService = scope.ServiceProvider.GetRequiredService<TradeExecutionSyncService>();
            var portfolio = scope.ServiceProvider.GetService<ISharedPortfolioStateManager>();
            var applied = await syncService.ApplyExecutionReportDetailedAsync(report);

            if (applied.Applied && portfolio is not null && applied.Trade is not null)
            {
                ApplyPortfolioDelta(portfolio, applied.Trade, applied.DeltaFilledQuantity, report.ClientOrderId);
            }

            _logger.LogDebug(
                "Execution report received for {Symbol}/{ClientOrderId}. Applied={Applied}, Status={Status}, Filled={FilledQuantity}, Remaining={RemainingQuantity}",
                report.Symbol,
                report.ClientOrderId,
                applied.Applied,
                report.Status,
                report.FilledQuantity,
                report.RemainingQuantity);

            _checkpointStore.Commit(report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply execution report for {Symbol}/{ClientOrderId}.", report.Symbol, report.ClientOrderId);
        }
    }

    private void StartPeriodicReplayLoop()
    {
        _periodicReplayCts = new CancellationTokenSource();
        var token = _periodicReplayCts.Token;
        _periodicReplayTask = Task.Run(async () =>
        {
            var nextDelay = ReplayIntervalStable;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(nextDelay, token);
                    var driftDetected = await ReplayOpenOrdersAsync(token);
                    nextDelay = driftDetected ? ReplayIntervalWithDrift : ReplayIntervalStable;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Execution sync periodic replay loop error.");
                    nextDelay = ReplayIntervalWithDrift;
                }
            }
        }, token);
    }

    private async Task<bool> ReplayOpenOrdersAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var telemetryScope = _serviceProvider.CreateScope();
            var telemetry = telemetryScope.ServiceProvider.GetService<OperationalTelemetry>();
            telemetry?.RecordExecutionSyncReplayRun();

            using var scope = _serviceProvider.CreateScope();
            var exchangeService = scope.ServiceProvider.GetService<IExchangeService>();
            var syncService = scope.ServiceProvider.GetService<TradeExecutionSyncService>();
            if (exchangeService is not IExchangeOrderStateProvider orderStateProvider || syncService is null)
            {
                return false;
            }

            var openOrders = await orderStateProvider.GetOpenOrdersAsync(cancellationToken);
            var result = await syncService.ReconcileOpenOrdersAsync(openOrders, cancellationToken);
            _logger.LogInformation(
                "Execution sync replay complete. Matched={Matched}, Updated={Updated}, Orphaned={Orphaned}, Missing={Missing}",
                result.MatchedTrades,
                result.UpdatedTrades,
                result.OrphanedVenueOrders,
                result.MissingLocalOrders);
            var driftCount = result.OrphanedVenueOrders + result.MissingLocalOrders;
            if (driftCount > 0)
            {
                telemetry?.RecordExecutionSyncReplayDrift(driftCount);
            }

            return driftCount > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Execution sync replay failed.");
            return true;
        }
    }

    private void ApplyPortfolioDelta(ISharedPortfolioStateManager portfolio, Trade trade, decimal deltaFilledQuantity, string? clientOrderId)
    {
        if (deltaFilledQuantity <= 0m)
        {
            if (IsTerminalLifecycle(trade))
            {
                var orderId = ResolveOrderReservationId(trade, clientOrderId);
                portfolio.ReleaseAllReservationsForOrder(_venue, "USDT", orderId);
            }
            return;
        }

        // Position delta is signed by side.
        var signedQty = trade.Side == ThatsMyQuant.Contracts.Enums.TradeSide.Sell
            ? -deltaFilledQuantity
            : deltaFilledQuantity;
        portfolio.UpdatePosition(_venue, trade.Symbol, signedQty);

        var orderReservationId = ResolveOrderReservationId(trade, clientOrderId);
        if (trade.Side == ThatsMyQuant.Contracts.Enums.TradeSide.Buy)
        {
            var fillNotional = deltaFilledQuantity * Math.Max(trade.EntryPrice, trade.MarkPrice);
            if (fillNotional > 0m)
            {
                portfolio.ReleaseReservationForOrder(_venue, "USDT", orderReservationId, fillNotional);
            }
        }

        // For completed terminal states, force-release any remaining reservation once.
        if (IsTerminalLifecycle(trade))
        {
            portfolio.ReleaseAllReservationsForOrder(_venue, "USDT", orderReservationId);
        }
    }

    private static bool IsTerminalLifecycle(Trade trade)
        => trade.LifecycleStatus is ThatsMyQuant.Contracts.Enums.TradeLifecycleStatus.Active
            or ThatsMyQuant.Contracts.Enums.TradeLifecycleStatus.Closed
            or ThatsMyQuant.Contracts.Enums.TradeLifecycleStatus.Cancelled
            or ThatsMyQuant.Contracts.Enums.TradeLifecycleStatus.Rejected
            or ThatsMyQuant.Contracts.Enums.TradeLifecycleStatus.Failed;

    private static string ResolveOrderReservationId(Trade trade, string? clientOrderId)
        => !string.IsNullOrWhiteSpace(clientOrderId)
            ? clientOrderId
            : ThatsMyQuant.Contracts.Helpers.ExecutionOrderIdentity.CreateClientOrderId(trade.SignalId);
}
