using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MediatR;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Application.Observability;
using ThatsMyQuant.Application.Strategy;

namespace ThatsMyQuant.Execution.Worker;

public class ExecutionWorker : BackgroundService
{
    private static readonly TimeSpan ClaimLeaseTimeout = TimeSpan.FromSeconds(30);
    private readonly ILogger<ExecutionWorker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly TickPipeline _tickPipeline;
    private readonly ExecutionSignalQueue _executionSignalQueue;
    private readonly OperationalTelemetry _telemetry;
    private readonly CircuitBreakerService _circuitBreaker;
    private readonly string _workerId = $"exec-{Environment.MachineName}-{Environment.ProcessId}";

    public ExecutionWorker(
        ILogger<ExecutionWorker> logger, 
        IServiceProvider serviceProvider,
        TickPipeline tickPipeline,
        ExecutionSignalQueue executionSignalQueue,
        OperationalTelemetry telemetry,
        CircuitBreakerService circuitBreaker)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _tickPipeline = tickPipeline;
        _executionSignalQueue = executionSignalQueue;
        _telemetry = telemetry;
        _circuitBreaker = circuitBreaker;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Execution.Worker (The Hand) starting...");
        
        // Start the zero-allocation tick pipeline (MiroFish Pattern)
        _tickPipeline.Start(stoppingToken);
        await RunStartupReconciliationAsync(stoppingToken);
        await SeedQueuedSignalsAsync(stoppingToken);

        var queueTask = DrainExecutionQueueAsync(stoppingToken);
        var recoveryTask = RunSignalRecoveryLoopAsync(stoppingToken);
        var lifecycleTask = RunLifecycleLoopAsync(stoppingToken);
        try
        {
            await Task.WhenAll(queueTask, recoveryTask, lifecycleTask);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunSignalRecoveryLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (_circuitBreaker.IsHalted)
            {
                _logger.LogWarning("ExecutionWorker: system halted, skipping queue recovery.");
                continue;
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var signalRepository = scope.ServiceProvider.GetRequiredService<ISignalRepository>();
                await ClaimAndEnqueueSignalsAsync(signalRepository, 25, stoppingToken);

                _telemetry.SetExecutionSignalQueueDepth(_executionSignalQueue.PendingCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Execution worker queue recovery loop failed.");
            }
        }
    }

    private async Task RunLifecycleLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var lifecycleService = scope.ServiceProvider.GetRequiredService<LivePositionLifecycleService>();
                var supervised = await lifecycleService.SuperviseOpenTradesAsync(stoppingToken);
                _telemetry.RecordLifecycleSupervision(supervised);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Execution worker lifecycle supervision loop failed.");
            }
        }
    }

    private async Task DrainExecutionQueueAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var signal in _executionSignalQueue.ReadAllAsync(stoppingToken))
            {
                _telemetry.SetExecutionSignalQueueDepth(_executionSignalQueue.PendingCount);

                using var scope = _serviceProvider.CreateScope();
                var signalRepository = scope.ServiceProvider.GetRequiredService<ISignalRepository>();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

                try
                {
                    await ProcessSingleSignal(signal, signalRepository, mediator, stoppingToken);
                }
                finally
                {
                    _executionSignalQueue.MarkCompleted(signal.Id);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SeedQueuedSignalsAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var signalRepository = scope.ServiceProvider.GetRequiredService<ISignalRepository>();
        await ClaimAndEnqueueSignalsAsync(signalRepository, 100, stoppingToken);
    }

    private async Task RunStartupReconciliationAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var exchangeService = scope.ServiceProvider.GetService<IExchangeService>();
            var syncService = scope.ServiceProvider.GetService<TradeExecutionSyncService>();

            if (exchangeService is not IExchangeOrderStateProvider orderStateProvider || syncService is null)
            {
                return;
            }

            var openOrders = await orderStateProvider.GetOpenOrdersAsync(stoppingToken);
            var result = await syncService.ReconcileOpenOrdersAsync(openOrders, stoppingToken);

            _logger.LogInformation(
                "Startup execution reconciliation complete. Matched={Matched}, Updated={Updated}, OrphanedVenueOrders={Orphaned}, MissingLocalOrders={Missing}",
                result.MatchedTrades,
                result.UpdatedTrades,
                result.OrphanedVenueOrders,
                result.MissingLocalOrders);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Execution startup reconciliation failed.");
        }
    }

    private async Task ProcessSingleSignal(Signal signal, ISignalRepository signalRepository, IMediator mediator, CancellationToken stoppingToken)
    {
        using var logScope = TradingLogScope.Begin(_logger, signal);

        // Circuit breaker check before dispatch
        var (allowed, blockReason) = _circuitBreaker.CheckOrderAllowed();
        if (!allowed)
        {
            signal.Status = TradeStatus.Rejected;
            signal.Reasoning += $" | CircuitBreaker: {blockReason}";
            ReleaseClaim(signal);
            _logger.LogWarning("Signal {Id} blocked by circuit breaker: {Reason}", signal.Id, blockReason);
            await signalRepository.UpdateAsync(signal);
            return;
        }

        try
        {
            var tradeResult = await mediator.Send(new ExecuteTradeCommand(signal), stoppingToken);
            ApplyExecutionResult(signal, tradeResult);
            _circuitBreaker.RecordSuccess();
        }
        catch (Exception ex)
        {
            HandleExecutionException(signal, ex);
            _circuitBreaker.RecordError(ex);
        }
        finally
        {
            ReleaseClaim(signal);
            await signalRepository.UpdateAsync(signal);
        }
    }

    private async Task ClaimAndEnqueueSignalsAsync(ISignalRepository signalRepository, int count, CancellationToken stoppingToken)
    {
        var now = DateTime.UtcNow;
        var staleClaimBeforeUtc = now - ClaimLeaseTimeout;
        var candidates = await signalRepository.GetExecutionClaimCandidatesAsync(count, staleClaimBeforeUtc);

        foreach (var candidate in candidates)
        {
            if (!await signalRepository.TryClaimQueuedAsync(candidate.Id, _workerId, now, staleClaimBeforeUtc))
                continue;

            candidate.Status = TradeStatus.Processing;
            candidate.ClaimedAtUtc = now;
            candidate.ClaimedBy = _workerId;
            await _executionSignalQueue.EnqueueAsync(candidate, stoppingToken);
        }
    }

    private void ApplyExecutionResult(Signal signal, Trade? tradeResult)
    {
        if (tradeResult != null && tradeResult.Status != TradeStatus.Failed)
        {
            signal.Status = tradeResult.Status;
            _logger.LogInformation(
                "Execution worker updated Signal {SignalId} ({Symbol}) to {Status}/{Lifecycle}",
                signal.Id,
                signal.Symbol,
                tradeResult.Status,
                tradeResult.LifecycleStatus);
        }
        else
        {
            signal.Status = TradeStatus.Failed;
            signal.Reasoning += $" | Execution returned non-success: {tradeResult?.Status}";
            _logger.LogWarning("Queued signal {SignalId} failed execution result: {Status}", signal.Id, tradeResult?.Status);
        }
    }

    private void HandleExecutionException(Signal signal, Exception ex)
    {
        signal.RetryCount++;
        if (signal.RetryCount >= 3)
        {
            signal.Status = TradeStatus.DeadLetter;
            signal.Reasoning = $"{signal.Reasoning} | Worker execution failed (Max Retries): {ex.Message}";
            _telemetry.RecordDeadLetter(signal.Symbol);
        }
        else
        {
            signal.Reasoning = $"{signal.Reasoning} | Worker execution failed (Retry {signal.RetryCount}): {ex.Message}";
        }
        _logger.LogError(ex, "Failed executing queued signal {SignalId} (Attempt {Attempt})", signal.Id, signal.RetryCount);
    }

    private static void ReleaseClaim(Signal signal)
    {
        signal.ClaimedAtUtc = null;
        signal.ClaimedBy = null;

        if (signal.Status == TradeStatus.Processing)
            signal.Status = TradeStatus.Queued;
    }

}
