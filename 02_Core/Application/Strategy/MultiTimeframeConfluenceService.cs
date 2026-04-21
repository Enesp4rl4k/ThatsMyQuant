using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

public sealed class MultiTimeframeConfluenceService
{
    public MultiTimeframeConfluenceResult EvaluateFromBase(IReadOnlyList<Candle> baseCandles)
    {
        if (baseCandles == null || baseCandles.Count < 10)
            return new MultiTimeframeConfluenceResult(0, "NoData", "Not enough base candles for MTF.", []);

        var dict = new Dictionary<string, IReadOnlyList<Candle>>();
        var tfMinutes = (int)(baseCandles[^1].Timestamp - baseCandles[^2].Timestamp).TotalMinutes;
        
        // Always add base
        dict[GetTimeframeName(tfMinutes)] = baseCandles;

        // Try aggregate 1h (~60m)
        if (tfMinutes < 60) dict["1h"] = AggregateCandles(baseCandles, 60 / tfMinutes);
        // Try aggregate 4h (~240m)
        if (tfMinutes < 240) dict["4h"] = AggregateCandles(baseCandles, 240 / tfMinutes);
        // Try aggregate 1d (~1440m)
        if (tfMinutes < 1440) dict["1d"] = AggregateCandles(baseCandles, 1440 / tfMinutes);

        return Evaluate(dict);
    }

    private static string GetTimeframeName(int minutes) => minutes switch
    {
        <= 1 => "1m", <= 5 => "5m", <= 15 => "15m", <= 60 => "1h", <= 240 => "4h", _ => "1d"
    };

    private static IReadOnlyList<Candle> AggregateCandles(IReadOnlyList<Candle> baseCandles, int groupSize)
    {
        if (groupSize <= 1 || baseCandles.Count < groupSize) return Array.Empty<Candle>();
        var result = new List<Candle>();
        for (int i = 0; i < baseCandles.Count; i += groupSize)
        {
            var chunk = baseCandles.Skip(i).Take(groupSize).ToList();
            if (chunk.Count < groupSize && i > 0) continue; // skip incomplete trailing chunk unless it's all we have
            result.Add(new Candle
            {
                Timestamp = chunk.Last().Timestamp,
                Open = chunk.First().Open,
                High = chunk.Max(c => c.High),
                Low = chunk.Min(c => c.Low),
                Close = chunk.Last().Close,
                Volume = chunk.Sum(c => c.Volume)
            });
        }
        return result;
    }

    public MultiTimeframeConfluenceResult Evaluate(IReadOnlyDictionary<string, IReadOnlyList<Candle>> candlesByTimeframe)
    {
        ArgumentNullException.ThrowIfNull(candlesByTimeframe);

        var snapshots = candlesByTimeframe
            .Where(pair => pair.Value is { Count: >= 3 })
            .Select(pair => BuildSnapshot(pair.Key, pair.Value))
            .OrderBy(snapshot => ResolveWeight(snapshot.Timeframe))
            .ToList();

        if (snapshots.Count == 0)
        {
            return new MultiTimeframeConfluenceResult(0, "NoData", "No candle history was provided for confluence scoring.", []);
        }

        var weightedBias = 0d;
        var weightTotal = 0d;
        var momentumStrength = 0d;

        foreach (var snapshot in snapshots)
        {
            var weight = ResolveWeight(snapshot.Timeframe);
            weightedBias += snapshot.BiasScore * weight;
            momentumStrength += snapshot.MomentumScore * weight;
            weightTotal += weight;
        }

        var normalizedBias = weightTotal > 0 ? weightedBias / weightTotal : 0d;
        var normalizedMomentum = weightTotal > 0 ? momentumStrength / weightTotal : 0d;
        var alignedSnapshots = snapshots.Count(snapshot => Math.Sign(snapshot.BiasScore) == Math.Sign(normalizedBias) || Math.Abs(snapshot.BiasScore) < 0.05d);
        var alignmentRatio = (double)alignedSnapshots / snapshots.Count;

        var score = (int)Math.Round(Math.Clamp(
            Math.Min(Math.Abs(normalizedBias) * 1000d, 50d) +
            Math.Min(normalizedMomentum * 1000d, 30d) +
            (alignmentRatio * 20d),
            0d,
            100d));

        var label = score switch
        {
            >= 80 => "Strong",
            >= 60 => "Confirmed",
            >= 40 => "Mixed",
            _ => "Weak"
        };

        var summary = $"{label} multi-timeframe confluence. Alignment {alignmentRatio:P0}, trend bias {normalizedBias:+0.00;-0.00;0.00}, momentum {normalizedMomentum:0.00}.";
        return new MultiTimeframeConfluenceResult(score, label, summary, snapshots);
    }

    private static TimeframeConfluenceSnapshot BuildSnapshot(string timeframe, IReadOnlyList<Candle> candles)
    {
        var closes = candles.Select(candle => candle.Close).ToArray();
        var current = closes[^1];
        var shortAverage = closes.Skip(Math.Max(0, closes.Length - 3)).Average();
        var longAverage = closes.Average();
        var previous = closes[^2];
        var momentum = previous == 0m ? 0d : (double)((current - previous) / previous);
        var bias = longAverage == 0m ? 0d : (double)((shortAverage - longAverage) / longAverage);

        return new TimeframeConfluenceSnapshot(timeframe, bias, Math.Abs(momentum));
    }

    private static double ResolveWeight(string timeframe)
        => timeframe.ToLowerInvariant() switch
        {
            "5m" => 1.0,
            "15m" => 1.2,
            "1h" => 1.6,
            "4h" => 2.0,
            "1d" => 2.3,
            _ => 1.0
        };
}

public sealed record MultiTimeframeConfluenceResult(
    int Score,
    string Label,
    string Summary,
    IReadOnlyList<TimeframeConfluenceSnapshot> Snapshots);

public sealed record TimeframeConfluenceSnapshot(
    string Timeframe,
    double BiasScore,
    double MomentumScore);
