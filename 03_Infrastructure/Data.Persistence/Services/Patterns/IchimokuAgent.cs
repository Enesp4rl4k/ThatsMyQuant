using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Services.Patterns;

public sealed class IchimokuAgent : IPatternAgent
{
    private readonly ILogger<IchimokuAgent> _logger;
    private const int TenkanPeriod = 9;
    private const int KijunPeriod = 26;
    private const int SenkouSpanBPeriod = 52;

    public string AgentId => "Pattern.Ichimoku";

    public IchimokuAgent(ILogger<IchimokuAgent> logger)
        => _logger = logger;

    public Task<PatternSnapshot> AnalyzeAsync(
        Signal signal,
        FoundationSnapshot foundation,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default)
    {
        if (candles.Count < SenkouSpanBPeriod + 26)
            return Task.FromResult(PatternSnapshot.Empty);

        var tenkan = Midpoint(candles, TenkanPeriod);
        var kijun = Midpoint(candles, KijunPeriod);
        var senkouA = (tenkan + kijun) / 2m;
        var senkouB = Midpoint(candles, SenkouSpanBPeriod);

        var cloudTop = Math.Max(senkouA, senkouB);
        var cloudBottom = Math.Min(senkouA, senkouB);

        IchimokuState state;
        if (signal.Price > cloudTop)
            state = IchimokuState.AboveCloud;
        else if (signal.Price < cloudBottom)
            state = IchimokuState.BelowCloud;
        else
            state = IchimokuState.InCloud;

        if (tenkan > kijun && candles.Count >= 2)
        {
            var prevTenkan = Midpoint(candles, TenkanPeriod, 1);
            var prevKijun = Midpoint(candles, KijunPeriod, 1);
            if (prevTenkan <= prevKijun)
                state = IchimokuState.TKCrossBullish;
        }
        else if (tenkan < kijun && candles.Count >= 2)
        {
            var prevTenkan = Midpoint(candles, TenkanPeriod, 1);
            var prevKijun = Midpoint(candles, KijunPeriod, 1);
            if (prevTenkan >= prevKijun)
                state = IchimokuState.TKCrossBearish;
        }

        var tags = new List<string> { state.ToString() };

        return Task.FromResult(new PatternSnapshot
        {
            IchimokuState = state,
            PatternTags = tags
        });
    }

    private static decimal Midpoint(IReadOnlyList<Candle> candles, int period, int offset = 0)
    {
        var slice = candles.SkipLast(offset).TakeLast(period).ToList();
        if (slice.Count == 0) return 0m;
        return (slice.Max(c => c.High) + slice.Min(c => c.Low)) / 2m;
    }
}
