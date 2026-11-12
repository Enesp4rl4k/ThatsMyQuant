namespace ThatsMyQuant.Contracts.Interfaces;

/// <summary>A resting protective stop this bot placed on the venue.</summary>
/// <param name="Symbol">Canonical symbol, e.g. BTCUSDT.</param>
/// <param name="ClosesLong">True when the stop sells (it protects a long position).</param>
public sealed record VenueStopOrder(string Symbol, string AlgoId, string AlgoClOrdId, decimal TriggerPx, bool ClosesLong, DateTime UpdatedUtc);

/// <summary>
/// Venue operations for exchange-side catastrophe stops and per-instrument leverage
/// (PREREG / RESULTS_stops_take_profit_2026_11_06). Stops close the whole position, trigger on mark
/// price and are reduce-only, so they protect the account while the bot is down.
/// </summary>
public interface ICatastropheStopVenue
{
    /// <summary>Sets the instrument's isolated leverage to the configured value if not already done.</summary>
    Task EnsureLeverageAsync(string symbol, CancellationToken cancellationToken = default);

    Task<decimal> GetTickSizeAsync(string symbol, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VenueStopOrder>> GetBotStopOrdersAsync(CancellationToken cancellationToken = default);

    /// <summary>Bot stops that fired (state effective) since <paramref name="sinceUtc"/>.</summary>
    Task<IReadOnlyList<VenueStopOrder>> GetTriggeredBotStopsAsync(DateTime sinceUtc, CancellationToken cancellationToken = default);

    Task<bool> PlaceCatastropheStopAsync(string symbol, bool positionIsLong, decimal triggerPx, CancellationToken cancellationToken = default);

    Task<bool> CancelStopAsync(string symbol, string algoId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds isolated margin to any position whose margin fell below notional / leverage, so the liquidation price stays
    /// beyond the catastrophe stop. Returns the number of positions topped up.
    /// </summary>
    Task<int> TopUpIsolatedMarginAsync(CancellationToken cancellationToken = default);
}
