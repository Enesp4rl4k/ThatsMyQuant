using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace ThatsMyQuant.Application.Resilience;

/// <summary>
/// Enterprise-grade resilience policies for external service calls.
/// Uses Polly v8 (native .NET 8 resilience pipeline).
/// </summary>
public static class ResiliencePolicies
{
    /// <summary>
    /// Retry policy: 3 retries with exponential backoff (1s, 2s, 4s).
    /// Use for transient failures (network timeouts, 429s, 5xx).
    /// </summary>
    public static ResiliencePipeline CreateRetryPipeline(ILogger? logger = null)
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                OnRetry = args =>
                {
                    logger?.LogWarning(
                        "Retry attempt {Attempt} after {Delay}ms. Exception: {Message}",
                        args.AttemptNumber,
                        args.RetryDelay.TotalMilliseconds,
                        args.Outcome.Exception?.Message ?? "Unknown");
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    /// Circuit Breaker: Opens after 5 failures, stays open for 30 seconds.
    /// Prevents hammering a dead service and gives it time to recover.
    /// </summary>
    public static ResiliencePipeline CreateCircuitBreakerPipeline(ILogger? logger = null)
    {
        return new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 5,
                BreakDuration = TimeSpan.FromSeconds(30),
                OnOpened = args =>
                {
                    logger?.LogError("Circuit OPENED! Service is down. Breaking for {Duration}s.",
                        args.BreakDuration.TotalSeconds);
                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    logger?.LogInformation("Circuit CLOSED. Service recovered.");
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = args =>
                {
                    logger?.LogWarning("Circuit HALF-OPEN. Testing service...");
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    /// Combined pipeline: Retry + Circuit Breaker together.
    /// Retry wraps Circuit Breaker (retries happen, but if too many fail, circuit opens).
    /// </summary>
    public static ResiliencePipeline CreateFullPipeline(ILogger? logger = null)
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                OnRetry = args =>
                {
                    logger?.LogWarning(
                        "Retry attempt {Attempt} after {Delay}ms.",
                        args.AttemptNumber,
                        args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 5,
                BreakDuration = TimeSpan.FromSeconds(30),
                OnOpened = args =>
                {
                    logger?.LogError("Circuit OPENED after repeated failures.");
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ => ValueTask.CompletedTask,
                OnHalfOpened = _ => ValueTask.CompletedTask
            })
            .Build();
    }
}
