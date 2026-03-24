using System;
using System.Collections;
using System.Collections.Generic;

namespace ThatsMyQuant.Domain.Common;

/// <summary>
/// A high-performance, zero-allocation circular buffer designed for HFT tick processing.
/// </summary>
public sealed class CircularBuffer<T> : IEnumerable<T>
{
    private readonly T[] _buffer;
    private int _head;
    private int _tail;
    private int _count;

    public CircularBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentException("Capacity must be greater than zero.", nameof(capacity));
        _buffer = new T[capacity];
    }

    public int Capacity => _buffer.Length;
    public int Count => _count;
    public bool IsFull => _count == _buffer.Length;

    public void Add(T item)
    {
        _buffer[_head] = item;
        _head = (_head + 1) % _buffer.Length;

        if (_count == _buffer.Length)
        {
            _tail = (_tail + 1) % _buffer.Length;
        }
        else
        {
            _count++;
        }
    }

    public T this[int index]
    {
        get
        {
            if (index < 0 || index >= _count) throw new ArgumentOutOfRangeException(nameof(index), "Index was out of range.");
            return _buffer[(_tail + index) % _buffer.Length];
        }
    }

    public void Clear()
    {
        _head = 0;
        _tail = 0;
        _count = 0;
        Array.Clear(_buffer, 0, _buffer.Length);
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < _count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
