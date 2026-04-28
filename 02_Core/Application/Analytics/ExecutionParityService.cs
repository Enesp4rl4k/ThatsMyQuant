using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Application.Analytics;

/// <summary>
/// Tracks paper-vs-live (or modelled-vs-actual) fill parity in real time.
/// For every paper/simulated fill we expect a matching live fill within a tolerance
/// window. Divergence in price (slippage) or size (partial fill) is classified and
/// reported — lets oncall catch broker degradation, venue routing issues, or
/// cost-model drift before they silently burn capital.
///
/// Usage:
///   1. On simulated fill: RecordPaperFill(correlationId, price, qty)
///   2. On live fill     : RecordLiveFill(correlationId, price, qty, ts)
///   3. GetParityReport() aggregates divergences into TCA-friendly metrics.
/// </summary>
public sealed class ExecutionParityService
{
    private readonly ILogger<ExecutionParityService> _logger;
    private readonly ConcurrentDictionary<string, PaperFill> _pendingPaper = new();
    private readonly ConcurrentBag<ParityObservation> _observations = new();
    private readonly decimal _priceTolerancePct;
    private readonly decimal _sizeTolerancePct;

    public ExecutionParityService(
        ILogger<ExecutionParityService> logger,
        decimal priceTolerancePct = 0.05m,
        decimal sizeTolerancePct = 0.02m)
    {
        _logger = logger;
        _priceTolerancePct = priceTolerancePct;
        _sizeTolerancePct = sizeTolerancePct;
    }

    public void RecordPaperFill(string correlationId, decimal expectedPrice, decimal expectedQty, DateTime utcTs)
    {
        _pendingPaper[correlationId] = new PaperFill(expectedPrice, expectedQty, utcTs);
    }

    public void RecordLiveFill(string correlationId, decimal livePrice, decimal liveQty, DateTime utcTs)
    {
        if (!_pendingPaper.TryRemove(correlationId, out var paper))
        {
            _logger.LogWarning("ParityService: live fill {Id} has no matching paper fill — orphan", correlationId);
            return;
        }

        decimal priceDiffPct = paper.Price > 0
            ? Math.Abs((livePrice - paper.Price) / paper.Price) * 100m
            : 0m;
        decimal qtyDiffPct = paper.Qty > 0
            ? Math.Abs((liveQty - paper.Qty) / paper.Qty) * 100m
            : 0m;

        var classification = ClassifyDivergence(priceDiffPct, qtyDiffPct);
        var obs = new ParityObservation(
            CorrelationId: correlationId,
            PaperPrice: paper.Price,
            LivePrice: livePrice,
            PaperQty: paper.Qty,
            LiveQty: liveQty,
            PriceDivergencePct: priceDiffPct,
            SizeDivergencePct: qtyDiffPct,
            LatencyMs: (utcTs - paper.Ts).TotalMilliseconds,
            Classification: classification);
        _observations.Add(obs);

        if (classification != ParityClassification.Acceptable)
            _logger.LogWarning(
                "Parity divergence {Class} | {Id} price Δ={P:F3}% qty Δ={Q:F3}% lat={L:F0}ms",
                classification, correlationId, priceDiffPct, qtyDiffPct, obs.LatencyMs);
    }

    public ParityReport GetParityReport()
    {
        var snapshot = _observations.ToArray();
        if (snapshot.Length == 0)
            return new ParityReport(0, 0, 0, 0, 0, 0, 0, new List<ParityObservation>());

        int acceptable = 0, priceDiv = 0, sizeDiv = 0, orphan = 0;
        double priceSum = 0, sizeSum = 0, latSum = 0;
        foreach (var o in snapshot)
        {
            switch (o.Classification)
            {
                case ParityClassification.Acceptable: acceptable++; break;
                case ParityClassification.PriceDivergence: priceDiv++; break;
                case ParityClassification.SizeDivergence: sizeDiv++; break;
                case ParityClassification.BothDiverged: priceDiv++; sizeDiv++; break;
            }
            priceSum += (double)o.PriceDivergencePct;
            sizeSum += (double)o.SizeDivergencePct;
            latSum += o.LatencyMs;
        }
        orphan = _pendingPaper.Count;

        return new ParityReport(
            TotalComparisons: snapshot.Length,
            AcceptableCount: acceptable,
            PriceDivergenceCount: priceDiv,
            SizeDivergenceCount: sizeDiv,
            OrphanPaperFillCount: orphan,
            AveragePriceDivergencePct: priceSum / snapshot.Length,
            AverageLatencyMs: latSum / snapshot.Length,
            RecentObservations: snapshot.TakeLast(50).ToList());
    }

    private ParityClassification ClassifyDivergence(decimal priceDiffPct, decimal qtyDiffPct)
    {
        bool priceBad = priceDiffPct > _priceTolerancePct;
        bool sizeBad = qtyDiffPct > _sizeTolerancePct;
        return (priceBad, sizeBad) switch
        {
            (true, true) => ParityClassification.BothDiverged,
            (true, false) => ParityClassification.PriceDivergence,
            (false, true) => ParityClassification.SizeDivergence,
            _ => ParityClassification.Acceptable
        };
    }

    private readonly record struct PaperFill(decimal Price, decimal Qty, DateTime Ts);
}

public enum ParityClassification
{
    Acceptable,
    PriceDivergence,
    SizeDivergence,
    BothDiverged
}

public sealed record ParityObservation(
    string CorrelationId,
    decimal PaperPrice,
    decimal LivePrice,
    decimal PaperQty,
    decimal LiveQty,
    decimal PriceDivergencePct,
    decimal SizeDivergencePct,
    double LatencyMs,
    ParityClassification Classification);

public sealed record ParityReport(
    int TotalComparisons,
    int AcceptableCount,
    int PriceDivergenceCount,
    int SizeDivergenceCount,
    int OrphanPaperFillCount,
    double AveragePriceDivergencePct,
    double AverageLatencyMs,
    List<ParityObservation> RecentObservations);
