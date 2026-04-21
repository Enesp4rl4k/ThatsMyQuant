namespace ThatsMyQuant.Contracts.Interfaces;

/// <summary>
/// Exchange-reported taker fee schedule (percent per side, same scale as MarketExecutionProfile.CommissionPercentPerSide).
/// </summary>
public interface IVenueTradeFeeProvider
{
    /// <summary>
    /// Returns taker commission as percent per side (e.g. 0.04 = 0.04%), or null if unavailable.
    /// </summary>
    Task<decimal?> GetTakerCommissionPercentPerSideAsync(string symbol, CancellationToken cancellationToken = default);
}
