using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Tiered venue fee provider. Resolves taker fee (percent per side) in priority order:
///   1. Per-symbol override     : "Execution:Fees:{SYMBOL}:TakerPercent"
///   2. Per-venue tier schedule : "Execution:Fees:Venue:{VENUE}:Tiers" (array of { MonthlyVolumeUsd, TakerPercent })
///   3. Per-venue default       : "Execution:Fees:Venue:{VENUE}:DefaultTakerPercent"
///   4. Global default          : "Execution:Fees:DefaultTakerPercent"
///   5. Hardcoded floor         : 0.04% (Binance/OKX futures taker baseline)
///
/// Built-in schedules for Binance and OKX futures are used when config is absent, so this
/// works out of the box without API credentials. Monthly notional volume is tracked via
/// <see cref="RecordFill"/> and used to select the matching tier automatically.
///
/// Swap with a real venue-credential provider when ready — this class implements the
/// canonical fallback so the execution-cost cache is never starved.
/// </summary>
public sealed class ConfigDrivenVenueFeeProvider : IVenueTradeFeeProvider
{
    private readonly IConfiguration? _configuration;
    private readonly ILogger<ConfigDrivenVenueFeeProvider>? _logger;
    private readonly string _defaultVenue;

    // Volume accumulator per venue for tier resolution (rolling 30d would be ideal; we
    // use a calendar-month reset for simplicity — matches exchange billing cycles anyway).
    private readonly ConcurrentDictionary<string, VenueVolume> _monthlyVolume = new(StringComparer.OrdinalIgnoreCase);

    // Default tier schedules — published Binance/OKX USDⓈ-M futures taker rates (approximate).
    // Used when config is absent. Units: (minMonthlyVolumeUsd, takerPercentPerSide).
    private static readonly (decimal MinVolumeUsd, decimal TakerPercent)[] BinanceFuturesTiers =
    [
        (0m,                0.050m),
        (15_000_000m,       0.040m),
        (50_000_000m,       0.035m),
        (250_000_000m,      0.032m),
        (1_000_000_000m,    0.030m),
        (5_000_000_000m,    0.027m),
        (12_500_000_000m,   0.024m),
        (25_000_000_000m,   0.021m),
        (125_000_000_000m,  0.017m),
    ];

    private static readonly (decimal MinVolumeUsd, decimal TakerPercent)[] OkxFuturesTiers =
    [
        (0m,                0.050m),
        (5_000_000m,        0.045m),
        (10_000_000m,       0.040m),
        (20_000_000m,       0.035m),
        (100_000_000m,      0.030m),
        (300_000_000m,      0.028m),
        (1_000_000_000m,    0.025m),
    ];

    public ConfigDrivenVenueFeeProvider(
        IConfiguration? configuration = null,
        ILogger<ConfigDrivenVenueFeeProvider>? logger = null)
    {
        _configuration = configuration;
        _logger = logger;
        _defaultVenue = (configuration?["Execution:Fees:DefaultVenue"] ?? "Binance").Trim();
    }

    /// <summary>Accumulate a fill's notional for tier bookkeeping. Thread-safe.</summary>
    public void RecordFill(decimal notionalUsd, string? venue = null)
    {
        var key = (venue ?? _defaultVenue).Trim();
        _monthlyVolume.AddOrUpdate(
            key,
            _ => new VenueVolume { MonthKey = CurrentMonthKey(), VolumeUsd = notionalUsd },
            (_, existing) =>
            {
                var currentMonth = CurrentMonthKey();
                if (existing.MonthKey != currentMonth)
                    return new VenueVolume { MonthKey = currentMonth, VolumeUsd = notionalUsd };
                return new VenueVolume { MonthKey = existing.MonthKey, VolumeUsd = existing.VolumeUsd + notionalUsd };
            });
    }

    public Task<decimal?> GetTakerCommissionPercentPerSideAsync(string symbol, CancellationToken cancellationToken = default)
    {
        decimal? fee = ResolveSymbolOverride(symbol) ?? ResolveTieredRate() ?? ResolveVenueDefault() ?? ResolveGlobalDefault();
        fee ??= 0.04m;
        _logger?.LogDebug("Fee resolved: {Sym} → {Fee:F4}% (venue={Venue})", symbol, fee, _defaultVenue);
        return Task.FromResult<decimal?>(fee);
    }

    private decimal? ResolveSymbolOverride(string symbol)
    {
        if (_configuration is null) return null;
        var raw = _configuration[$"Execution:Fees:{symbol}:TakerPercent"];
        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private decimal? ResolveTieredRate()
    {
        var currentVolume = _monthlyVolume.TryGetValue(_defaultVenue, out var v) && v.MonthKey == CurrentMonthKey()
            ? v.VolumeUsd
            : 0m;

        var configTiers = LoadConfigTiers(_defaultVenue);
        var tiers = configTiers ?? DefaultTiersFor(_defaultVenue);
        if (tiers is null || tiers.Length == 0) return null;

        decimal selected = tiers[0].TakerPercent;
        foreach (var tier in tiers)
        {
            if (currentVolume >= tier.MinVolumeUsd) selected = tier.TakerPercent;
            else break;
        }
        return selected;
    }

    private (decimal MinVolumeUsd, decimal TakerPercent)[]? LoadConfigTiers(string venue)
    {
        if (_configuration is null) return null;
        var section = _configuration.GetSection($"Execution:Fees:Venue:{venue}:Tiers");
        if (!section.Exists()) return null;

        var list = new List<(decimal, decimal)>();
        foreach (var child in section.GetChildren())
        {
            var minRaw = child["MonthlyVolumeUsd"];
            var feeRaw = child["TakerPercent"];
            if (decimal.TryParse(minRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var min)
                && decimal.TryParse(feeRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var fee))
            {
                list.Add((min, fee));
            }
        }
        if (list.Count == 0) return null;
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return list.ToArray();
    }

    private static (decimal MinVolumeUsd, decimal TakerPercent)[]? DefaultTiersFor(string venue) =>
        venue.ToUpperInvariant() switch
        {
            "BINANCE" => BinanceFuturesTiers,
            "OKX" => OkxFuturesTiers,
            _ => null
        };

    private decimal? ResolveVenueDefault()
    {
        var raw = _configuration?[$"Execution:Fees:Venue:{_defaultVenue}:DefaultTakerPercent"];
        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private decimal? ResolveGlobalDefault()
    {
        var raw = _configuration?["Execution:Fees:DefaultTakerPercent"];
        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string CurrentMonthKey()
    {
        var now = DateTime.UtcNow;
        return $"{now.Year:D4}-{now.Month:D2}";
    }

    private readonly record struct VenueVolume
    {
        public string MonthKey { get; init; }
        public decimal VolumeUsd { get; init; }
    }
}
