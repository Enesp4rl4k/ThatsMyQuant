using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

public interface IHftSignalGenerationService
{
    Task<TradeDecision> AnalyzeAsync(Signal signal, List<Candle> candles, RiskParameters risk);
}
