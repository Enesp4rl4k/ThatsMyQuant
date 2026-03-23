using System;
using System.Runtime.CompilerServices;

namespace ThatsMyQuant.Application.MachineLearning;

/// <summary>
/// Execution bounds strictly mapped to $O(1)$ Math transitions processing sequential updates.
/// </summary>
public sealed class OnlineMathHelper
{
    public double Ema { get; private set; }
    
    // RSI Structs
    private double _avgGain;
    private double _avgLoss;
    private double _previousPrice;
    private readonly int _period;
    private int _ticks;

    // Standardized alpha smoothing scalar
    private readonly double _alpha;

    public OnlineMathHelper(int period)
    {
        _period = period;
        _alpha = 2.0 / (period + 1.0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateEma(double currentPrice)
    {
        if (Ema == 0)
        {
            Ema = currentPrice;
            return;
        }

        Ema = (currentPrice * _alpha) + (Ema * (1 - _alpha));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double UpdateAndGetRSI(double currentPrice)
    {
        if (_previousPrice == 0)
        {
            _previousPrice = currentPrice;
            return 50.0; // Neutral start
        }

        double change = currentPrice - _previousPrice;
        _previousPrice = currentPrice;

        double currentGain = change > 0 ? change : 0;
        double currentLoss = change < 0 ? -change : 0;

        // Wilder's Smoothing natively
        if (_ticks < _period)
        {
            _avgGain += currentGain;
            _avgLoss += currentLoss;
            _ticks++;
            
            if (_ticks == _period)
            {
                _avgGain /= _period;
                _avgLoss /= _period;
            }
            return 50.0; // Warming up
        }
        else
        {
            _avgGain = ((_avgGain * (_period - 1)) + currentGain) / _period;
            _avgLoss = ((_avgLoss * (_period - 1)) + currentLoss) / _period;
        }

        if (_avgLoss == 0) return 100.0;
        if (_avgGain == 0) return 0.0;

        double rs = _avgGain / _avgLoss;
        return 100.0 - (100.0 / (1.0 + rs));
    }
}
