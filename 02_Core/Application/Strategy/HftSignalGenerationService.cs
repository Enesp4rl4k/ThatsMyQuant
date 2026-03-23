using Microsoft.Extensions.Logging;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Interfaces;
using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Application.Strategy;

public class HftSignalGenerationService : IHftSignalGenerationService
{
    private readonly ITechnicalAnalysisService _technicalAnalysis;
    private readonly ILogger<HftSignalGenerationService> _logger;

    public HftSignalGenerationService(ITechnicalAnalysisService technicalAnalysis, ILogger<HftSignalGenerationService> logger)
    {
        _technicalAnalysis = technicalAnalysis ?? throw new ArgumentNullException(nameof(technicalAnalysis));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<TradeDecision> AnalyzeAsync(Signal signal, List<Candle> candles, RiskParameters risk)
    {
        if (signal == null) throw new ArgumentNullException(nameof(signal));
        if (candles == null || candles.Count == 0) return TradeDecision.Reject("No candle data for HFT analysis.");

        // For now, delegate to technical analysis service
        // In a real HFT scenario, this would include order flow, imbalance, and microsecond patterns
        var decision = await _technicalAnalysis.AnalyzeAsync(signal, candles, risk).ConfigureAwait(false);
        
        _logger.LogDebug("HFT Signal Generated for {Symbol}: {Side} (Conf: {Confidence:F1}%)", 
            signal.Symbol, decision.Side, decision.Confidence);

        return decision;
    }
}