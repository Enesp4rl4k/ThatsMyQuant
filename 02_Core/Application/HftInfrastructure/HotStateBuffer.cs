using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ThatsMyQuant.Application.HftInfrastructure;

/// <summary>
/// A zero-allocation, lock-free global state buffer designed for high-frequency trading.
/// It acts as the sync point between the Red (Hot), Yellow (Warm), and Blue (Cold) paths.
/// All operations use atomics (Interlocked) or volatile memory barriers to ensure p99 latency stability.
/// </summary>
public static class HotStateBuffer
{
    // Bit-encoded long fields to ensure 64-bit atomic operations for price/volume
    private static long _bestBid;
    private static long _bestAsk;
    private static long _ofi;               // Order Flow Imbalance (-1.0 to 1.0)
    private static long _vpin;              // VPIN Toxicity Score
    private static int _isRiskBlocked;      // 0 = Normal, 1 = Blocked (Atomic int)
    private static long _directionalBias;   // ML/Warm Path prediction bias (-1.0 to 1.0)
    private static long _lastTickTimestamp; // Last processed tick UTC ticks

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateBid(double price) => Interlocked.Exchange(ref _bestBid, BitConverter.DoubleToInt64Bits(price));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double GetBestBid() => BitConverter.Int64BitsToDouble(Volatile.Read(ref _bestBid));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateAsk(double price) => Interlocked.Exchange(ref _bestAsk, BitConverter.DoubleToInt64Bits(price));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double GetBestAsk() => BitConverter.Int64BitsToDouble(Volatile.Read(ref _bestAsk));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateOFI(double value) => Interlocked.Exchange(ref _ofi, BitConverter.DoubleToInt64Bits(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double GetOFI() => BitConverter.Int64BitsToDouble(Volatile.Read(ref _ofi));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateVPIN(double value) => Interlocked.Exchange(ref _vpin, BitConverter.DoubleToInt64Bits(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double GetVPIN() => BitConverter.Int64BitsToDouble(Volatile.Read(ref _vpin));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetRiskBlocked(bool blocked) => Interlocked.Exchange(ref _isRiskBlocked, blocked ? 1 : 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsRiskBlocked() => Volatile.Read(ref _isRiskBlocked) == 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateDirectionalBias(double value) => Interlocked.Exchange(ref _directionalBias, BitConverter.DoubleToInt64Bits(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double GetDirectionalBias() => BitConverter.Int64BitsToDouble(Volatile.Read(ref _directionalBias));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateHeartbeat() => Interlocked.Exchange(ref _lastTickTimestamp, DateTime.UtcNow.Ticks);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetLastHeartbeat() => Volatile.Read(ref _lastTickTimestamp);

    /// <summary>
    /// Checks if a trade is authorized by both Hot Path risk logic and Cold Path governors.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CanTrade() => !IsRiskBlocked() && GetVPIN() < 1.0; 
}
