namespace ThatsMyQuant.Domain.Entities;

/// <summary>
/// OHLCV candlestick data — the fundamental unit of price action.
/// </summary>
public class Candle
{
    public DateTime Timestamp { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }

    /// <summary>
    /// True body size (absolute difference between open and close).
    /// </summary>
    public decimal BodySize => Math.Abs(Close - Open);

    /// <summary>
    /// True if this candle closed higher than it opened.
    /// </summary>
    public bool IsBullish => Close > Open;
}
