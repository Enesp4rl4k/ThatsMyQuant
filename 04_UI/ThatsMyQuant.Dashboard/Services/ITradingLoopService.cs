using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Dashboard.Services;

public sealed class TradingLoopService : ITradingLoopService, IDisposable
{
    private readonly ILogger<TradingLoopService> _logger;
    private CancellationTokenSource? _loopCts;
    private Task? _executingTask;
    private bool _disposed;

    public bool IsRunning => _executingTask != null && !_executingTask.IsCompleted;

    public TradingLoopService(ILogger<TradingLoopService> logger) => _logger = logger;

    public async Task StartAsync(Func<CancellationToken, Task> cycle, TimeSpan interval, CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _executingTask = RunLoopAsync(cycle, interval, _loopCts.Token);
        
        _logger.LogInformation("Trading Loop started with interval: {Interval}ms", interval.TotalMilliseconds);
        await Task.CompletedTask;
    }

    private async Task RunLoopAsync(Func<CancellationToken, Task> cycle, TimeSpan interval, CancellationToken token)
    {
        // PeriodicTimer .NET 6+ ile gelen en hassas zamanlayıcıdır (Thread-safe ve drift-free)
        using var timer = new PeriodicTimer(interval);

        while (!token.IsCancellationRequested && await timer.WaitForNextTickAsync(token))
        {
            try
            {
                // SonarQube Guard: İşlem döngüsünü asenkron ve güvenli çalıştır
                await cycle(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical error in Trading Cycle. Bot is recovering for next tick...");
                // İsteğe bağlı: Burada arka arkaya 5 hata alırsak Stop() çağırabiliriz (Circuit Breaker)
            }
        }
    }

    public void Stop()
    {
        _logger.LogWarning("Stopping Trading Loop...");
        _loopCts?.Cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loopCts?.Cancel();
        _loopCts?.Dispose();
    }
}