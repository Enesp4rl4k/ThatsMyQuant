using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ThatsMyQuant.Infrastructure.Services.Patterns;

public sealed class BollingerBandAgent : IPatternAgent
{
    private readonly ILogger<BollingerBandAgent> _logger;
    private readonly int _period;
    private readonly double _stdDevMult;
    private readonly double _squeezeBandwidthThreshold;

    public string AgentId => "Pattern.BollingerBands";

    public BollingerBandAgent(ILogger<BollingerBandAgent> logger, IOptions<PatternAgentOptions>? options = null)
    {
        _logger = logger;
        var opts = options?.Value ?? new PatternAgentOptions();
        _period = opts.BollingerPeriod;
        _stdDevMult = opts.BollingerStdDevMultiplier;
        _squeezeBandwidthThreshold = opts.BollingerSqueezeThreshold;
    }

    public Task<PatternSnapshot> AnalyzeAsync(
        Signal signal,
        FoundationSnapshot foundation,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default)
    {
        if (candles.Count < _period)
            return Task.FromResult(PatternSnapshot.Empty);

        var slice = candles.TakeLast(_period).Select(c => c.Close).ToList();
        var mean = slice.Average();
        var variance = slice.Select(p => Math.Pow((double)(p - (decimal)mean), 2)).Average();
        var stdDev = Math.Sqrt(variance);

        var upper = mean + (decimal)(_stdDevMult * stdDev);
        var lower = mean - (decimal)(_stdDevMult * stdDev);
        var bandwidth = mean > 0 ? (double)((upper - lower) / (decimal)mean) : 0;

        var percentB = (upper - lower) > 0
            ? (double)((signal.Price - lower) / (upper - lower))
            : 0.5;

        var squeeze = bandwidth < _squeezeBandwidthThreshold;
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
