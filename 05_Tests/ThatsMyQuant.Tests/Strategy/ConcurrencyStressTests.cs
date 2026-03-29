using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

/// <summary>
/// Concurrent access stress tests: hammer singleton agents from many threads
/// to verify no data corruption or race conditions.
/// </summary>
public sealed class ConcurrencyStressTests
{
    // ─── DataFreshnessMonitor: concurrent RecordTick + CheckFreshness ────

    [Fact]
    public async Task DataFreshnessMonitor_SurvivesConcurrentAccess()
    {
        var monitor = new DataFreshnessMonitor(Substitute.For<ILogger<DataFreshnessMonitor>>());
        var symbols = new[] { "BTCUSDT", "ETHUSDT", "BNBUSDT", "SOLUSDT", "XRPUSDT" };
        var errors = new ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(() =>
        {
            try
            {
                var symbol = symbols[i % symbols.Length];
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // Rapid fire record + check
                for (int j = 0; j < 100; j++)
                {
                    monitor.RecordTick(symbol, now + j);
                    var (isFresh, _, _) = monitor.CheckFreshness(symbol);
                    // Just exercising the code path — freshness should not throw
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }));

        await Task.WhenAll(tasks);

        errors.Should().BeEmpty("no exceptions should occur during concurrent access");
    }

    [Fact]
    public async Task DataFreshnessMonitor_HasAnyStaleData_ThreadSafe()
    {
        var monitor = new DataFreshnessMonitor(Substitute.For<ILogger<DataFreshnessMonitor>>());
        var errors = new ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, 30).Select(i => Task.Run(() =>
        {
            try
            {
                monitor.RecordTick($"SYM{i}", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                monitor.HasAnyStaleData();
                monitor.GetAllStatus();
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }));

        await Task.WhenAll(tasks);
        errors.Should().BeEmpty();
    }

    // ─── TickRingBuffer: concurrent writes ───────────────────────────────

    [Fact]
    public async Task TickRingBuffer_DoesNotCorrupt_UnderConcurrentWrites()
    {
        var buffer = new TickRingBuffer(256);
        var errors = new ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, 20).Select(threadId => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 500; i++)
                {
                    buffer.Add(100.0 + threadId + i * 0.01);
                    var avg = buffer.MovingAverage;
                    // Verify average is in a sane range (not NaN, not corrupted)
                    if (double.IsNaN(avg) || double.IsInfinity(avg))
                        throw new Exception($"Corrupted MovingAverage: {avg}");
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }));

        await Task.WhenAll(tasks);
        errors.Should().BeEmpty("ring buffer should not produce NaN/Infinity under concurrent writes");
    }

    // ─── InferenceAgent: concurrent predictions ──────────────────────────

    [Fact]
    public void InferenceAgent_ReturnsInvalid_WhenNoModel()
    {
        // Without a model file, the agent returns IsValid=false gracefully
        var agent = new InferenceAgent("nonexistent_model.zip");
        var results = new ConcurrentBag<PredictionResult>();

        Parallel.For(0, 50, _ =>
        {
            var result = agent.PredictNextReturn(new float[] { 0.5f, 1.0f, 0.1f, 0.0f, 1.0f });
            results.Add(result);
        });

        results.Should().AllSatisfy(r => r.IsValid.Should().BeFalse());
    }

    [Fact]
    public void InferenceAgent_HandlesNullFeatures_Gracefully()
    {
        var agent = new InferenceAgent("nonexistent_model.zip");

        var result = agent.PredictNextReturn(null!);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void InferenceAgent_HandlesWrongFeatureLength_Gracefully()
    {
        var agent = new InferenceAgent("nonexistent_model.zip");

        var result = agent.PredictNextReturn(new float[] { 1f, 2f, 3f });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void InferenceAgent_HandlesNaNFeatures_Gracefully()
    {
        var agent = new InferenceAgent("nonexistent_model.zip");

        var result = agent.PredictNextReturn(new float[] { float.NaN, 1f, 0f, 0f, 1f });

        result.IsValid.Should().BeFalse();
    }

    // ─── AgentCoordinator: concurrent full pipeline calls ────────────────

    [Fact]
    public async Task AgentCoordinator_SurvivesConcurrentPipelineRuns()
    {
        var logger = Substitute.For<ILogger<AgentCoordinator>>();
        var coordinator = new AgentCoordinator(
            Array.Empty<IFoundationAgent>(),
            Array.Empty<IPatternAgent>(),
            Array.Empty<IStrategyAgent>(),
            Array.Empty<IRiskGuardAgent>(),
            Array.Empty<IMetaLearningAgent>(),
            logger);

        var errors = new ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, 30).Select(i => Task.Run(async () =>
        {
            try
            {
                var result = await coordinator.RunFullPipelineAsync(
                    new Signal { Symbol = $"SYM{i}" },
                    new List<MarketTick>(),
                    new List<Candle>(),
                    MarketRegime.Ranging,
                    new List<Trade>(),
                    new List<Trade>());

                result.Foundation.Should().Be(FoundationSnapshot.Empty);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }));

        await Task.WhenAll(tasks);
        errors.Should().BeEmpty();
    }

    // ─── EdgeScoreService-style concurrent dictionary access ─────────────

    [Fact]
    public async Task ConcurrentDictionary_HandlesRapidGetOrAdd()
    {
        var dict = new ConcurrentDictionary<string, int>();
        var errors = new ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, 100).Select(i => Task.Run(() =>
        {
            try
            {
                for (int j = 0; j < 100; j++)
                {
                    var key = $"key_{j % 10}";
                    dict.AddOrUpdate(key, 1, (_, v) => v + 1);
                    dict.TryGetValue(key, out _);
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }));

        await Task.WhenAll(tasks);
        errors.Should().BeEmpty();
        dict.Values.Sum().Should().BeGreaterThan(0);
    }
}
