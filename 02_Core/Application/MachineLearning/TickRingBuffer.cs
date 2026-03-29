using System;

namespace ThatsMyQuant.Application.MachineLearning;

/// <summary>
/// A guaranteed zero-allocation continuous Circular Buffer specifically optimized for high-frequency pricing.
/// Uses a flat array mapping directly to contiguous memory, rolling indexes linearly bypassing standard Collections.
/// </summary>
public sealed class TickRingBuffer
{
    private readonly double[] _metrics;
    private int _head;
    private int _count;
    private readonly int _capacity;

    private double _sum;

    public TickRingBuffer(int capacity)
    {
        _capacity = capacity;
        _metrics = new double[capacity];
        _head = 0;
        _count = 0;
        _sum = 0;
    }

    /// <summary>
    /// Inserts a new metric in O(1) constant time, instantly dropping the oldest node and recalculating the Running Sum.
    /// </summary>
    public void Add(double value)
    {
        // Subtract the oldest metric falling out of the window window
        if (_count == _capacity)
        {
            _sum -= _metrics[_head];
        }
        else
        {
            _count++;
        }

        _sum += value;
        _metrics[_head] = value;
        
        _head = (_head + 1) % _capacity;
    }

    /// <summary>
    /// O(1) instantaneous extraction mapping completely bypassing LINQ or standard loop arrays.
    /// </summary>
    public double MovingAverage => _count == 0 ? 0 : _sum / _count;

    /// <summary>
    /// Returns the oldest value in the current window.
    /// When the buffer is not yet full, entries start at index 0, so the oldest is _metrics[0].
    /// When full, _head points to the slot about to be overwritten — which is the current oldest entry.
    /// </summary>
    public double OldestValue => _count == 0 ? 0
        : _count < _capacity ? _metrics[0]
        : _metrics[_head];

    public bool IsFilled => _count == _capacity;
}
