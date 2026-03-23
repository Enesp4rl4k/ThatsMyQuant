namespace ThatsMyQuant.Dashboard.Services;

public interface ITradingLoopService
{
    bool IsRunning { get; }
    Task StartAsync(Func<CancellationToken, Task> cycle, TimeSpan interval, CancellationToken cancellationToken = default);
    void Stop();
}