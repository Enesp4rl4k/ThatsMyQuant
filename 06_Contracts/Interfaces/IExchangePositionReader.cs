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
    /// Returns the most recent fill for a symbol within the last <paramref name="lookbackMinutes"/>
    /// minutes (a closing fill of a bracket TP/SL trigger, for example), or <c>null</c> if no
    /// fill is found in that window. Default 5 minutes preserves the original tight-window
    /// semantics for fast ghost-recovery; ghost-cleanup paths invoked after a worker restart
    /// (where the bracket may have fired hours ago) should pass a wider window so the real
    /// fee + fill price are not lost to a too-narrow cutoff.
    /// </summary>
    Task<ExchangeFillSnapshot?> GetLastFillAsync(string symbol, int lookbackMinutes = 5, CancellationToken cancellationToken = default)
        => Task.FromResult<ExchangeFillSnapshot?>(null);

    /// <summary>
    /// Returns historical fills (paged, up to 3 months back per OKX) for fee-harvest /
    /// post-mortem analysis. Implementations that don't support history should return empty.
    /// </summary>
    Task<IReadOnlyList<ExchangeFillSnapshot>> GetFillsHistoryAsync(
        DateTime sinceUtc,
        DateTime untilUtc,
        string? instType = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ExchangeFillSnapshot>>(Array.Empty<ExchangeFillSnapshot>());
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
