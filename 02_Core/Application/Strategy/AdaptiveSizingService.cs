namespace ThatsMyQuant.Application.Strategy;

public static class AdaptiveSizingService
{
    public static decimal Apply(decimal baseSizePercent, int confidence, string market, DateTime timestampUtc)
    {
        var multiplier = ConfidenceMultiplier(confidence) * SessionMultiplier(market, timestampUtc);
        return Math.Max(0m, baseSizePercent * multiplier);
    }

    private static decimal ConfidenceMultiplier(int confidence)
    {
        if (confidence >= 85) return 1.00m;
        if (confidence >= 75) return 0.85m;
        if (confidence >= 65) return 0.65m;
        return 0.45m;
    }

    private static decimal SessionMultiplier(string market, DateTime timestampUtc)
    {
        if (string.Equals(market, "BIST", StringComparison.OrdinalIgnoreCase))
        {
            return 0.90m;
        }

        var turkeyNow = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(timestampUtc, "Turkey Standard Time");
        var hour = turkeyNow.Hour;

        if (hour is >= 10 and < 13) return 1.00m; // London killzone
        if (hour is >= 15 and < 18) return 0.95m; // NY killzone
        if (hour is >= 3 and < 10) return 0.70m;  // Asia / handoff
        return 0.55m;                              // dead zone
    }
}
