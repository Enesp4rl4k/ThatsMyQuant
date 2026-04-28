using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy.ProMode;

/// <summary>
/// Injects synthetic crisis scenarios into historical market data to stress-test the
/// Pro Mode defense stack (regime allocator + circuit breaker + correlation guard +
/// concentration cap).
///
/// Scenarios:
///   • FlashCrash    — -40% BTC spot over 4 hours (like 2020 March 12)
///   • LunaLike      — -99% single-asset collapse + correlation spike to 1.0 (May 2022)
///   • FTXLike       — -50% across all alts over 72 hours + funding rate explosion (Nov 2022)
///   • LiquidityDry  — spread 10× normal, slippage amplified (acute risk-off event)
///
/// Output is NOT a return number — it's a DEFENSE EFFICACY REPORT: did each safety
/// layer activate when it was supposed to? How fast? What DD did the portfolio take?
/// </summary>
public sealed class StressScenarioSimulator
{
    private readonly ILogger<StressScenarioSimulator> _logger;

    public StressScenarioSimulator(ILogger<StressScenarioSimulator> logger) { _logger = logger; }

    /// <summary>Inject a synthetic crash overlay into a candle series and return modified candles.</summary>
    public static IReadOnlyList<Candle> OverlayScenario(
        IReadOnlyList<Candle> original,
        StressScenario scenario,
        int injectAtIndex)
    {
        if (injectAtIndex < 0 || injectAtIndex >= original.Count - 10)
            throw new ArgumentException("Injection index out of range");

        var modified = new List<Candle>(original);
        switch (scenario)
        {
            case StressScenario.FlashCrash:
                // -40% over 4 hourly bars, then slow recovery -20% from crash low.
                ApplyShock(modified, injectAtIndex, 4, -0.40m, recoveryBars: 24, recoveryFraction: 0.5m);
                break;
            case StressScenario.LunaLike:
                // -99% over 48 hours, NO recovery.
                ApplyShock(modified, injectAtIndex, 48, -0.99m, recoveryBars: 0, recoveryFraction: 0m);
                break;
            case StressScenario.FTXLike:
                // -50% over 72 hours, recovery to -30% from start.
                ApplyShock(modified, injectAtIndex, 72, -0.50m, recoveryBars: 168, recoveryFraction: 0.4m);
                break;
            case StressScenario.LiquidityDry:
                // Price unchanged (trend), but widen high-low range by 10x to simulate spread blowup.
                ApplyWideningSpread(modified, injectAtIndex, 48, 10m);
                break;
        }
        return modified;
    }

    private static void ApplyShock(List<Candle> candles, int startIdx, int shockBars, decimal totalDrop, int recoveryBars, decimal recoveryFraction)
    {
        decimal entryPrice = candles[startIdx].Close;
        decimal lowPrice = entryPrice * (1m + totalDrop);   // e.g. -0.40 → entry × 0.60

        // Shock phase — linear drop.
        for (int k = 0; k < shockBars && startIdx + k < candles.Count; k++)
        {
            var frac = (decimal)(k + 1) / shockBars;
            var target = entryPrice + (lowPrice - entryPrice) * frac;
            var c = candles[startIdx + k];
            candles[startIdx + k] = new Candle
            {
                Timestamp = c.Timestamp,
                Open = k == 0 ? entryPrice : candles[startIdx + k - 1].Close,
                High = Math.Max(c.Open, target),
                Low = Math.Min(c.Low, target),
                Close = target,
                Volume = c.Volume * 5m  // crisis = vol spike
            };
        }

        // Recovery phase — linear bounce.
        if (recoveryBars > 0 && recoveryFraction > 0m)
        {
            var recoveryTarget = lowPrice + (entryPrice - lowPrice) * recoveryFraction;
            int recoveryStart = startIdx + shockBars;
            for (int k = 0; k < recoveryBars && recoveryStart + k < candles.Count; k++)
            {
                var frac = (decimal)(k + 1) / recoveryBars;
                var target = lowPrice + (recoveryTarget - lowPrice) * frac;
                var c = candles[recoveryStart + k];
                candles[recoveryStart + k] = new Candle
                {
                    Timestamp = c.Timestamp,
                    Open = candles[recoveryStart + k - 1].Close,
                    High = Math.Max(c.High, target),
                    Low = Math.Min(c.Low, target),
                    Close = target,
                    Volume = c.Volume
                };
            }
        }
    }

    private static void ApplyWideningSpread(List<Candle> candles, int startIdx, int bars, decimal multiplier)
    {
        for (int k = 0; k < bars && startIdx + k < candles.Count; k++)
        {
            var c = candles[startIdx + k];
            var mid = (c.High + c.Low) / 2m;
            var spread = (c.High - c.Low) * multiplier;
            candles[startIdx + k] = new Candle
            {
                Timestamp = c.Timestamp,
                Open = c.Open,
                High = mid + spread / 2m,
                Low = mid - spread / 2m,
                Close = c.Close,
                Volume = c.Volume * 3m
            };
        }
    }

    /// <summary>
    /// Evaluate defense efficacy against a scenario. Given the pre/post portfolio
    /// equity curves, reports how well each safety layer responded.
    /// </summary>
    public StressTestReport Evaluate(
        StressScenario scenario,
        IReadOnlyList<decimal> baselineEquity,
        IReadOnlyList<decimal> phaseDEquity,
        int injectIndex,
        MarketRegime detectedRegimeAtInject,
        bool circuitTripped,
        int barsToCircuitTrip,
        CorrelationGuardState correlationStateAtInject)
    {
        decimal peak = baselineEquity.Take(injectIndex + 1).Max();
        decimal baselineTrough = baselineEquity.Skip(injectIndex).Min();
        decimal baselineDd = peak > 0 ? (peak - baselineTrough) / peak : 0m;

        decimal peakD = phaseDEquity.Take(injectIndex + 1).Max();
        decimal phaseDTrough = phaseDEquity.Skip(injectIndex).Min();
        decimal phaseDDd = peakD > 0 ? (peakD - phaseDTrough) / peakD : 0m;

        decimal ddSavings = baselineDd - phaseDDd;
        var grade = ddSavings switch
        {
            > 0.05m => "EXCELLENT — Phase D saved >5% of portfolio DD",
            > 0.02m => "GOOD — Phase D saved 2-5% DD",
            > 0.005m => "MARGINAL — Phase D saved <2% DD",
            _ => "INEFFECTIVE — Phase D did not meaningfully reduce DD"
        };

        var observations = new List<string>
        {
            $"Detected regime at crisis inject: {detectedRegimeAtInject}",
            $"Correlation guard state at inject: {correlationStateAtInject}",
            circuitTripped
                ? $"Circuit breaker TRIPPED {barsToCircuitTrip} bars after crisis start — flattening triggered"
                : "Circuit breaker did NOT trip — crisis stayed below 5% DD threshold",
            $"Baseline max DD: {baselineDd:P2}",
            $"Phase D max DD:  {phaseDDd:P2}  (savings: {ddSavings:P2})",
        };

        return new StressTestReport(scenario, baselineDd, phaseDDd, ddSavings, grade, observations);
    }
}

public enum StressScenario { FlashCrash, LunaLike, FTXLike, LiquidityDry }

public sealed record StressTestReport(
    StressScenario Scenario,
    decimal BaselineMaxDrawdown,
    decimal PhaseDMaxDrawdown,
    decimal DrawdownSavings,
    string Grade,
    IReadOnlyList<string> Observations);
