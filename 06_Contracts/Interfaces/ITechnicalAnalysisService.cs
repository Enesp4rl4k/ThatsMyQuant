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
    Task<IndicatorSnapshot> ComputeIndicatorsAsync(List<Candle> candles);

    /// <summary>
    /// Full analysis: compute indicators → run confluence strategy → apply risk management.
    /// Returns a typed TradeDecision with confidence, reasoning, stop-loss, take-profit, and position size.
    /// </summary>
    Task<TradeDecision> AnalyzeAsync(Signal signal, List<Candle> candles, RiskParameters risk);
}
