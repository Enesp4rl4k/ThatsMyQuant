using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Domain.Entities;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// A zero-allocation HFT engine that tracks the price spread between two correlated assets.
/// It uses a rolling window to calculate the Z-Score of the spread and emits signals.
/// </summary>
public class PairsTradingEngine
{
    private readonly ILogger<PairsTradingEngine> _logger;
    private readonly string _symbolA;
    private readonly string _symbolB;
    
    // Using fixed sized arrays or pointers for true zero alloc in C#, 
    // but a pre-allocated ConcurrentQueue or CircularBuffer works well enough.
    private readonly double[] _spreadHistory;
    private int _historyIndex = 0;
    private int _historyCount = 0;
    private readonly int _windowSize;

    private decimal _latestPriceA;
    private decimal _latestPriceB;

    private const double ZScoreThresholdEntry = 2.0;
    private const double ZScoreThresholdExit = 0.5;

    public PairsTradingEngine(string symbolA, string symbolB, int windowSize, ILogger<PairsTradingEngine> logger)
    {
        _symbolA = symbolA;
        _symbolB = symbolB;
        _windowSize = windowSize;
        _spreadHistory = new double[windowSize];
        _logger = logger;
    }

    /// <summary>
    /// Processes a market tick. Passed by 'in' to avoid copying the struct (zero-allocation).
    /// </summary>
    public void ProcessTick(in MarketTick tick)
    {
        bool updated = false;

        if (tick.Symbol == _symbolA)
        {
            _latestPriceA = tick.Price;
            updated = true;
        }
        else if (tick.Symbol == _symbolB)
        {
            _latestPriceB = tick.Price;
            updated = true;
        }

        if (updated && _latestPriceA > 0 && _latestPriceB > 0)
        {
            EvaluateSpread();
        }
    }

    private void EvaluateSpread()
    {
        // Spread = log(PriceA) - log(PriceB) or simple ratio
        double currentSpread = (double)(_latestPriceA / _latestPriceB);

        // Circular buffer insertion
        _spreadHistory[_historyIndex] = currentSpread;
        _historyIndex = (_historyIndex + 1) % _windowSize;
        if (_historyCount < _windowSize) _historyCount++;

        // Need enough data
        if (_historyCount < _windowSize) return;

        double zScore = CalculateZScore(currentSpread);

        if (zScore > ZScoreThresholdEntry)
        {
            // Spread is too wide: Asset A is overpriced relative to B.
            // Action: Short A, Long B
            _logger.LogInformation("PAIRS SIGNAL: Short {SymbolA} / Long {SymbolB} | Z-Score: {Z:F2}", _symbolA, _symbolB, zScore);
        }
        else if (zScore < -ZScoreThresholdEntry)
        {
            // Spread is too narrow: Asset A is underpriced relative to B.
            // Action: Long A, Short B
            _logger.LogInformation("PAIRS SIGNAL: Long {SymbolA} / Short {SymbolB} | Z-Score: {Z:F2}", _symbolA, _symbolB, zScore);
        }
    }

    private double CalculateZScore(double currentSpread)
    {
        double sum = 0;
        for (int i = 0; i < _windowSize; i++)
        {
            sum += _spreadHistory[i];
        }
        double mean = sum / _windowSize;

        double sumOfSquaresOfDifferences = 0;
        for (int i = 0; i < _windowSize; i++)
        {
            double diff = _spreadHistory[i] - mean;
            sumOfSquaresOfDifferences += diff * diff;
        }

        double variance = sumOfSquaresOfDifferences / _windowSize;
        double stdDev = Math.Sqrt(variance);

        if (stdDev == 0) return 0;

        return (currentSpread - mean) / stdDev;
    }
}
