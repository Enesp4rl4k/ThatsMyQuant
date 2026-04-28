namespace ThatsMyQuant.Contracts.Options;

/// <summary>
/// Feature flags that gate which asset classes the engine is allowed to trade
/// (section: <c>Markets</c>). Crypto is the only production-ready venue today;
/// BIST (Turkish stocks) and Forex code paths exist as scaffolding for a future
/// expansion, so they default OFF. When a flag is OFF:
///   • <c>DynamicSymbolUniverseService</c> strips symbols of that market type.
///   • <c>PaperTradingService</c> refuses new entries outside enabled markets.
///   • <c>MarketHoursScheduler</c>-backed session gates are short-circuited.
/// Flip to true once the real gateway (not the simulated stub) is wired.
/// </summary>
public sealed class MarketUniverseOptions
{
    /// <summary>Crypto (OKX perpetuals + spot). Primary production venue.</summary>
    public bool EnableCrypto { get; set; } = true;

    /// <summary>Turkish stocks. Currently backed by a simulated gateway only —
    /// keep disabled in production until a real BIST gateway is integrated.</summary>
    public bool EnableBist { get; set; } = false;

    /// <summary>Forex majors + TRY pairs. Currently scaffold only — disabled
    /// until a real broker integration replaces the simulator.</summary>
    public bool EnableForex { get; set; } = false;

    public bool IsEnabled(string market)
    {
        if (string.IsNullOrWhiteSpace(market)) return false;
        return market.Trim().ToUpperInvariant() switch
        {
            "CRYPTO" => EnableCrypto,
            "BIST" => EnableBist,
            "FOREX" => EnableForex,
            _ => false
        };
    }
}
