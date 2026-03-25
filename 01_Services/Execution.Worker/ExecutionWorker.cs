using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MediatR;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Application.Strategy;

namespace ThatsMyQuant.Execution.Worker;

public class ExecutionWorker : BackgroundService
{
    private readonly ILogger<ExecutionWorker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly TickPipeline _tickPipeline;

    public ExecutionWorker(
        ILogger<ExecutionWorker> logger, 
        IServiceProvider serviceProvider,
        TickPipeline tickPipeline)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _tickPipeline = tickPipeline;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Execution.Worker (The Hand) starting...");
        
        // Start the zero-allocation tick pipeline (MiroFish Pattern)
        _tickPipeline.Start(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            await PollAndProcessSignals(stoppingToken);
            await Task.Delay(1500, stoppingToken);
        }
    }

    private async Task PollAndProcessSignals(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var signalRepository = scope.ServiceProvider.GetRequiredService<ISignalRepository>();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            var queuedSignals = await signalRepository.GetByStatusAsync(TradeStatus.Queued, 25);
            foreach (var signal in queuedSignals)
            {
                await ProcessSingleSignal(signal, signalRepository, mediator, stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Execution worker polling loop failed.");
        }
    }

    private async Task ProcessSingleSignal(Signal signal, ISignalRepository signalRepository, IMediator mediator, CancellationToken stoppingToken)
    {
        try
        {
            var tradeResult = await mediator.Send(new ExecuteTradeCommand(signal), stoppingToken);
            ApplyExecutionResult(signal, tradeResult);
        }
        catch (Exception ex)
        {
            HandleExecutionException(signal, ex);
        }
        finally
        {
            await signalRepository.UpdateAsync(signal);
        }
    }

    private void ApplyExecutionResult(Signal signal, Trade? tradeResult)
    {
        if (tradeResult != null && tradeResult.Status == TradeStatus.Executed)
        {
            signal.Status = TradeStatus.Executed;
            _logger.LogInformation("Executed queued signal {SignalId} ({Symbol})", signal.Id, signal.Symbol);
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
        }
        else
        {
            signal.Reasoning = $"{signal.Reasoning} | Worker execution failed (Retry {signal.RetryCount}): {ex.Message}";
        }
        _logger.LogError(ex, "Failed executing queued signal {SignalId} (Attempt {Attempt})", signal.Id, signal.RetryCount);
    }

}
