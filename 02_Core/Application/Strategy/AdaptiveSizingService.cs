using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Strategy;

public static class AdaptiveSizingService
{
    public static decimal Apply(decimal baseSizePercent, int confidence, string market, DateTime timestampUtc, TradingCoreOptions? options = null)
    {
        var multiplier = ConfidenceMultiplier(confidence, options) * SessionMultiplier(market, timestampUtc, options);
        return Math.Max(0m, baseSizePercent * multiplier);
    }

    private static decimal ConfidenceMultiplier(int confidence, TradingCoreOptions? options = null)
    {
        if (confidence >= (options?.ConfidenceHighThreshold ?? 85)) return options?.ConfidenceHighMultiplier ?? 1.00m;
        if (confidence >= (options?.ConfidenceMediumThreshold ?? 75)) return options?.ConfidenceMediumMultiplier ?? 0.85m;
        if (confidence >= (options?.ConfidenceLowThreshold ?? 65)) return options?.ConfidenceLowMultiplier ?? 0.65m;
        return options?.ConfidenceFloorMultiplier ?? 0.45m;
    }

    private static decimal SessionMultiplier(string market, DateTime timestampUtc, TradingCoreOptions? options = null)
    {
        if (string.Equals(market, "BIST", StringComparison.OrdinalIgnoreCase))
        {
            return options?.SessionBistMultiplier ?? 0.90m;
        }

        var turkeyNow = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(timestampUtc, "Turkey Standard Time");
        var hour = turkeyNow.Hour;

        if (hour is >= 10 and < 13) return options?.SessionLondonMultiplier ?? 1.00m; // London killzone
        if (hour is >= 15 and < 18) return options?.SessionNyMultiplier ?? 0.95m; // NY killzone
        if (hour is >= 3 and < 10) return options?.SessionAsiaMultiplier ?? 0.70m;  // Asia / handoff
        return options?.SessionDeadZoneMultiplier ?? 0.55m;                          // dead zone
    }
}
