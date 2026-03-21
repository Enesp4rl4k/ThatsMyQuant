namespace ThatsMyQuant.Domain.Enums;

/// <summary>
/// Trading timeframes for multi-timeframe analysis.
/// </summary>
public enum TimeFrame
{
    M15,    // 15 minutes
    H1,     // 1 hour
    H4,     // 4 hours
    Daily,  // 1 day
    Weekly  // 1 week
}

/// <summary>
/// Detected market regime — determines how indicators should be tuned.
/// </summary>
public enum MarketRegime
{
    /// <summary>Price making consistent higher highs and higher lows. Use trend-following indicators.</summary>
    Trending,

    /// <summary>Price oscillating between support and resistance. Use mean-reversion indicators.</summary>
    Ranging,

    /// <summary>High ATR, wide Bollinger Bands, fast moves. Reduce position size, widen stops.</summary>
    Volatile,

    /// <summary>Not enough data to determine regime.</summary>
    Unknown
}
