using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ThatsMyQuant.Infrastructure.Collections;

/// <summary>
/// A high-performance, zero-allocation circular buffer designed for HFT indicator processing.
/// </summary>
/// <typeparam name="T">The type of data stored (usually double or float).</typeparam>
public sealed class RingBuffer<T> where T : struct
{
    private readonly T[] _buffer;
    private int _head;
    private int _count;
    private readonly int _capacity;

    public RingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentException("Capacity must be greater than zero.");
        _capacity = capacity;
        _buffer = new T[capacity];
        _head = 0;
        _count = 0;
    }

    /// <summary>
    /// Adds a new value to the buffer, overwriting the oldest if at capacity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(T value)
    {
        _buffer[_head] = value;
        _head = (_head + 1) % _capacity;
        if (_count < _capacity) _count++;
    }

    public int Count => _count;
    public int Capacity => _capacity;

    /// <summary>
    /// Accesses elements by index, where 0 is the newest and Count-1 is the oldest.
    /// </summary>
    public T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (index < 0 || index >= _count) throw new IndexOutOfRangeException();
            int actualIndex = (_head - 1 - index + _capacity) % _capacity;
            return _buffer[actualIndex];
        }
    }

    /// <summary>
    /// Clears the buffer.
    /// </summary>
    public void Clear()
    {
        _head = 0;
        _count = 0;
    }

    /// <summary>
    /// Copies the current contents into a Span.
    /// </summary>
    public void CopyTo(Span<T> destination)
    {
        if (destination.Length < _count) throw new ArgumentException("Destination too small.");
        for (int i = 0; i < _count; i++)
        {
            destination[i] = this[i];
        }
    }
}
