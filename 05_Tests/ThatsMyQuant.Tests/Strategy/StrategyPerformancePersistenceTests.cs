using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public sealed class StrategyPerformancePersistenceTests : IDisposable
{
    private readonly string _tempDir;

    public StrategyPerformancePersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tmq-perf-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static StrategyBucketPerformanceSnapshot CreateSampleBucket(string symbol = "BTCUSDT") =>
        new()
        {
            Symbol = symbol,
            StrategyTag = "Momentum",
            RegimeTag = "Trending",
            SessionTag = "Asia",
            Trades = 42,
            WinRate = 0.65,
            ProfitFactor = 2.1,
            ExpectedValue = 12.5m,
            TotalPnL = 525m,
            AverageEdgeScore = 0.78m,
            HighEdgeTrades = 10,
            HighEdgeWinRate = 0.80,
            HighEdgeProfitFactor = 3.0,
            HighEdgeExpectedValue = 25m,
            HighEdgeTotalPnL = 250m,
            AverageFinalAllocatorBias = 1.05m,
            BoostedAllocatorTrades = 5,
            BoostedAllocatorWinRate = 0.70,
            BoostedAllocatorProfitFactor = 2.5,
            BoostedAllocatorExpectedValue = 15m,
            TrimmedAllocatorTrades = 3,
            TrimmedAllocatorWinRate = 0.33,
            TrimmedAllocatorProfitFactor = 0.8,
            TrimmedAllocatorExpectedValue = -2m,
            AverageVenueExecutionBias = 0.98m,
            BoostedVenueExecutionTrades = 4,
            BoostedVenueExecutionWinRate = 0.75,
            BoostedVenueExecutionProfitFactor = 2.8,
            BoostedVenueExecutionExpectedValue = 20m,
            TrimmedVenueExecutionTrades = 2,
            TrimmedVenueExecutionWinRate = 0.50,
            TrimmedVenueExecutionProfitFactor = 1.0,
            TrimmedVenueExecutionExpectedValue = 0m,
            AverageEntrySlippagePercent = 0.02m,
            AverageExecutionLatencyMs = 1.5,
            DominantMlModelVersion = "v3.2.1",
            FeatureDriftTrades = 8,
            CriticalFeatureDriftTrades = 2,
            SizeTrimmedByDriftTrades = 1,
            FeatureDriftRate = 0.19,
            CriticalFeatureDriftRate = 0.05,
            SizeTrimmedByDriftRate = 0.02,
            AverageFeatureDriftMaxZScore = 1.8m,
            BacktestPass = true,
            PaperPass = true,
            LiveProbePass = false,
            ScaledLiveEligible = false,
            PromotionReady = false,
            PromotionStage = "Paper",
            RecordedAtUtc = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc)
        };

    private static ExitPolicyPerformanceSnapshot CreateSampleExitPolicy() =>
        new()
        {
            Symbol = "ETHUSDT",
            StrategyTag = "Scalping",
            RegimeTag = "MeanReverting",
            SessionTag = "London",
            ExitPolicyTag = "TrailingStop",
            ExecutionMode = "LIVE",
            Trades = 100,
            WinRate = 0.55,
            ProfitFactor = 1.4,
            ExpectedValue = 3.2m,
            TotalPnL = 320m,
            RecordedAtUtc = new DateTime(2025, 7, 1, 8, 30, 0, DateTimeKind.Utc)
        };

    // ── StrategyPerformanceState.Clone deep-copy ─────────────────────

    [Fact]
    public void StrategyPerformanceState_Clone_ShouldDeepCopy()
    {
        var original = new StrategyPerformanceState
        {
            BacktestBuckets = new List<StrategyBucketPerformanceSnapshot> { CreateSampleBucket("BTCUSDT") },
            LiveBuckets = new List<StrategyBucketPerformanceSnapshot> { CreateSampleBucket("ETHUSDT") },
            PaperBuckets = new List<StrategyBucketPerformanceSnapshot> { CreateSampleBucket("SOLUSDT") },
            ExitPolicyBuckets = new List<ExitPolicyPerformanceSnapshot> { CreateSampleExitPolicy() },
            RecordedAtUtc = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc)
        };

        var clone = original.Clone();

        // Values should match
        clone.RecordedAtUtc.Should().Be(original.RecordedAtUtc);
        clone.BacktestBuckets.Should().HaveCount(1);
        clone.BacktestBuckets[0].Symbol.Should().Be("BTCUSDT");
        clone.LiveBuckets.Should().HaveCount(1);
        clone.LiveBuckets[0].Symbol.Should().Be("ETHUSDT");
        clone.PaperBuckets.Should().HaveCount(1);
        clone.PaperBuckets[0].Symbol.Should().Be("SOLUSDT");
        clone.ExitPolicyBuckets.Should().HaveCount(1);
        clone.ExitPolicyBuckets[0].Symbol.Should().Be("ETHUSDT");

        // Lists should be different instances (deep copy)
        clone.BacktestBuckets.Should().NotBeSameAs(original.BacktestBuckets);
        clone.LiveBuckets.Should().NotBeSameAs(original.LiveBuckets);
        clone.PaperBuckets.Should().NotBeSameAs(original.PaperBuckets);
        clone.ExitPolicyBuckets.Should().NotBeSameAs(original.ExitPolicyBuckets);

        // Individual snapshot objects should be different instances
        clone.BacktestBuckets[0].Should().NotBeSameAs(original.BacktestBuckets[0]);
        clone.ExitPolicyBuckets[0].Should().NotBeSameAs(original.ExitPolicyBuckets[0]);

        // Mutating clone should not affect original
        clone.BacktestBuckets[0].Symbol = "MUTATED";
        original.BacktestBuckets[0].Symbol.Should().Be("BTCUSDT");
    }

    // ── StrategyBucketPerformanceSnapshot.Clone ──────────────────────

    [Fact]
    public void StrategyBucketPerformanceSnapshot_Clone_ShouldCopyAllFields()
    {
        var original = CreateSampleBucket();

        var clone = original.Clone();

        clone.Should().NotBeSameAs(original);
        clone.Symbol.Should().Be(original.Symbol);
        clone.StrategyTag.Should().Be(original.StrategyTag);
        clone.RegimeTag.Should().Be(original.RegimeTag);
        clone.SessionTag.Should().Be(original.SessionTag);
        clone.Trades.Should().Be(original.Trades);
        clone.WinRate.Should().Be(original.WinRate);
        clone.ProfitFactor.Should().Be(original.ProfitFactor);
        clone.ExpectedValue.Should().Be(original.ExpectedValue);
        clone.TotalPnL.Should().Be(original.TotalPnL);
        clone.AverageEdgeScore.Should().Be(original.AverageEdgeScore);
        clone.HighEdgeTrades.Should().Be(original.HighEdgeTrades);
        clone.HighEdgeWinRate.Should().Be(original.HighEdgeWinRate);
        clone.HighEdgeProfitFactor.Should().Be(original.HighEdgeProfitFactor);
        clone.HighEdgeExpectedValue.Should().Be(original.HighEdgeExpectedValue);
        clone.HighEdgeTotalPnL.Should().Be(original.HighEdgeTotalPnL);
        clone.AverageFinalAllocatorBias.Should().Be(original.AverageFinalAllocatorBias);
        clone.BoostedAllocatorTrades.Should().Be(original.BoostedAllocatorTrades);
        clone.BoostedAllocatorWinRate.Should().Be(original.BoostedAllocatorWinRate);
        clone.BoostedAllocatorProfitFactor.Should().Be(original.BoostedAllocatorProfitFactor);
        clone.BoostedAllocatorExpectedValue.Should().Be(original.BoostedAllocatorExpectedValue);
        clone.TrimmedAllocatorTrades.Should().Be(original.TrimmedAllocatorTrades);
        clone.TrimmedAllocatorWinRate.Should().Be(original.TrimmedAllocatorWinRate);
        clone.TrimmedAllocatorProfitFactor.Should().Be(original.TrimmedAllocatorProfitFactor);
        clone.TrimmedAllocatorExpectedValue.Should().Be(original.TrimmedAllocatorExpectedValue);
        clone.AverageVenueExecutionBias.Should().Be(original.AverageVenueExecutionBias);
        clone.BoostedVenueExecutionTrades.Should().Be(original.BoostedVenueExecutionTrades);
        clone.BoostedVenueExecutionWinRate.Should().Be(original.BoostedVenueExecutionWinRate);
        clone.BoostedVenueExecutionProfitFactor.Should().Be(original.BoostedVenueExecutionProfitFactor);
        clone.BoostedVenueExecutionExpectedValue.Should().Be(original.BoostedVenueExecutionExpectedValue);
        clone.TrimmedVenueExecutionTrades.Should().Be(original.TrimmedVenueExecutionTrades);
        clone.TrimmedVenueExecutionWinRate.Should().Be(original.TrimmedVenueExecutionWinRate);
        clone.TrimmedVenueExecutionProfitFactor.Should().Be(original.TrimmedVenueExecutionProfitFactor);
        clone.TrimmedVenueExecutionExpectedValue.Should().Be(original.TrimmedVenueExecutionExpectedValue);
        clone.AverageEntrySlippagePercent.Should().Be(original.AverageEntrySlippagePercent);
        clone.AverageExecutionLatencyMs.Should().Be(original.AverageExecutionLatencyMs);
        clone.DominantMlModelVersion.Should().Be(original.DominantMlModelVersion);
        clone.FeatureDriftTrades.Should().Be(original.FeatureDriftTrades);
        clone.CriticalFeatureDriftTrades.Should().Be(original.CriticalFeatureDriftTrades);
        clone.SizeTrimmedByDriftTrades.Should().Be(original.SizeTrimmedByDriftTrades);
        clone.FeatureDriftRate.Should().Be(original.FeatureDriftRate);
        clone.CriticalFeatureDriftRate.Should().Be(original.CriticalFeatureDriftRate);
        clone.SizeTrimmedByDriftRate.Should().Be(original.SizeTrimmedByDriftRate);
        clone.AverageFeatureDriftMaxZScore.Should().Be(original.AverageFeatureDriftMaxZScore);
        clone.BacktestPass.Should().Be(original.BacktestPass);
        clone.PaperPass.Should().Be(original.PaperPass);
        clone.LiveProbePass.Should().Be(original.LiveProbePass);
        clone.ScaledLiveEligible.Should().Be(original.ScaledLiveEligible);
        clone.PromotionReady.Should().Be(original.PromotionReady);
        clone.PromotionStage.Should().Be(original.PromotionStage);
        clone.RecordedAtUtc.Should().Be(original.RecordedAtUtc);
    }

    // ── ExitPolicyPerformanceSnapshot.Clone ──────────────────────────

    [Fact]
    public void ExitPolicyPerformanceSnapshot_Clone_ShouldCopyAllFields()
    {
        var original = CreateSampleExitPolicy();

        var clone = original.Clone();

        clone.Should().NotBeSameAs(original);
        clone.Symbol.Should().Be(original.Symbol);
        clone.StrategyTag.Should().Be(original.StrategyTag);
        clone.RegimeTag.Should().Be(original.RegimeTag);
        clone.SessionTag.Should().Be(original.SessionTag);
        clone.ExitPolicyTag.Should().Be(original.ExitPolicyTag);
        clone.ExecutionMode.Should().Be(original.ExecutionMode);
        clone.Trades.Should().Be(original.Trades);
        clone.WinRate.Should().Be(original.WinRate);
        clone.ProfitFactor.Should().Be(original.ProfitFactor);
        clone.ExpectedValue.Should().Be(original.ExpectedValue);
        clone.TotalPnL.Should().Be(original.TotalPnL);
        clone.RecordedAtUtc.Should().Be(original.RecordedAtUtc);
    }

    // ── JsonStrategyPerformanceStore round-trip ──────────────────────

    [Fact]
    public async Task JsonStrategyPerformanceStore_SaveAndLoad_ShouldRoundTrip()
    {
        // Arrange: create store pointing at temp directory
        var filePath = Path.Combine(_tempDir, "engine-strategy-performance.json");
        var store = CreateStoreWithCustomPath(filePath);

        var state = new StrategyPerformanceState
        {
            BacktestBuckets = new List<StrategyBucketPerformanceSnapshot>
            {
                CreateSampleBucket("BTCUSDT"),
                CreateSampleBucket("ETHUSDT")
            },
            LiveBuckets = new List<StrategyBucketPerformanceSnapshot>
            {
                CreateSampleBucket("SOLUSDT")
            },
            PaperBuckets = new List<StrategyBucketPerformanceSnapshot>(),
            ExitPolicyBuckets = new List<ExitPolicyPerformanceSnapshot>
            {
                CreateSampleExitPolicy()
            },
            RecordedAtUtc = new DateTime(2025, 8, 10, 14, 30, 0, DateTimeKind.Utc)
        };

        // Act
        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();

        // Assert: file was written
        File.Exists(filePath).Should().BeTrue();

        // Assert: round-tripped values match
        loaded.RecordedAtUtc.Should().Be(state.RecordedAtUtc);
        loaded.BacktestBuckets.Should().HaveCount(2);
        loaded.BacktestBuckets[0].Symbol.Should().Be("BTCUSDT");
        loaded.BacktestBuckets[1].Symbol.Should().Be("ETHUSDT");
        loaded.BacktestBuckets[0].Trades.Should().Be(42);
        loaded.BacktestBuckets[0].WinRate.Should().Be(0.65);
        loaded.BacktestBuckets[0].ProfitFactor.Should().Be(2.1);
        loaded.BacktestBuckets[0].ExpectedValue.Should().Be(12.5m);
        loaded.BacktestBuckets[0].TotalPnL.Should().Be(525m);
        loaded.BacktestBuckets[0].PromotionStage.Should().Be("Paper");

        loaded.LiveBuckets.Should().HaveCount(1);
        loaded.LiveBuckets[0].Symbol.Should().Be("SOLUSDT");

        loaded.PaperBuckets.Should().BeEmpty();

        loaded.ExitPolicyBuckets.Should().HaveCount(1);
        loaded.ExitPolicyBuckets[0].Symbol.Should().Be("ETHUSDT");
        loaded.ExitPolicyBuckets[0].ExitPolicyTag.Should().Be("TrailingStop");
        loaded.ExitPolicyBuckets[0].ExecutionMode.Should().Be("LIVE");
        loaded.ExitPolicyBuckets[0].Trades.Should().Be(100);
        loaded.ExitPolicyBuckets[0].TotalPnL.Should().Be(320m);
    }

    [Fact]
    public async Task JsonStrategyPerformanceStore_LoadMissingFile_ShouldReturnDefaultState()
    {
        var filePath = Path.Combine(_tempDir, "nonexistent.json");
        var store = CreateStoreWithCustomPath(filePath);

        var loaded = await store.LoadAsync();

        loaded.Should().NotBeNull();
        loaded.BacktestBuckets.Should().BeEmpty();
        loaded.LiveBuckets.Should().BeEmpty();
        loaded.PaperBuckets.Should().BeEmpty();
        loaded.ExitPolicyBuckets.Should().BeEmpty();
    }

    /// <summary>
    /// Creates a <see cref="JsonStrategyPerformanceStore"/> with a custom file path
    /// by setting the private <c>_filePath</c> field via reflection.
    /// This avoids writing to the real AppData location during tests.
    /// </summary>
    private static JsonStrategyPerformanceStore CreateStoreWithCustomPath(string filePath)
    {
        var store = new JsonStrategyPerformanceStore();

        var field = typeof(JsonStrategyPerformanceStore)
            .GetField("_filePath", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find _filePath field.");

        field.SetValue(store, filePath);
        return store;
    }
}
