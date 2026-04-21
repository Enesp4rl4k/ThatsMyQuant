using System.Runtime.InteropServices;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Infrastructure.Indicators;

internal static class LinearRegressionCalculator
{
    private const int DefaultLookback = 50;

    public static LinearRegressionEdgeSnapshot? Calculate(
        IReadOnlyList<Candle> candles,
        double? atr)
    {
        if (candles.Count < 3)
            return null;

        var lookback = Math.Min(DefaultLookback, candles.Count);
        if (lookback < 3)
            return null;

        var span = candles is List<Candle> list
            ? CollectionsMarshal.AsSpan(list)
            : candles.ToArray().AsSpan();
        var window = span.Slice(span.Length - lookback, lookback);

        var n = lookback;
        var sumX = (n - 1d) * n / 2d;
        var sumXX = (n - 1d) * n * ((2d * n) - 1d) / 6d;
        double sumY = 0d;
        double sumYY = 0d;
        double sumXY = 0d;

        for (var i = 0; i < n; i++)
        {
            var y = decimal.ToDouble(window[i].Close);
            sumY += y;
            sumYY += y * y;
            sumXY += i * y;
        }

        var xVariance = (n * sumXX) - (sumX * sumX);
        if (xVariance <= 0d)
            return null;

        var slope = ((n * sumXY) - (sumX * sumY)) / xVariance;
        var intercept = (sumY - (slope * sumX)) / n;

        double sse = 0d;
        for (var i = 0; i < n; i++)
        {
            var y = decimal.ToDouble(window[i].Close);
            var predicted = intercept + (slope * i);
            var residual = y - predicted;
            sse += residual * residual;
        }

        var latestIndex = n - 1;
        var regressionValue = intercept + (slope * latestIndex);
        var latestClose = decimal.ToDouble(window[latestIndex].Close);
        var residualValue = latestClose - regressionValue;
        var standardError = n > 2 ? Math.Sqrt(sse / (n - 2d)) : 0d;
        var sigmaDistance = standardError > 1e-12d ? residualValue / standardError : 0d;

        var yVariance = (n * sumYY) - (sumY * sumY);
        var covariance = (n * sumXY) - (sumX * sumY);
        var rSquared = yVariance > 0d
            ? Math.Clamp((covariance * covariance) / (xVariance * yVariance), 0d, 1d)
            : 0d;

        var atrValue = atr.GetValueOrDefault();
        if (atrValue <= 0d)
            atrValue = Math.Abs(latestClose) * 0.01d;
        if (atrValue <= 1e-12d)
            atrValue = 1d;

        var normalizedSlope = slope / atrValue;

        return new LinearRegressionEdgeSnapshot
        {
            Lookback = lookback,
            Slope = Convert.ToDecimal(slope),
            NormalizedSlope = Convert.ToDecimal(normalizedSlope),
            RegressionValue = Convert.ToDecimal(regressionValue),
            Residual = Convert.ToDecimal(residualValue),
            SigmaDistance = sigmaDistance,
            StandardError = standardError,
            RSquared = rSquared,
            IsOutsideOnePointFiveSigma = Math.Abs(sigmaDistance) >= 1.5d,
            IsOutsideTwoSigma = Math.Abs(sigmaDistance) >= 2.0d,
            IsOverextendedAbove = sigmaDistance > 2.0d
        };
    }
}
