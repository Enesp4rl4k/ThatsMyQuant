using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MediatR;
using ThatsMyQuant.Application.Commands;
using ThatsMyQuant.Domain.Enums;
using ThatsMyQuant.Domain.Interfaces;

namespace ThatsMyQuant.Execution.Worker;

public class ExecutionWorker : BackgroundService
{
    private readonly ILogger<ExecutionWorker> _logger;
    private readonly IServiceProvider _serviceProvider;

    public ExecutionWorker(ILogger<ExecutionWorker> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Execution.Worker (The Hand) starting...");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var signalRepository = scope.ServiceProvider.GetRequiredService<ISignalRepository>();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

                var queuedSignals = await signalRepository.GetByStatusAsync(TradeStatus.Queued, 25);
                foreach (var signal in queuedSignals)
                {
                    try
                    {
                        var tradeResult = await mediator.Send(new ExecuteTradeCommand(signal), stoppingToken);
                        
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
                        
                        await signalRepository.UpdateAsync(signal);
                    }
                    catch (Exception ex)
                    {
                        signal.RetryCount++;
                        if (signal.RetryCount >= 3)
                        {
                            signal.Status = TradeStatus.DeadLetter;
                            signal.Reasoning = $"{signal.Reasoning} | Worker execution failed (Max Retries): {ex.Message}";
                        }
                        else
                        {
                            // Stays in Queued status for next poll
                            signal.Reasoning = $"{signal.Reasoning} | Worker execution failed (Retry {signal.RetryCount}): {ex.Message}";
                        }
                        
                        await signalRepository.UpdateAsync(signal);
                        _logger.LogError(ex, "Failed executing queued signal {SignalId} (Attempt {Attempt})", signal.Id, signal.RetryCount);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Execution worker polling loop failed.");
            }

            await Task.Delay(1500, stoppingToken);
        }
    }
}
