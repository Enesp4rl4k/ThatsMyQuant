using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Interfaces;

/// <summary>
/// Optional capability implemented by exchange services that can enumerate the
/// user's currently open positions from the venue REST API. Used by
/// <c>ReconciliationHostedService</c> for position-level drift checks.
/// </summary>
public interface IExchangePositionReader
{
    Task<IReadOnlyList<ExchangePositionSnapshot>> GetOpenPositionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the most recent fill for a symbol (the closing fill of a bracket TP/SL trigger
    /// for example), or <c>null</c> if no fill found in the last few minutes.
    /// Used by ghost-cleanup to recover the exit price + side so a CLOSED notification can
    /// be published with real PnL data.
    /// </summary>
    Task<ExchangeFillSnapshot?> GetLastFillAsync(string symbol, CancellationToken cancellationToken = default)
        => Task.FromResult<ExchangeFillSnapshot?>(null);
}

/// <summary>
/// Lightweight fill record returned by venues for ghost-close PnL reconstruction.
/// Side: "buy"/"sell". FillSide is what was executed (a closing-long fill is "sell"
/// because we sold to flatten). Quantity is in BASE-asset units (already de-contracted
/// at the venue adapter layer).
/// </summary>
public sealed record ExchangeFillSnapshot(
    string Symbol,
    string FillSide,
    decimal Quantity,
    decimal Price,
    decimal FeePaid,
    DateTime FillTimeUtc);
