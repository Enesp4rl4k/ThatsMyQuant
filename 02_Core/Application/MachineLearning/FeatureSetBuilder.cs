using Microsoft.ML.Data;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.MachineLearning;

/// <summary>
/// The raw data model expected by ML.NET for structural training.
/// </summary>
public class CandleMlData
{
    // The target variable we want the ML engine to learn to predict
    [ColumnName("Label")]
    public float NextClosePrice { get; set; }

    // The normalized feature vector extracted from historical price action
    [VectorType(5)]
    [ColumnName("Features")]
    public float[] Features { get; set; } = new float[5];
}

public class CandlePrediction
{
    [ColumnName("Score")]
    public float PredictedNextClose { get; set; }
}

/// <summary>
/// Prepares raw OCHLV candlestick data into normalized feature matrices (Z-Scores, Volatility Spreads) 
/// so the ML algorithm can natively digest the momentum.
/// </summary>
public static class FeatureSetBuilder
{
    public static List<CandleMlData> BuildTrainingSet(List<Candle> historicalCandles)
    {
        var dataset = new List<CandleMlData>();

        for (int i = 5; i < historicalCandles.Count - 1; i++) // Leave the final candle out as the label
        {
            var targetLabel = (float)historicalCandles[i + 1].Close;
            var current = historicalCandles[i];
            
            // Feature 1: Spread between High and Low (Absolute Volatility)
            float volatility = (float)(current.High - current.Low);
            
            // Feature 2: Distance from 5-period Simple Moving Average
            float sma5 = (float)historicalCandles.GetRange(i - 4, 5).Average(c => c.Close);
            float distFromSma = (float)current.Close - sma5;

            // Feature 3: Relative Volume (Volume spike detection)
            float avgVolume = (float)historicalCandles.GetRange(i - 4, 5).Average(c => c.Volume);
            float relativeVolume = avgVolume > 0 ? (float)current.Volume / avgVolume : 1f;

            // Feature 4 & 5: Raw return metrics
            float closeToOpen = (float)(current.Close - current.Open);
            float prevCloseToCurrentOpen = (float)(current.Open - historicalCandles[i - 1].Close);

            dataset.Add(new CandleMlData
            {
                NextClosePrice = targetLabel,
                Features = new float[] { volatility, distFromSma, relativeVolume, closeToOpen, prevCloseToCurrentOpen }
            });
        }

        return dataset;
    }
}
