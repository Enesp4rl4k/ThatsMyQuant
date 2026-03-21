namespace ThatsMyQuant.Domain.ValueObjects;

public record Money(decimal Amount, string Currency);

public record RiskParameters(
    decimal MaxDailyLossPercent,
    decimal MaxPositionSizePercent,
    decimal StopLossPercent,
    decimal TakeProfitPercent,
    decimal StartingEquity = 100_000m
)
{
    /// <summary>
    /// Conservative defaults for BIST trading.
    /// - Max 2% daily loss
    /// - Max 5% of portfolio per position
    /// - 2% stop-loss
    /// - 6% take-profit (3:1 reward/risk)
    /// </summary>
    public static RiskParameters Default => new(
        MaxDailyLossPercent: 2.0m,
        MaxPositionSizePercent: 5.0m,
        StopLossPercent: 2.0m,
        TakeProfitPercent: 6.0m,
        StartingEquity: 100_000m
    );

    /// <summary>
    /// Aggressive settings for high-conviction trades.
    /// </summary>
    public static RiskParameters Aggressive => new(
        MaxDailyLossPercent: 5.0m,
        MaxPositionSizePercent: 10.0m,
        StopLossPercent: 3.0m,
        TakeProfitPercent: 9.0m,
        StartingEquity: 100_000m
    );
}
