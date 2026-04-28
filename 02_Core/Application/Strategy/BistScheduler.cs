namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Market hours scheduler — handles BIST, Crypto (24/7), and Forex (24/5 approx).
/// </summary>
public static class MarketHoursScheduler
{
    private static readonly TimeZoneInfo TurkeyTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time");

    private static readonly AsyncLocal<bool?> _overrideCanTrade = new();

    public static bool? OverrideCanTrade
    {
        get => _overrideCanTrade.Value;
        set => _overrideCanTrade.Value = value;
    }

    public static MarketSessionInfo GetCurrentSession(string market = "BIST")
    {
        var ovr = _overrideCanTrade.Value;
        if (ovr.HasValue)
            return new MarketSessionInfo { Session = "Test", IsMarketOpen = true,
                CanTrade = ovr.Value, Description = "Test override", NextSessionStart = DateTime.UtcNow.AddHours(1) };

        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TurkeyTimeZone);

        if (market.ToUpper() == "CRYPTO")
        {
            return new MarketSessionInfo
            {
                Session = "Continuous", IsMarketOpen = true, CanTrade = true,
                Description = "Crypto 24/7 — ✅ Always active.", NextSessionStart = now.AddDays(7)
            };
        }

        if (market.ToUpper() == "FOREX")
        {
            // Forex is generally closed from Fri 22:00 UTC to Sun 22:00 UTC
            bool isWeekend = now.DayOfWeek == DayOfWeek.Saturday || 
                            (now.DayOfWeek == DayOfWeek.Sunday && now.Hour < 22) ||
                            (now.DayOfWeek == DayOfWeek.Friday && now.Hour > 22);

            return new MarketSessionInfo
            {
                Session = isWeekend ? "Closed" : "Continuous",
                IsMarketOpen = !isWeekend,
                CanTrade = !isWeekend,
                Description = isWeekend ? "Forex Weekend — closed." : "Forex Weekday — ✅ Active.",
                NextSessionStart = isWeekend ? now.AddDays(1).Date.AddHours(22) : now.AddHours(1)
            };
        }

        return GetBistSessionAt(now);
    }

    private static MarketSessionInfo GetBistSessionAt(DateTime turkeyTime)
    {
        // Weekend
        if (turkeyTime.DayOfWeek == DayOfWeek.Saturday || turkeyTime.DayOfWeek == DayOfWeek.Sunday)
        {
            var nextMonday = turkeyTime.AddDays(turkeyTime.DayOfWeek == DayOfWeek.Saturday ? 2 : 1);
            return new MarketSessionInfo
            {
                Session = "Closed", IsMarketOpen = false, CanTrade = false,
                Description = "Weekend — BIST closed",
                NextSessionStart = new DateTime(nextMonday.Year, nextMonday.Month, nextMonday.Day, 10, 0, 0)
            };
        }

        var time = turkeyTime.TimeOfDay;

        if (time < new TimeSpan(9, 40, 0)) return Session("PreMarket", false, false, "Before BIST pre-market.", turkeyTime.Date.Add(new TimeSpan(9, 40, 0)));
        if (time < new TimeSpan(10, 0, 0)) return Session("PreMarket", true, false, "BIST Pre-market auction.", turkeyTime.Date.Add(new TimeSpan(10, 0, 0)));
        if (time < new TimeSpan(13, 0, 0)) return Session("Continuous", true, true, "BIST ✅ Trading active.", turkeyTime.Date.Add(new TimeSpan(13, 0, 0)));
        if (time < new TimeSpan(14, 0, 0)) return Session("LunchBreak", false, false, "BIST Lunch break.", turkeyTime.Date.Add(new TimeSpan(14, 0, 0)));
        if (time < new TimeSpan(18, 0, 0)) return Session("Continuous", true, true, "BIST ✅ Trading active.", turkeyTime.Date.Add(new TimeSpan(18, 0, 0)));
        
        return Session("Closed", false, false, "BIST market closed.", turkeyTime.Date.AddDays(1).Add(new TimeSpan(10, 0, 0)));
    }

    private static MarketSessionInfo Session(string session, bool isOpen, bool canTrade, string desc, DateTime next) =>
        new() { Session = session, IsMarketOpen = isOpen, CanTrade = canTrade, Description = desc, NextSessionStart = next };
}

public record MarketSessionInfo
{
    public string Session { get; init; } = "";
    public bool IsMarketOpen { get; init; }
    public bool CanTrade { get; init; }
    public string Description { get; init; } = "";
    public DateTime NextSessionStart { get; init; }
}
