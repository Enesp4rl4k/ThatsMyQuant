using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Volume-Synchronized Probability of Informed Trading (VPIN).
/// Detects toxic order flow and institutional predation by measuring the
/// imbalance between buy-classified and sell-classified volume in fixed-volume buckets.
///
/// Academic basis: Easley, Lopez de Prado, O'Hara (2012) — "Flow Toxicity and Liquidity
/// in a High-Frequency World."
/// </summary>
public sealed class VpinCalculator
{
    private readonly int _bucketCount;
    private readonly decimal _bucketVolumeSize;
    private readonly Queue<(decimal BuyVolume, decimal SellVolume)> _buckets;
    private decimal _currentBuyVolume;
    private decimal _currentSellVolume;
    private decimal _currentBucketFill;
    private decimal _lastPrice;

    /// <summary>
    /// Current VPIN value (0 to 1). Values above 0.7 indicate high toxicity.
    /// </summary>
    public decimal CurrentVpin { get; private set; }

    /// <summary>
    /// Whether the current VPIN level indicates dangerous toxicity (whale hunting / flash crash risk).
    /// </summary>
    public bool IsToxic => CurrentVpin >= ToxicityThreshold;

    /// <summary>
    /// Toxicity threshold. Default 0.70 — above this the bot should halt.
    /// </summary>
    public decimal ToxicityThreshold { get; set; } = 0.70m;

    /// <param name="bucketVolumeSize">Volume required to fill one bucket (e.g. 1000 units).</param>
    /// <param name="bucketCount">Number of rolling buckets for VPIN average (e.g. 50).</param>
    public VpinCalculator(decimal bucketVolumeSize = 1000m, int bucketCount = 50)
    {
        _bucketVolumeSize = bucketVolumeSize > 0 ? bucketVolumeSize : 1000m;
        _bucketCount = bucketCount > 0 ? bucketCount : 50;
        _buckets = new Queue<(decimal, decimal)>(_bucketCount + 1);
    }

    /// <summary>
    /// Processes a single tick. Uses the Bulk Volume Classification (BVC) method:
    /// volume is probabilistically split into buy/sell using price change relative
    /// to a normal CDF approximation.
    /// </summary>
    public void ProcessTick(decimal price, decimal volume)
    {
        if (volume <= 0 || price <= 0) return;

        decimal buyFraction = ClassifyVolume(price);
        decimal buyVol = volume * buyFraction;
        decimal sellVol = volume * (1m - buyFraction);

        _currentBuyVolume += buyVol;
        _currentSellVolume += sellVol;
        _currentBucketFill += volume;
        _lastPrice = price;

        while (_currentBucketFill >= _bucketVolumeSize)
        {
            decimal overflow = _currentBucketFill - _bucketVolumeSize;
            decimal fillRatio = _bucketVolumeSize / (_currentBucketFill > 0 ? _currentBucketFill : 1m);

            _buckets.Enqueue((_currentBuyVolume * fillRatio, _currentSellVolume * fillRatio));

            if (_buckets.Count > _bucketCount)
                _buckets.Dequeue();

            _currentBuyVolume *= (1m - fillRatio);
            _currentSellVolume *= (1m - fillRatio);
            _currentBucketFill = overflow;
        }

        RecalculateVpin();
    }

    /// <summary>
    /// Processes a candle bar using OHLC-derived tick approximation.
    /// Volume is split across the bar's price range.
    /// </summary>
    public void ProcessBar(Candle candle)
    {
        if (candle.Volume <= 0) return;

        // Approximate 4 ticks from OHLC
        decimal quarterVol = candle.Volume / 4m;
        ProcessTick(candle.Open, quarterVol);
        ProcessTick(candle.High, quarterVol);
        ProcessTick(candle.Low, quarterVol);
        ProcessTick(candle.Close, quarterVol);
    }

    /// <summary>
    /// Bulk Volume Classification: approximates buy/sell split using
    /// price change normalized by volatility (simplified CDF).
    /// </summary>
    private decimal ClassifyVolume(decimal price)
    {
        if (_lastPrice <= 0) return 0.5m;

        decimal priceChange = price - _lastPrice;
        decimal normalizedChange = _lastPrice > 0 ? priceChange / _lastPrice : 0m;

        // Approximate standard normal CDF using logistic function: 1 / (1 + e^(-k*x))
        // k=1600 calibrated for typical crypto/equity tick-to-tick moves
        double z = (double)(normalizedChange * 1600m);
        double cdf = 1.0 / (1.0 + Math.Exp(-z));

        return Math.Clamp((decimal)cdf, 0m, 1m);
    }

    private void RecalculateVpin()
    {
        if (_buckets.Count == 0)
        {
            CurrentVpin = 0m;
            return;
        }

        decimal totalImbalance = 0m;
        decimal totalVolume = 0m;

        foreach (var (buyVol, sellVol) in _buckets)
        {
            totalImbalance += Math.Abs(buyVol - sellVol);
            totalVolume += buyVol + sellVol;
        }

        CurrentVpin = totalVolume > 0 ? totalImbalance / totalVolume : 0m;
    }

    /// <summary>
    /// Returns a snapshot of current VPIN state for logging/decision-making.
    /// </summary>
    public VpinSnapshot GetSnapshot() => new()
    {
        Vpin = CurrentVpin,
        IsToxic = IsToxic,
        BucketsFilled = _buckets.Count,
        TotalBuckets = _bucketCount,
        ToxicityThreshold = ToxicityThreshold
    };

    public sealed record VpinSnapshot
    {
        public decimal Vpin { get; init; }
        public bool IsToxic { get; init; }
        public int BucketsFilled { get; init; }
        public int TotalBuckets { get; init; }
        public decimal ToxicityThreshold { get; init; }
    }
}
