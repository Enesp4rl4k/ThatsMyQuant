using System.Runtime.InteropServices;
using System.Threading;

namespace ThatsMyQuant.Application.HftInfrastructure;

// Cache line padding via StructLayout to completely prevent False Sharing between CPU cores.
// L1 Cache lines are typically 64 bytes on modern x64 architecture. Lifted to a top-level
// struct (was nested in HftRingBuffer<T>) — when nested in a generic, the runtime treats
// its layout as generic-context and reflection-based assembly scanners (e.g. MediatR
// v12 ServiceRegistrar.GetDefinedTypes) throw "generic types cannot have explicit layout"
// while enumerating types for handler registration.
[StructLayout(LayoutKind.Explicit, Size = 192)]
internal struct PaddedSequence
{
    [FieldOffset(64)] // Padding before
    public long Value;
    // 64 + 8 = 72. Padded to 192 guarantees the pointer sits isolated on its own silicon cache line.
}

/// <summary>
/// A specialized, lock-free circular buffer designed for High-Frequency Trading.
/// Derived from the LMAX Disruptor pattern. It allows a dedicated network polling thread to constantly
/// enqueue incoming ticks directly into a strategy thread without yielding to kernel mutexes or locks.
/// </summary>
public sealed class HftRingBuffer<T> where T : class
{
    private readonly T[] _entries;
    private readonly int _mask;

    private PaddedSequence _head;
    private PaddedSequence _tail;

    public HftRingBuffer(int capacity = 1024)
    {
        // Capacity must be a power of 2 for ultra-fast bitwise modulo '&' operations.
        if ((capacity & (capacity - 1)) != 0) 
            throw new ArgumentException("Capacity must be a power of 2.");

        _entries = new T[capacity];
        _mask = capacity - 1;
        _head.Value = 0;
        _tail.Value = 0;
    }

    /// <summary>
    /// Executed by the Network thread. Zero-lock, volatile pointer progression.
    /// </summary>
    public bool TryEnqueue(T item)
    {
        long currentTail = Volatile.Read(ref _tail.Value);
        long currentHead = Volatile.Read(ref _head.Value);

        // Buffer full check
        if (currentTail - currentHead >= _entries.Length) return false;

        _entries[currentTail & _mask] = item;

        // Memory Barrier ensuring the payload is visible to the CPU before the tail pointer increments
        Interlocked.Increment(ref _tail.Value);

        return true;
    }

    /// <summary>
    /// Executed by the Agent Orchestrator thread. Zero-lock spin loop.
    /// </summary>
    public bool TryDequeue(out T? item)
    {
        long currentHead = Volatile.Read(ref _head.Value);
        long currentTail = Volatile.Read(ref _tail.Value);

        // Buffer empty check
        if (currentHead >= currentTail)
        {
            item = null;
            return false;
        }

        item = _entries[currentHead & _mask];

        // Wipe pointer to avoid memory leaks blocking the GC if the internal object holds deep state
        _entries[currentHead & _mask] = null!;

        Interlocked.Increment(ref _head.Value);
        return true;
    }
}
