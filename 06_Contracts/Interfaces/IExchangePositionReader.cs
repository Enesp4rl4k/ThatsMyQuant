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

    /// <summary>
    /// [2026-05-16] Returns CLOSED positions (one record per closed position lifecycle,
    /// not per-fill) within the window. Used by DbToPortfolioSync to mirror live-mode
    /// closed positions into PaperPortfolio.ClosedPositions so digest / TCA / ShadowAudit
    /// see realtime activity instead of just stale paper-mode history.
    /// OKX-source: /api/v5/account/positions-history (3-month max).
    /// Implementations that don't support position history should return empty.
    /// </summary>
    Task<IReadOnlyList<ClosedPositionSnapshot>> GetClosedPositionsHistoryAsync(
        DateTime sinceUtc,
        DateTime untilUtc,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ClosedPositionSnapshot>>(Array.Empty<ClosedPositionSnapshot>());
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
    DateTime FillTimeUtc,
    string? ExecType = null,    // 2026-05-07 (Sprint 1.2): "T"=taker, "M"=maker per OKX. Null on legacy paths / venues that don't surface it. Consumed by MakerFillRatioMonitorHostedService.
    string? SubType = null,     // 2026-05-07 (Sprint 1.2 live-fill validation): OKX SWAP subType. In Hedge Mode: "3"=open long, "4"=open short, "5"=close long, "6"=close short (clean entry/exit). In Net Mode (our config): subType only encodes "1"=buy / "2"=sell — entry/exit must be derived from RealizedPnl instead. Kept here for completeness so a future Hedge Mode flip works without re-plumbing.
    decimal RealizedPnl = 0m);  // 2026-05-07 (Sprint 1.2 live-fill validation): OKX `fillPnl` — realized PnL booked by this fill. 0 on entry fills (no close yet), non-zero on exit fills (close booked PnL). The Net Mode entry/exit oracle: |RealizedPnl| > 0 ⇒ exit fill (closing a position), == 0 ⇒ entry fill. Used by MakerFillRatioMonitor to compute the entry-only maker ratio that Premortem #4 actually measures, since Net Mode subType is direction-only (1/2) and ordType is not in the fills-history response at all.
