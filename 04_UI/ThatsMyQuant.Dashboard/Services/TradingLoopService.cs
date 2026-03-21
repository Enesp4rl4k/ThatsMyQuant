namespace ThatsMyQuant.Dashboard.Services;

public sealed class TradingLoopService : ITradingLoopService
{
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _loopTask is { IsCompleted: false };
            }
        }
    }

    public Task StartAsync(Func<CancellationToken, Task> cycle, TimeSpan interval, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_loopTask is { IsCompleted: false })
            {
                return Task.CompletedTask;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;

            _loopTask = Task.Run(async () =>
            {
                await cycle(token);

                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(interval, token);
                    await cycle(token);
                }
            }, token);
        }

        return Task.CompletedTask;
    }

    public void Stop()
    {
        lock (_sync)
        {
            _cts?.Cancel();
        }
    }
}
