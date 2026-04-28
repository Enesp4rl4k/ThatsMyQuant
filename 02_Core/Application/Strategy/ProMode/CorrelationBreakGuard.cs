using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Application.Strategy.ProMode;

/// <summary>
/// Correlation-break guard. Tracks daily returns for each strategy over a rolling
/// 30-day window, computes average off-diagonal Pearson correlation, and emits
/// state transitions when correlations spike — a canary for regime transitions
/// and "everything correlates to 1 in a crisis" events (March 2020, May 2022 LUNA,
/// November 2022 FTX).
///
/// Thresholds (empirically derived from crypto crisis episodes):
///   Normal:   avg |ρ| ≤ 0.30 — diversification intact
///   Elevated: 0.30 < avg |ρ| ≤ 0.50 — mild regime alignment, stay alert
///   Warning:  0.50 < avg |ρ| ≤ 0.70 — meaningful correlation rise, consider cash
///   Panic:    avg |ρ| > 0.70 — crisis signature, ALL to cash + halt new entries
///
/// Thread-safe. Singleton. Fed daily P&L per strategy; state polled by the
/// allocator / circuit breaker / UI.
/// </summary>
public sealed class CorrelationBreakGuard
{
    private readonly ILogger<CorrelationBreakGuard> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, Queue<decimal>> _dailyPnlPerStrategy = new(StringComparer.OrdinalIgnoreCase);
    private CorrelationGuardState _state = CorrelationGuardState.Normal;
    private double _latestAvgCorrelation;
    private DateTime _lastUpdateUtc;

    public int RollingWindowDays { get; init; } = 30;
    public double ElevatedThreshold { get; init; } = 0.30;
    public double WarningThreshold { get; init; } = 0.50;
    public double PanicThreshold { get; init; } = 0.70;

    public CorrelationBreakGuard(ILogger<CorrelationBreakGuard> logger) { _logger = logger; }

    public CorrelationGuardSnapshot Snapshot()
    {
        lock (_lock)
            return new CorrelationGuardSnapshot(
                State: _state,
                AverageCorrelation: _latestAvgCorrelation,
                StrategyCount: _dailyPnlPerStrategy.Count,
                LastUpdateUtc: _lastUpdateUtc);
    }

    /// <summary>Feed a daily PnL observation per strategy. Triggers state recompute.</summary>
    public void RecordDailyPnl(DateTime dayUtc, IReadOnlyDictionary<string, decimal> dailyPnlByStrategy)
    {
        lock (_lock)
        {
            foreach (var kv in dailyPnlByStrategy)
            {
                if (!_dailyPnlPerStrategy.TryGetValue(kv.Key, out var q))
                {
                    q = new Queue<decimal>(RollingWindowDays);
                    _dailyPnlPerStrategy[kv.Key] = q;
                }
                q.Enqueue(kv.Value);
                while (q.Count > RollingWindowDays) q.Dequeue();
            }

            _lastUpdateUtc = dayUtc;
            Recompute();
        }
    }

    private void Recompute()
    {
        var names = _dailyPnlPerStrategy.Keys.ToArray();
        if (names.Length < 2) { _state = CorrelationGuardState.Normal; _latestAvgCorrelation = 0; return; }

        int n = _dailyPnlPerStrategy.Values.Min(q => q.Count);
        if (n < 10) { _state = CorrelationGuardState.Normal; _latestAvgCorrelation = 0; return; }

        // Align all series to common length (last n points).
        var series = names.ToDictionary(
            name => name,
            name => _dailyPnlPerStrategy[name].TakeLast(n).Select(v => (double)v).ToArray(),
            StringComparer.OrdinalIgnoreCase);

        // Compute average off-diagonal |correlation|.
        double sumAbs = 0; int count = 0;
        for (int i = 0; i < names.Length; i++)
        for (int j = i + 1; j < names.Length; j++)
        {
            var c = Math.Abs(Pearson(series[names[i]], series[names[j]]));
            if (double.IsNaN(c) || double.IsInfinity(c)) continue;
            sumAbs += c;
            count++;
        }

        _latestAvgCorrelation = count > 0 ? sumAbs / count : 0.0;
        var prev = _state;
        _state = _latestAvgCorrelation switch
        {
            > 0.70 => CorrelationGuardState.Panic,
            > 0.50 => CorrelationGuardState.Warning,
            > 0.30 => CorrelationGuardState.Elevated,
            _ => CorrelationGuardState.Normal
        };

        if (_state != prev)
        {
            var level = _state switch
            {
                CorrelationGuardState.Panic => LogLevel.Error,
                CorrelationGuardState.Warning => LogLevel.Warning,
                CorrelationGuardState.Elevated => LogLevel.Information,
                _ => LogLevel.Debug
            };
            _logger.Log(level,
                "Correlation guard: {Prev} → {State}. Avg |ρ| = {Corr:F3} over {N}-day window across {K} strategies.",
                prev, _state, _latestAvgCorrelation, n, names.Length);
        }
    }

    private static double Pearson(double[] x, double[] y)
    {
        if (x.Length != y.Length || x.Length < 2) return 0.0;
        double mx = x.Average(), my = y.Average();
        double num = 0, dx = 0, dy = 0;
        for (int i = 0; i < x.Length; i++)
        {
            var a = x[i] - mx; var b = y[i] - my;
            num += a * b;
            dx += a * a;
            dy += b * b;
        }
        if (dx < 1e-12 || dy < 1e-12) return 0.0;
        return num / Math.Sqrt(dx * dy);
    }
}

public enum CorrelationGuardState { Normal, Elevated, Warning, Panic }

public sealed record CorrelationGuardSnapshot(
    CorrelationGuardState State,
    double AverageCorrelation,
    int StrategyCount,
    DateTime LastUpdateUtc);
