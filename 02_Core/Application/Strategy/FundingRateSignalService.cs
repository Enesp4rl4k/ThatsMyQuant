using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Funding-rate directional filter. On OKX perps, funding is paid every 8h between long
/// and short holders. Persistent premia (longs paying shorts) typically precede local tops;
/// persistent discounts typically precede local bottoms. Rule:
///
///   Reject longs  when funding &gt;= +BlockLongThresholdBps / 8h (crowded longs).
///   Reject shorts when funding &lt;= -BlockShortThresholdBps / 8h (crowded shorts).
///
/// Defaults (1 bp = 0.0001): block at ±1 bp/8h, i.e. ~±0.1% annualized funding.
/// Conservative — real extreme-funding setups show ±5 bps+ but we want the filter to fire
/// often enough to matter without being a binary coin-flip.
/// </summary>
public sealed class FundingRateSignalService
{
    private readonly IFundingRateProvider _provider;
    public decimal BlockLongThreshold { get; init; } = 0.0001m;  // +1 bp / 8h
    public decimal BlockShortThreshold { get; init; } = -0.0001m;

    public FundingRateSignalService(IFundingRateProvider provider)
    {
        _provider = provider ?? new NullFundingRateProvider();
    }

    public readonly record struct FundingGateResult(bool Pass, string? RejectReason, decimal Rate);

    public FundingGateResult Evaluate(string symbol, DateTime asOfUtc, TradeSide side)
    {
        decimal rate = _provider.GetRate(symbol, asOfUtc);
        if (rate == 0m) return new FundingGateResult(true, null, 0m); // no data, don't block

        if (side == TradeSide.Buy && rate >= BlockLongThreshold)
            return new FundingGateResult(false, "FundingExtremeLong", rate);
        if (side == TradeSide.Sell && rate <= BlockShortThreshold)
            return new FundingGateResult(false, "FundingExtremeShort", rate);

        return new FundingGateResult(true, null, rate);
    }
}
