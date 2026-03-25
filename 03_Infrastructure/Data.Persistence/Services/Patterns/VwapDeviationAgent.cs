using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Services.Patterns;

public sealed class VwapDeviationAgent : IPatternAgent
{
    private readonly ILogger<VwapDeviationAgent> _logger;

    public string AgentId => "Pattern.VwapDeviation";

    public VwapDeviationAgent(ILogger<VwapDeviationAgent> logger)
        => _logger = logger;

    public Task<PatternSnapshot> AnalyzeAsync(
        Signal signal,
        FoundationSnapshot foundation,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default)
    {
        if (foundation.Vwap == 0m || foundation.VwapStdDev == 0m)
            return Task.FromResult(PatternSnapshot.Empty);

        var deviation = (double)((signal.Price - foundation.Vwap) / foundation.VwapStdDev);
        var tags = new List<string>();

        if (Math.Abs(deviation) > 2.0) tags.Add("VwapExtreme");
        else if (Math.Abs(deviation) > 1.0) tags.Add("VwapExtended");
        else tags.Add("VwapMean");

        return Task.FromResult(new PatternSnapshot
        {
            VwapDeviationSigmas = deviation,
            PatternTags = tags
        });
    }
}
