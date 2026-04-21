namespace ThatsMyQuant.Contracts.Models;

public enum VenueKind
{
    Binance = 1,
    Okx = 2
}

public readonly record struct BalanceKey(VenueKind Venue, string Asset);

public readonly record struct PositionKey(VenueKind Venue, string Symbol);

public sealed record VenueBalanceState(
    decimal Total,
    decimal Reserved,
    decimal Available,
    DateTime UpdatedAtUtc);

public sealed record VenuePositionState(
    string Symbol,
    decimal Quantity,
    DateTime UpdatedAtUtc);

public sealed record VenuePortfolioSnapshot(
    VenueKind Venue,
    IReadOnlyDictionary<string, VenueBalanceState> Balances,
    IReadOnlyDictionary<string, VenuePositionState> Positions,
    DateTime SnapshotAtUtc);

public sealed record GlobalPortfolioSnapshot(
    IReadOnlyDictionary<BalanceKey, VenueBalanceState> Balances,
    IReadOnlyDictionary<PositionKey, VenuePositionState> Positions,
    DateTime SnapshotAtUtc)
{
    public decimal TotalUsdtValue => Balances
        .Where(b => b.Key.Asset == "USDT")
        .Sum(b => b.Value.Total);
}
