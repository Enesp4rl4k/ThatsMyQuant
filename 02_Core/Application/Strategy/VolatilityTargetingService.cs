using Microsoft.Extensions.Options;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Computes a multiplier to scale position size toward a target annualized volatility.
/// Multiplier = targetAnnualVol / realizedAnnualVol.  Clamped to [floor, cap].
///
/// Realized vol: sample std-dev of simple log returns over lookback, annualized by
/// sqrt(barsPerYear) where barsPerYear is derived from the candle timeframe.
/// </summary>
public sealed class VolatilityTargetingService
{
    private readonly RiskGovernorOptions _options;

    public VolatilityTargetingService(IOptions<RiskGovernorOptions>? options = null)
    {
        _options = options?.Value ?? new RiskGovernorOptions();
    }

    public bool IsEnabled => _options.VolatilityTargetingEnabled;

    public decimal ComputeMultiplier(IReadOnlyList<Candle> candles, string timeframe)
    {
        if (!_options.VolatilityTargetingEnabled) return 1m;
        if (candles is null || candles.Count < 2) return 1m;

        var lookback = Math.Max(5, _options.VolatilityLookbackBars);
        if (candles.Count < lookback + 1) return 1m;

        var start = candles.Count - lookback - 1;
        var returns = new double[lookback];
        for (int i = 0; i < lookback; i++)
        {
            var prev = (double)candles[start + i].Close;
            var curr = (double)candles[start + i + 1].Close;
            if (prev <= 0 || curr <= 0) return 1m;
            returns[i] = Math.Log(curr / prev);
        }

        var mean = 0.0;
        for (int i = 0; i < lookback; i++) mean += returns[i];
        mean /= lookback;

        var variance = 0.0;
        for (int i = 0; i < lookback; i++)
        {
            var d = returns[i] - mean;
            variance += d * d;
        }
        variance /= Math.Max(1, lookback - 1);
        var stdDev = Math.Sqrt(variance);
        if (stdDev <= 0) return 1m;

        var barsPerYear = BarsPerYear(timeframe);
        var realizedAnnualVol = stdDev * Math.Sqrt(barsPerYear);
        if (realizedAnnualVol <= 0) return 1m;

        var targetAnnual = (double)_options.VolatilityTargetAnnualPercent / 100.0;
        var multiplier = (decimal)(targetAnnual / realizedAnnualVol);
        return Math.Clamp(multiplier, _options.VolatilityMultiplierFloor, _options.VolatilityMultiplierCap);
    }

    private static int BarsPerYear(string timeframe)
    {
        return (timeframe ?? "1h").ToLowerInvariant() switch
        {
            "1m" => 525_600,
            "5m" => 105_120,
            "15m" => 35_040,
            "30m" => 17_520,
            "1h" => 8_760,
            "2h" => 4_380,
            "4h" => 2_190,
            "6h" => 1_460,
            "12h" => 730,
            "1d" => 365,
            _ => 8_760,
        };
    }
}
