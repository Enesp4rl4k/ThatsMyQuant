using System;
using System.Collections.Generic;
using System.Linq;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Implements the Engle-Granger two-step method for testing cointegration between two asset price series.
/// Essential for mean-reversion pairs trading strategies.
/// </summary>
public static class EngleGrangerDetector
{
    public sealed record CointegrationResult(bool IsCointegrated, decimal HedgeRatio, decimal SpreadAverage, decimal SpreadStdDev, decimal AdfTestStatistic);

    /// <summary>
    /// Executes the Engle-Granger test.
    /// Step 1: OLS regression to establish the hedge ratio (Y = \alpha + \beta X + \epsilon).
    /// Step 2: ADF test on the residuals \epsilon to check for stationarity.
    /// </summary>
    /// <param name="seriesY">Prices of Asset Y (Dependent Variable)</param>
    /// <param name="seriesX">Prices of Asset X (Independent Variable)</param>
    /// <returns>CointegrationResult containing the Hedge Ratio (\beta) and stationarity metrics.</returns>
    public static CointegrationResult TestCointegration(IReadOnlyList<decimal> seriesY, IReadOnlyList<decimal> seriesX)
    {
        if (seriesY.Count != seriesX.Count || seriesY.Count < 30) // Minimum sample size for statistical significance
        {
            return new CointegrationResult(false, 0, 0, 0, 0);
        }

        // 1. OLS Regression to find Hedge Ratio (Beta)
        decimal meanX = seriesX.Average();
        decimal meanY = seriesY.Average();

        decimal covariance = 0;
        decimal varianceX = 0;

        for (int i = 0; i < seriesX.Count; i++)
        {
            decimal dx = seriesX[i] - meanX;
            decimal dy = seriesY[i] - meanY;
            covariance += dx * dy;
            varianceX += dx * dx;
        }

        if (varianceX == 0) return new CointegrationResult(false, 0, 0, 0, 0);

        // Hedge ratio
        decimal beta = covariance / varianceX;
        decimal alpha = meanY - (beta * meanX);

        // 2. Calculate Residuals (Spread)
        var residuals = new decimal[seriesX.Count];
        for (int i = 0; i < seriesX.Count; i++)
        {
            residuals[i] = seriesY[i] - (alpha + beta * seriesX[i]);
        }

        // 3. Simple unit root test approximation (Lag-1 Autoregression of residuals)
        // \Delta \epsilon_t = \gamma \epsilon_{t-1} + u_t
        decimal sumResiduals = residuals.Average();
        decimal stdDevResiduals = CalculateStdDev(residuals, sumResiduals);

        decimal gammaCov = 0;
        decimal gammaVar = 0;

        for (int i = 1; i < residuals.Length; i++)
        {
            decimal deltaResidual = residuals[i] - residuals[i - 1];
            decimal laggedResidual = residuals[i - 1]; // We don't deduct mean as residuals are centered ~0
            
            gammaCov += deltaResidual * laggedResidual;
            gammaVar += laggedResidual * laggedResidual;
        }

        decimal gamma = gammaVar == 0 ? 0 : gammaCov / gammaVar;

        // ADF Test Statistic approximation: (gamma) / Standard error of gamma
        // If gamma < -0.25 significantly, it strongly implies mean reversion.
        // We use a simplified threshold for Cointegration:
        bool isCointegrated = gamma < -0.25m && stdDevResiduals > 0;

        return new CointegrationResult(
            IsCointegrated: isCointegrated,
            HedgeRatio: beta,
            SpreadAverage: sumResiduals, // Should be close to 0
            SpreadStdDev: stdDevResiduals,
            AdfTestStatistic: gamma
        );
    }

    private static decimal CalculateStdDev(decimal[] values, decimal mean)
    {
        if (values.Length < 2) return 0;
        decimal sumVariance = values.Sum(v => (v - mean) * (v - mean));
        return (decimal)Math.Sqrt((double)(sumVariance / (values.Length - 1)));
    }
}
