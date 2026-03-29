using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Interfaces;

/// <summary>
/// Computes technical indicators from historical candle data and produces trade decisions.
/// </summary>
public interface ITechnicalAnalysisService
{
    /// <summary>
    /// Compute all indicators (RSI, MACD, EMA, Bollinger, ATR, Volume) from candle data.
    /// </summary>
    Task<IndicatorSnapshot> ComputeIndicatorsAsync(IReadOnlyList<Candle> candles);
    Task<TradeDecision> AnalyzeAsync(Signal signal, IReadOnlyList<Candle> candles, RiskParameters risk);
}
