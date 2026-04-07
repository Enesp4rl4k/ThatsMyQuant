namespace ThatsMyQuant.Dashboard.Auth;

/// <summary>
/// Abstraction over a periodic tick source so <see cref="IdleMonitor"/> can be tested
/// without a real <see cref="DispatcherTimer"/>.
/// </summary>
public interface IIdleTicker
{
    event EventHandler Tick;
    void Start();
    void Stop();
}

#if WINDOWS
/// <summary>
/// Production ticker backed by <see cref="System.Windows.Threading.DispatcherTimer"/>
/// with a 1-second interval. Only compiled for WPF (Windows) targets.
/// </summary>
public sealed class DispatcherIdleTicker : IIdleTicker
{
    private readonly System.Windows.Threading.DispatcherTimer _timer;

    public DispatcherIdleTicker()
    {
        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (s, e) => Tick?.Invoke(s, e);
    }

    public event EventHandler? Tick;
    public void Start() => _timer.Start();
    public void Stop()  => _timer.Stop();
}
#endif

/// <summary>
/// Tracks elapsed idle time and raises <see cref="IdleWarning"/> and
/// <see cref="IdleElapsed"/> events according to <see cref="IdleTimeoutConfig"/>.
/// Requirements: 3.1, 3.2, 3.3, 3.4, 3.5, 3.6
/// </summary>
public class IdleMonitor
{
    private readonly IdleTimeoutConfig _config;
    private readonly IIdleTicker       _ticker;
    private int _elapsedSeconds;
    private bool _warningRaised;

    public event EventHandler? IdleWarning;
    public event EventHandler? IdleElapsed;

    /// <summary>
    /// Creates an <see cref="IdleMonitor"/> using the production
    /// <see cref="DispatcherIdleTicker"/>.
    /// </summary>
#if WINDOWS
    public IdleMonitor(IdleTimeoutConfig config)
        : this(config, new DispatcherIdleTicker()) { }
#endif

    /// <summary>
    /// Creates an <see cref="IdleMonitor"/> with an injectable ticker (for testing).
    /// </summary>
    public IdleMonitor(IdleTimeoutConfig config, IIdleTicker ticker)
    {
        _config = config;
        _ticker = ticker;
        _ticker.Tick += OnTick;
    }

    /// <summary>
    /// Starts the idle timer. No-op when <see cref="IdleTimeoutConfig.TimeoutSeconds"/> is 0.
    /// </summary>
    public void Start()
    {
        if (_config.TimeoutSeconds == 0) return;
        _ticker.Start();
    }

    /// <summary>Stops the idle timer.</summary>
    public void Stop() => _ticker.Stop();

    /// <summary>
    /// Resets the elapsed counter to zero. The next <see cref="IdleWarning"/> and
    /// <see cref="IdleElapsed"/> events will fire after a full timeout cycle.
    /// Requirement 3.2
    /// </summary>
    public void Reset()
    {
        _elapsedSeconds = 0;
        _warningRaised  = false;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _elapsedSeconds++;

        int warningAt = _config.TimeoutSeconds - _config.WarningLeadSeconds;

        // Raise IdleWarning once per cycle (Req 3.3)
        if (!_warningRaised && _config.WarningLeadSeconds > 0 && _elapsedSeconds >= warningAt)
        {
            _warningRaised = true;
            IdleWarning?.Invoke(this, EventArgs.Empty);
        }

        // Raise IdleElapsed when full timeout reached (Req 3.4)
        if (_elapsedSeconds >= _config.TimeoutSeconds)
        {
            IdleElapsed?.Invoke(this, EventArgs.Empty);
            Reset(); // ready for next cycle
        }
    }
}
