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
        // Guard clause: Periyot 0 veya negatif olamaz
        _period = period > 0 ? period : 14; 
        _alpha = 2.0 / (_period + 1.0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateEma(double currentPrice)
    {
        if (Math.Abs(Ema) < double.Epsilon) // Daha güvenli "sıfır" kontrolü
        {
            Ema = currentPrice;
            return;
        }

        Ema = (currentPrice * _alpha) + (Ema * (1 - _alpha));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double UpdateAndGetRSI(double currentPrice)
    {
        if (Math.Abs(_previousPrice) < double.Epsilon)
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
        
        _avgGain = ((_avgGain * (_period - 1)) + currentGain) / _period;
        _avgLoss = ((_avgLoss * (_period - 1)) + currentLoss) / _period;

        // SonarQube Fix: Division by zero prevention & Epsilon check
        if (_avgLoss < double.Epsilon) 
            return 100.0;
        
        if (_avgGain < double.Epsilon) 
            return 0.0;

        double rs = _avgGain / _avgLoss;
        return 100.0 - (100.0 / (1.0 + rs));
    }
}