namespace ThatsMyQuant.Contracts.Models;

/// <summary>
/// A snapshot of all computed technical indicators at a specific point in time.
/// These values are computed from historical OHLCV candle data.
/// </summary>
public record IndicatorSnapshot
{
    // --- Momentum ---
    /// <summary>RSI(14) — 0-100. Below 30 = oversold, above 70 = overbought.</summary>
    public double? RSI { get; init; }

    /// <summary>Rate of Change(10) — percent change vs 10 bars ago. Positive = bullish momentum.</summary>
    public double? ROC { get; init; }

    /// <summary>Stochastic RSI %K (0-100). Below 20 = oversold, above 80 = overbought.</summary>
    public double? StochRsiK { get; init; }

    /// <summary>Stochastic RSI %D (signal line, 0-100). K crossing above D = bullish momentum pivot.</summary>
    public double? StochRsiD { get; init; }

    // --- Trend Strength (ADX) ---
    /// <summary>Average Directional Index (14). Above 25 = strong trend, below 20 = weak / ranging.</summary>
    public double? ADX { get; init; }

    /// <summary>DI+ (14) — positive directional movement. DI+ > DI- = uptrend.</summary>
    public double? DIPlus { get; init; }

    /// <summary>DI- (14) — negative directional movement. DI- > DI+ = downtrend.</summary>
    public double? DIMinus { get; init; }

    // --- Trend (MACD) ---
    /// <summary>MACD(12,26,9) main line.</summary>
    public double? MACD { get; init; }
    /// <summary>MACD signal line.</summary>
    public double? MACDSignal { get; init; }
    /// <summary>MACD histogram (MACD - Signal). Positive = bullish momentum.</summary>
    public double? MACDHistogram { get; init; }

    // --- Moving Averages ---
    /// <summary>Exponential Moving Average (20 periods) — short-term trend.</summary>
    public double? EMA20 { get; init; }
    /// <summary>Exponential Moving Average (50 periods) — medium-term trend.</summary>
    public double? EMA50 { get; init; }

    // --- Volatility (Bollinger Bands) ---
    /// <summary>Bollinger upper band (SMA20 + 2σ).</summary>
    public double? BollingerUpper { get; init; }
    /// <summary>Bollinger middle band (SMA20).</summary>
    public double? BollingerMiddle { get; init; }
    /// <summary>Bollinger lower band (SMA20 - 2σ).</summary>
    public double? BollingerLower { get; init; }

    // --- Volatility (ATR) ---
    /// <summary>Average True Range (14) — used for dynamic stop-loss/take-profit.</summary>
    public double? ATR { get; init; }

    // --- Volume ---
    /// <summary>Current volume divided by 20-period average volume. Above 1.5 = high volume confirmation.</summary>
    public double? VolumeRatio { get; init; }

    // --- AlphaTrend (KivancOzbilgic) ---
    /// <summary>AlphaTrend indicator result — ATR-based adaptive trend with crossover signals.</summary>
    public AlphaTrendResult? AlphaTrend { get; init; }

    /// <summary>PMax indicator result - moving-average/ATR trend filter.</summary>
    public PMaxResult? PMax { get; init; }

    /// <summary>Rolling linear-regression edge context for slope, fit quality, and sigma-band stretch.</summary>
    public LinearRegressionEdgeSnapshot? LinearRegression { get; init; }

    // --- Absorbing System (Support/Resistance) ---
    /// <summary>Detected support and resistance levels from pivot + wick absorption analysis.</summary>
    public List<SupportResistanceLevel>? SupportResistanceLevels { get; init; }

    /// <summary>Nearest unbroken support level below current price.</summary>
    public SupportResistanceLevel? NearestSupport { get; init; }

    /// <summary>Nearest unbroken resistance level above current price.</summary>
    public SupportResistanceLevel? NearestResistance { get; init; }

    /// <summary>The open price of the bar when indicators were computed (used for bar-direction volume signal).</summary>
    public decimal BarOpen { get; init; }

    /// <summary>The price at the time indicators were computed.</summary>
    public decimal CurrentPrice { get; init; }

    /// <summary>The algorithmic candlestick pattern detected (e.g., Bullish Engulfing).</summary>
    public string DetectedPattern { get; init; } = "None";

    /// <summary>Timestamp when indicators were computed.</summary>
    public DateTime ComputedAt { get; init; } = DateTime.UtcNow;
}
