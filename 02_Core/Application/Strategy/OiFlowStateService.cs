using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// [P1.6] Maintains a small per-symbol rolling buffer of (timestamp, OI, price)
/// snapshots and exposes the current <see cref="OiFlowDetector.FlowResult"/> at
/// configurable lookback windows. Thread-safe via a per-symbol lock; suitable
/// for direct injection into hot strategy paths (1 read = O(buffer) ≈ O(50)).
///
/// State is in-memory only — restart loses history; classification falls back to
/// Neutral until the buffer is rebuilt (typically a few minutes on live OKX OI
/// feed which pushes ~once per minute per active SWAP).
/// </summary>
public sealed class OiFlowStateService
{
    private readonly int _maxBufferPerSymbol;
    private readonly ConcurrentDictionary<string, RollingBuffer> _bySymbol = new(StringComparer.OrdinalIgnoreCase);

    public OiFlowStateService(int maxBufferPerSymbol = 480)
    {
        // 480 minutes = 8h of 1-minute snapshots — plenty for 1h/4h windows.
        _maxBufferPerSymbol = Math.Max(60, maxBufferPerSymbol);
    }

    /// <summary>Record a fresh OI snapshot. Price defaults to 0 when caller doesn't have it
    /// (the next price tick can update it via <see cref="UpdatePrice"/>).</summary>
    public void RecordOi(string symbol, decimal oi, DateTime timestamp, decimal price = 0m)
    {
        if (string.IsNullOrWhiteSpace(symbol) || oi <= 0m) return;
        var buf = _bySymbol.GetOrAdd(symbol, _ => new RollingBuffer(_maxBufferPerSymbol));
        buf.AddOi(oi, price, timestamp);
    }

    /// <summary>Update only the latest price (called on tick stream between OI pushes).</summary>
    public void UpdatePrice(string symbol, decimal price, DateTime timestamp)
    {
        if (string.IsNullOrWhiteSpace(symbol) || price <= 0m) return;
        if (!_bySymbol.TryGetValue(symbol, out var buf)) return;
        buf.UpdateLatestPrice(price, timestamp);
    }

    /// <summary>
    /// Classify the symbol's current flow regime over the requested lookback.
    /// Returns Neutral when fewer than 2 snapshots straddle the window.
    /// </summary>
    public OiFlowDetector.FlowResult ClassifyAt(string symbol, TimeSpan lookback)
    {
        if (!_bySymbol.TryGetValue(symbol, out var buf))
            return new OiFlowDetector.FlowResult(OiFlowDetector.FlowRegime.Neutral, 0, 0, 1.0);

        var (prev, now) = buf.GetWindowEdges(lookback);
        if (prev is null || now is null)
            return new OiFlowDetector.FlowResult(OiFlowDetector.FlowRegime.Neutral, 0, 0, 1.0);

        return OiFlowDetector.Classify(
            oiPrev: prev.Value.Oi,
            oiNow: now.Value.Oi,
            pricePrev: prev.Value.Price > 0 ? prev.Value.Price : now.Value.Price,
            priceNow: now.Value.Price > 0 ? now.Value.Price : prev.Value.Price);
    }

    /// <summary>Cascade-risk pre-arm — see <see cref="OiFlowDetector.DetectCascadeRisk"/>.</summary>
    public bool DetectCascadeRiskAt(string symbol, TimeSpan lookback, double fundingRateBps)
    {
        if (!_bySymbol.TryGetValue(symbol, out var buf)) return false;
        var (prev, now) = buf.GetWindowEdges(lookback);
        if (prev is null || now is null) return false;
        return OiFlowDetector.DetectCascadeRisk(prev.Value.Oi, now.Value.Oi, fundingRateBps);
    }

    /// <summary>Diagnostic: how many snapshots are buffered for the symbol.</summary>
    public int BufferDepth(string symbol) =>
        _bySymbol.TryGetValue(symbol, out var buf) ? buf.Count : 0;

    private readonly record struct Snapshot(decimal Oi, decimal Price, DateTime Timestamp);

    private sealed class RollingBuffer
    {
        private readonly int _maxSize;
        private readonly LinkedList<Snapshot> _items = new();
        private readonly object _gate = new();

        public RollingBuffer(int maxSize) { _maxSize = maxSize; }

        public int Count { get { lock (_gate) return _items.Count; } }

        public void AddOi(decimal oi, decimal price, DateTime ts)
        {
            lock (_gate)
            {
                // If the latest snapshot is from the same timestamp (same WS push),
                // overwrite to keep the buffer monotonic. Otherwise append.
                decimal carryPrice = 0m;
                if (_items.Last is { } lastNode)
                {
                    carryPrice = lastNode.Value.Price;
                    if (lastNode.Value.Timestamp == ts)
                        _items.RemoveLast();
                }
                _items.AddLast(new Snapshot(oi, price > 0m ? price : carryPrice, ts));
                while (_items.Count > _maxSize) _items.RemoveFirst();
            }
        }

        public void UpdateLatestPrice(decimal price, DateTime ts)
        {
            lock (_gate)
            {
                if (_items.Last is { } last)
                {
                    _items.RemoveLast();
                    _items.AddLast(new Snapshot(last.Value.Oi, price, ts));
                }
            }
        }

        public (Snapshot? prev, Snapshot? now) GetWindowEdges(TimeSpan lookback)
        {
            lock (_gate)
            {
                if (_items.Count < 2) return (null, null);
                var nowSnap = _items.Last!.Value;
                var target = nowSnap.Timestamp - lookback;
                Snapshot? prev = null;
                // Walk back to the closest snapshot at-or-before target.
                for (var node = _items.Last!; node is not null; node = node.Previous)
                {
                    if (node.Value.Timestamp <= target) { prev = node.Value; break; }
                }
                // If no snapshot is old enough, use the oldest available — caller can decide
                // whether the implied lookback is good enough.
                prev ??= _items.First!.Value;
                if (prev.Value.Timestamp == nowSnap.Timestamp) return (null, null);
                return (prev, nowSnap);
            }
        }
    }
}
