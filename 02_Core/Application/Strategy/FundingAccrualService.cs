using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Application.Strategy;

public static class FundingAccrualService
{
    /// <summary>
    /// Legacy flat-rate accrual (bps/8h held constant across the hold). Kept for backwards
    /// compatibility where no funding history is available. New code should prefer the
    /// provider-based overload which uses real OKX 8h funding stamps.
    /// Positive return means funding received, negative means paid.
    /// </summary>
    public static decimal Calculate(
        decimal notional,
        TradeSide side,
        DateTime entryUtc,
        DateTime exitUtc,
        decimal fundingRateBpsPer8h)
    {
        if (notional <= 0m || fundingRateBpsPer8h == 0m || exitUtc <= entryUtc)
            return 0m;

        var periods = (decimal)(exitUtc - entryUtc).TotalHours / 8m;
        if (periods <= 0m)
            return 0m;

        var rate = fundingRateBpsPer8h / 10_000m;
        var cashflow = notional * rate * periods;

        // Market convention with positive funding: longs pay, shorts receive.
        return side == TradeSide.Buy ? -cashflow : cashflow;
    }

    /// <summary>
    /// Provider-based accrual: sums cashflows at each real OKX funding stamp (00/08/16 UTC)
    /// between entry and exit, using the historical rate effective at that stamp. This is the
    /// physically-correct calculation for perpetual swap backtests. If the provider returns
    /// zero for all stamps (e.g. NullFundingRateProvider), the cashflow is zero — no silent
    /// fallback to flat bps, since that would double-count in callers that already applied
    /// the flat formula at another layer.
    /// </summary>
    public static decimal CalculateFromProvider(
        string symbol,
        decimal notional,
        TradeSide side,
        DateTime entryUtc,
        DateTime exitUtc,
        IFundingRateProvider provider)
    {
        if (notional <= 0m || exitUtc <= entryUtc || provider is null) return 0m;

        // Align the first funding stamp at or after entryUtc. OKX funds at 00:00, 08:00, 16:00 UTC.
        var hours = entryUtc.Hour;
        int nextStampHour = hours < 8 ? 8 : hours < 16 ? 16 : 24;
        var stamp = new DateTime(entryUtc.Year, entryUtc.Month, entryUtc.Day, 0, 0, 0, DateTimeKind.Utc)
                    .AddHours(nextStampHour);

        decimal cashflow = 0m;
        int guard = 0;
        while (stamp <= exitUtc && guard++ < 5_000)
        {
            var rate = provider.GetRate(symbol, stamp);
            if (rate != 0m)
                cashflow += notional * rate;
            stamp = stamp.AddHours(8);
        }

        return side == TradeSide.Buy ? -cashflow : cashflow;
    }
}
