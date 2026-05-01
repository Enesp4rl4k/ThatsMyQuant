namespace ThatsMyQuant.Contracts.Interfaces;

/// <summary>
/// Per-symbol execution cost snapshot used to override hardcoded BacktestCostModel defaults at
/// run-start. Values are in bps (1 bp = 0.01%).
///
/// Rationale: a single <c>SpreadBps = 3</c> default is a BTC-only number. Real OKX L1 spreads
/// vary ~1-3bps (BTC) → ~3-6bps (SOL) → ~5-10bps (DOGE) → wider for long-tail alts. Backtesting
/// every symbol with BTC spread makes alt results look ~2-5x rosier than reality.
/// This profile is fetched once per symbol at backtest start from OKX /api/v5/market/ticker
/// (current top-of-book) and used as the constant spread for that run. It's not per-bar-perfect
/// but is an order of magnitude better than flat.
/// </summary>
public readonly record struct SymbolCostProfile(
    string Symbol,
    decimal TakerBps,
    decimal MakerBps,
    decimal SpreadBps,
    DateTime CapturedAtUtc);

public interface ISymbolCostProfileProvider
{
    /// <summary>
    /// Fetch a cost profile for the given symbol. Returns null if the venue lookup fails and no
    /// fallback was configured — callers should then stick with BacktestCostModel defaults.
    /// </summary>
    Task<SymbolCostProfile?> GetAsync(string symbol, CancellationToken ct = default);

    /// <summary>
    /// Fetch a cost profile calibrated for a SPECIFIC historical timestamp. Allows backtests
    /// to use the spread that prevailed around <paramref name="atUtc"/> instead of the
    /// (legacy) "today's spread × all of history" approximation. Implementations that lack
    /// historical data fall back to <see cref="GetAsync"/> semantics.
    /// </summary>
    Task<SymbolCostProfile?> GetAsAtAsync(string symbol, DateTime atUtc, CancellationToken ct = default)
        => GetAsync(symbol, ct);
}
