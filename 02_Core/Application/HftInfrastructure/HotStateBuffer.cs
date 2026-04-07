using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ThatsMyQuant.Application.HftInfrastructure;

/// <summary>
/// Lock-free, atomic shared memory buffer for the Hot Path (Red Layer).
/// Stores real-time market microstructure state that the execution engine reads
/// on every tick without any locking, allocation, or kernel transition.
///
/// Architecture:
///   RED  (Hot)  → Reads state atomically via Volatile.Read (nanoseconds)
///   YELLOW (Warm) → Writes risk/ML updates via Interlocked (microseconds)
///   BLUE (Cold)  → Writes regime/parameter updates via Interlocked (seconds)
///
/// Memory layout uses explicit padding to prevent false sharing between CPU cores.
/// Each field sits on its own 64-byte cache line.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 512)]
public sealed class HotStateBuffer
{
    // ═══════════════════════════════════════════════════════════
    // MARKET MICROSTRUCTURE (written by Red Path tick processor)
    // ═══════════════════════════════════════════════════════════

    /// <summary>Best bid price (raw long = price * 1e8 for atomic ops on decimal).</summary>
    [FieldOffset(0)]
    private long _bestBidRaw;

    /// <summary>Best ask price (raw long).</summary>
    [FieldOffset(64)]
    private long _bestAskRaw;

    /// <summary>Net Order Flow Imbalance [-1.0, +1.0] scaled to int (x10000).</summary>
    [FieldOffset(128)]
    private int _ofiScaled;

    /// <summary>VPIN toxicity [0.0, 1.0] scaled to int (x10000).</summary>
    [FieldOffset(136)]
    private int _vpinScaled;

    /// <summary>Tick sequence number — monotonically increasing. Used for staleness detection.</summary>
    [FieldOffset(144)]
    private long _tickSequence;

    /// <summary>Last tick timestamp (UTC ticks).</summary>
    [FieldOffset(152)]
    private long _lastTickTimestamp;

    // ═══════════════════════════════════════════════════════════
    // RISK GUARD FLAGS (written by Yellow Path RiskAgent)
    // ═══════════════════════════════════════════════════════════

    /// <summary>1 = trading allowed, 0 = halted by risk governor.</summary>
    [FieldOffset(192)]
    private int _tradingAllowed;

    /// <summary>1 = kill switch armed, 0 = normal.</summary>
    [FieldOffset(196)]
    private int _killSwitchArmed;

    /// <summary>Safe position size percent (x10000). Written by RiskAgent.</summary>
    [FieldOffset(200)]
    private int _safePositionSizeScaled;

    /// <summary>Drawdown percent (x100). Written by RiskAgent.</summary>
    [FieldOffset(204)]
    private int _drawdownPercentScaled;

    // ═══════════════════════════════════════════════════════════
    // ML PREDICTION (written by Yellow Path MicrosecondPredictionEngine)
    // ═══════════════════════════════════════════════════════════

    /// <summary>Directional bias [-1.0, +1.0] scaled to int (x10000). Positive = bullish.</summary>
    [FieldOffset(256)]
    private int _directionalBiasScaled;

    /// <summary>Prediction confidence [0, 100] scaled to int (x100).</summary>
    [FieldOffset(260)]
    private int _predictionConfidenceScaled;

    // ═══════════════════════════════════════════════════════════
    // REGIME PARAMETERS (written by Blue Path MarketRegimeDetector)
    // ═══════════════════════════════════════════════════════════

    /// <summary>Current regime: 0=Unknown, 1=Trending, 2=Ranging, 3=Volatile.</summary>
    [FieldOffset(320)]
    private int _currentRegime;

    /// <summary>Stop-loss ATR multiplier (x1000).</summary>
    [FieldOffset(324)]
    private int _stopLossMultiplierScaled;

    /// <summary>Take-profit ATR multiplier (x1000).</summary>
    [FieldOffset(328)]
    private int _takeProfitMultiplierScaled;

    public HotStateBuffer()
    {
        _tradingAllowed = 1;
        _stopLossMultiplierScaled = 2000;  // 2.0x default
        _takeProfitMultiplierScaled = 3000; // 3.0x default
    }

    // ═══════════════════════════════════════════════════════════
    // RED PATH WRITERS (called from dedicated tick thread)
    // ═══════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateQuote(decimal bestBid, decimal bestAsk)
    {
        Interlocked.Exchange(ref _bestBidRaw, DecimalToLong(bestBid));
        Interlocked.Exchange(ref _bestAskRaw, DecimalToLong(bestAsk));
        Interlocked.Increment(ref _tickSequence);
        Interlocked.Exchange(ref _lastTickTimestamp, DateTime.UtcNow.Ticks);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateOfi(double ofi)
    {
        Interlocked.Exchange(ref _ofiScaled, (int)(Math.Clamp(ofi, -1.0, 1.0) * 10000));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateVpin(decimal vpin)
    {
        Interlocked.Exchange(ref _vpinScaled, (int)(Math.Clamp(vpin, 0m, 1m) * 10000));
    }

    // ═══════════════════════════════════════════════════════════
    // YELLOW PATH WRITERS (called from RiskAgent / ML Engine)
    // ═══════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetTradingAllowed(bool allowed)
        => Interlocked.Exchange(ref _tradingAllowed, allowed ? 1 : 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetKillSwitch(bool armed)
        => Interlocked.Exchange(ref _killSwitchArmed, armed ? 1 : 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateSafePositionSize(decimal percent)
        => Interlocked.Exchange(ref _safePositionSizeScaled, (int)(percent * 10000));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateDrawdown(decimal percent)
        => Interlocked.Exchange(ref _drawdownPercentScaled, (int)(percent * 100));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateDirectionalBias(float bias, float confidence)
    {
        Interlocked.Exchange(ref _directionalBiasScaled, (int)(Math.Clamp(bias, -1f, 1f) * 10000));
        Interlocked.Exchange(ref _predictionConfidenceScaled, (int)(Math.Clamp(confidence, 0f, 100f) * 100));
    }

    // ═══════════════════════════════════════════════════════════
    // BLUE PATH WRITERS (called from BackgroundService regime detector)
    // ═══════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateRegimeParameters(int regime, double slMultiplier, double tpMultiplier)
    {
        Interlocked.Exchange(ref _currentRegime, regime);
        Interlocked.Exchange(ref _stopLossMultiplierScaled, (int)(slMultiplier * 1000));
        Interlocked.Exchange(ref _takeProfitMultiplierScaled, (int)(tpMultiplier * 1000));
    }

    // ═══════════════════════════════════════════════════════════
    // RED PATH READERS (hot loop — zero allocation, nanosecond)
    // ═══════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public decimal ReadBestBid() => LongToDecimal(Volatile.Read(ref _bestBidRaw));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public decimal ReadBestAsk() => LongToDecimal(Volatile.Read(ref _bestAskRaw));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double ReadOfi() => Volatile.Read(ref _ofiScaled) / 10000.0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public decimal ReadVpin() => Volatile.Read(ref _vpinScaled) / 10000m;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadTickSequence() => Volatile.Read(ref _tickSequence);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsTradingAllowed() => Volatile.Read(ref _tradingAllowed) == 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsKillSwitchArmed() => Volatile.Read(ref _killSwitchArmed) == 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public decimal ReadSafePositionSize() => Volatile.Read(ref _safePositionSizeScaled) / 10000m;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public decimal ReadDrawdownPercent() => Volatile.Read(ref _drawdownPercentScaled) / 100m;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadDirectionalBias() => Volatile.Read(ref _directionalBiasScaled) / 10000f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadPredictionConfidence() => Volatile.Read(ref _predictionConfidenceScaled) / 100f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadCurrentRegime() => Volatile.Read(ref _currentRegime);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double ReadStopLossMultiplier() => Volatile.Read(ref _stopLossMultiplierScaled) / 1000.0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double ReadTakeProfitMultiplier() => Volatile.Read(ref _takeProfitMultiplierScaled) / 1000.0;

    /// <summary>
    /// Returns true if the last tick is older than the given threshold.
    /// Used by DeadMan's Switch to detect stale data feeds.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsDataStale(TimeSpan threshold)
    {
        var lastTick = Volatile.Read(ref _lastTickTimestamp);
        if (lastTick == 0) return true;
        return (DateTime.UtcNow.Ticks - lastTick) > threshold.Ticks;
    }

    /// <summary>
    /// Reads the full hot-path snapshot for diagnostic/logging purposes.
    /// NOT for the hot loop — this allocates.
    /// </summary>
    public HotStateSnapshot TakeSnapshot() => new()
    {
        BestBid = ReadBestBid(),
        BestAsk = ReadBestAsk(),
        Ofi = ReadOfi(),
        Vpin = ReadVpin(),
        TickSequence = ReadTickSequence(),
        TradingAllowed = IsTradingAllowed(),
        KillSwitchArmed = IsKillSwitchArmed(),
        SafePositionSize = ReadSafePositionSize(),
        DrawdownPercent = ReadDrawdownPercent(),
        DirectionalBias = ReadDirectionalBias(),
        PredictionConfidence = ReadPredictionConfidence(),
        CurrentRegime = ReadCurrentRegime(),
        StopLossMultiplier = ReadStopLossMultiplier(),
        TakeProfitMultiplier = ReadTakeProfitMultiplier(),
        DataStale = IsDataStale(TimeSpan.FromMilliseconds(500))
    };

    // ═══════════════════════════════════════════════════════════
    // DECIMAL ↔ LONG conversion (8 decimal places precision)
    // ═══════════════════════════════════════════════════════════

    private const long DecimalScale = 100_000_000L;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long DecimalToLong(decimal value) => (long)(value * DecimalScale);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static decimal LongToDecimal(long value) => value / (decimal)DecimalScale;
}

public sealed record HotStateSnapshot
{
    public decimal BestBid { get; init; }
    public decimal BestAsk { get; init; }
    public double Ofi { get; init; }
    public decimal Vpin { get; init; }
    public long TickSequence { get; init; }
    public bool TradingAllowed { get; init; }
    public bool KillSwitchArmed { get; init; }
    public decimal SafePositionSize { get; init; }
    public decimal DrawdownPercent { get; init; }
    public float DirectionalBias { get; init; }
    public float PredictionConfidence { get; init; }
    public int CurrentRegime { get; init; }
    public double StopLossMultiplier { get; init; }
    public double TakeProfitMultiplier { get; init; }
    public bool DataStale { get; init; }
}
