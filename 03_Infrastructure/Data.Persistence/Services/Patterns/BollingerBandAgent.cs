using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Services.Patterns;

public sealed class BollingerBandAgent : IPatternAgent
{
    private readonly ILogger<BollingerBandAgent> _logger;
    private const int Period = 20;
    private const double StdDevMult = 2.0;
    private const double SqueezeBandwidthThreshold = 0.02;

    public string AgentId => "Pattern.BollingerBands";

    public BollingerBandAgent(ILogger<BollingerBandAgent> logger)
        => _logger = logger;

    public Task<PatternSnapshot> AnalyzeAsync(
        Signal signal,
        FoundationSnapshot foundation,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default)
    {
        if (candles.Count < Period)
            return Task.FromResult(PatternSnapshot.Empty);

        var slice = candles.TakeLast(Period).Select(c => c.Close).ToList();
        var mean = slice.Average();
        var variance = slice.Select(p => Math.Pow((double)(p - (decimal)mean), 2)).Average();
        var stdDev = Math.Sqrt(variance);

        var upper = mean + (decimal)(StdDevMult * stdDev);
        var lower = mean - (decimal)(StdDevMult * stdDev);
        var bandwidth = mean > 0 ? (double)((upper - lower) / (decimal)mean) : 0;

        var percentB = (upper - lower) > 0
            ? (double)((signal.Price - lower) / (upper - lower))
            : 0.5;

        var squeeze = bandwidth < SqueezeBandwidthThreshold;
        var tags = new List<string>();
        if (squeeze) tags.Add("BollingerSqueeze");
        if (percentB > 1.0) tags.Add("BollingerOverbought");
        else if (percentB < 0.0) tags.Add("BollingerOversold");

        return Task.FromResult(new PatternSnapshot
        {
            BollingerPercentB = percentB,
            BollingerSqueeze = squeeze,
            PatternTags = tags
        });
    }
}
