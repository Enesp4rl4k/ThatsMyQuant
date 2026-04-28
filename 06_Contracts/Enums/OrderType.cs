namespace ThatsMyQuant.Contracts.Enums;

/// <summary>
/// Order type used by <see cref="Contracts.Models.Signal"/> and consumed by
/// <c>OkxExecutionService</c> to choose between post-only limit (maker) or
/// IOC market (taker) execution.
/// </summary>
public enum OrderType
{
    /// <summary>Limit order at the signal price (legacy / maker-preferred behaviour).</summary>
    Limit = 0,

    /// <summary>Immediate-or-cancel market order — crosses the book, pays taker fee.
    /// Use when fill urgency outweighs spread cost (e.g. circuit-breaker exits,
    /// pairs-leg compensation, stop-loss hits).</summary>
    Market = 1
}
