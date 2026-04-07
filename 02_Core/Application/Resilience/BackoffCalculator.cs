namespace ThatsMyQuant.Application.Resilience;

/// <summary>
/// Computes exponential backoff delays with optional jitter for reconnect loops.
/// </summary>
public static class BackoffCalculator
{
    /// <summary>
    /// Computes exponential backoff delay with optional jitter.
    /// Base delay = min(2^attempt, 60) seconds.
    /// Jitter adds a random value in [0, base * jitterFactor].
    /// </summary>
    /// <param name="attempt">Zero-based reconnect attempt number.</param>
    /// <param name="jitterFactor">Fraction of base delay to use as max jitter (default 0.3 = 30%).</param>
    public static TimeSpan Compute(int attempt, double jitterFactor = 0.3)
    {
        var baseSeconds = Math.Min(Math.Pow(2, attempt), 60.0);
        var jitter = Random.Shared.NextDouble() * baseSeconds * jitterFactor;
        return TimeSpan.FromSeconds(baseSeconds + jitter);
    }
}
