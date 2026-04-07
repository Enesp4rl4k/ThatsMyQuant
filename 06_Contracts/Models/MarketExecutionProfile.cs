namespace ThatsMyQuant.Contracts.Models;

public sealed record MarketExecutionProfile(
    string Market,
    string Venue,
    decimal SlippagePercent,
    decimal SpreadPercent,
    decimal CommissionPercentPerSide,
    decimal MinHftVolume24h,
    long PreferredMaxLatencyMs);
