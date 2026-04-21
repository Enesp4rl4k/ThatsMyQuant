using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Persistence;
using ThatsMyQuant.Infrastructure.Repositories;
using ThatsMyQuant.Infrastructure.Services;
using ThatsMyQuant.Tests.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace ThatsMyQuant.Tests.Performance;

public sealed class Phase8APerfBenchmarksTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _workspacePath;
    private readonly string _modelPath;

    public Phase8APerfBenchmarksTests(ITestOutputHelper output)
    {
        _output = output;
        _workspacePath = Path.Combine(Path.GetTempPath(), "tmq-phase8a-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspacePath);
        _modelPath = Path.Combine(_workspacePath, "PredictiveAlphaModel.zip");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task Phase8A_Baseline_ShouldEmitMetrics_WhenEnabled()
    {
        if (!IsPerfEnabled())
        {
            _output.WriteLine("Phase 8A perf baseline is disabled. Set TMQ_RUN_PERF_BASELINE=1 to enable.");
            return;
        }

        var options = PerfOptions.FromEnvironment();
        var results = new List<PerfResult>
        {
            await BenchmarkSwarmOrchestratorAsync(options),
            await BenchmarkAgentCoordinatorPipelineAsync(options),
            await BenchmarkBinanceDepthParsingAsync(options)
        };

        results.AddRange(await BenchmarkSignalPersistenceAsync(options));

        _output.WriteLine($"Warmup iterations: {options.WarmupIterations}");
        _output.WriteLine($"Measured iterations: {options.MeasureIterations}");
        _output.WriteLine($"Parser iterations: {options.ParserIterations}");
        _output.WriteLine($"Persistence iterations: {options.PersistenceIterations}");
        _output.WriteLine("");
        _output.WriteLine("| Benchmark | Iterations | Mean us | P50 us | P95 us | P99 us | Ops/s | Alloc B/op | Gen0 | Gen1 | Gen2 |");
        _output.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");

        foreach (var result in results)
        {
            _output.WriteLine(
                $"| {result.Name} | {result.Iterations} | {result.MeanMicroseconds:F2} | {result.P50Microseconds:F2} | {result.P95Microseconds:F2} | {result.P99Microseconds:F2} | {result.OperationsPerSecond:F2} | {result.AllocatedBytesPerOperation:F2} | {result.Gen0Collections} | {result.Gen1Collections} | {result.Gen2Collections} |");
        }

        var highestAllocation = results.OrderByDescending(r => r.AllocatedBytesPerOperation).First();
        var highestP99 = results.OrderByDescending(r => r.P99Microseconds).First();

        _output.WriteLine("");
        _output.WriteLine($"Highest allocation/op: {highestAllocation.Name} ({highestAllocation.AllocatedBytesPerOperation:F2} B/op)");
        _output.WriteLine($"Highest p99 latency: {highestP99.Name} ({highestP99.P99Microseconds:F2} us)");

        results.Should().HaveCount(5);
        results.Should().OnlyContain(result => result.Iterations > 0);
    }

    private async Task<PerfResult> BenchmarkSwarmOrchestratorAsync(PerfOptions options)
    {
        var orchestrator = CreateSwarmOrchestrator();
        var candles = CreateCandles(96);
        var foundation = CreateFoundationSnapshot();
        var patterns = CreatePatternSnapshot();

        return await MeasureAsync(
            "SwarmOrchestrator.Handle",
            options.WarmupIterations,
            options.MeasureIterations,
            async iteration =>
            {
                var request = CreateSwarmRequest(iteration, candles, foundation, patterns);
                var result = await orchestrator.Handle(request, CancellationToken.None);
                result.RejectionFlags.Should().NotHaveFlag(SignalReasonFlags.SystemError);
            });
    }

    private async Task<PerfResult> BenchmarkAgentCoordinatorPipelineAsync(PerfOptions options)
    {
        var coordinator = CreateAgentCoordinator();
        var signal = CreateSignal(0);
        var ticks = CreateTicks(signal.Symbol, 40);
        var candles = CreateCandles(120);
        var trades = CreateRecentTrades(signal.Symbol, 48);

        return await MeasureAsync(
            "AgentCoordinator.RunFullPipelineAsync",
            options.WarmupIterations,
            options.MeasureIterations,
            async _ =>
            {
                var snapshot = await coordinator.RunFullPipelineAsync(
                    signal,
                    ticks,
                    candles,
                    MarketRegime.Trending,
                    trades,
                    trades,
                    CancellationToken.None);

                snapshot.Votes.Should().NotBeEmpty();
            });
    }

    private async Task<PerfResult> BenchmarkBinanceDepthParsingAsync(PerfOptions options)
    {
        var service = new BinanceWebSocketService(NullLogger<BinanceWebSocketService>.Instance);
        OrderBookSnapshot latest = default;
        service.OnOrderBookUpdated += snapshot => latest = snapshot;

        var payload = BuildDepthMessage();
        var parser = CreatePrivateMethodDelegate<Action<BinanceWebSocketService, ReadOnlyMemory<byte>>>(
            typeof(BinanceWebSocketService),
            "ParseMessageCore");

        return await MeasureSyncAsync(
            "BinanceWebSocketService.DepthParse",
            options.WarmupIterations,
            options.ParserIterations,
            _ =>
            {
                parser(service, payload);
                latest.Bids.Length.Should().BeGreaterThan(0);
                latest.Asks.Length.Should().BeGreaterThan(0);
            });
    }

    private async Task<IReadOnlyList<PerfResult>> BenchmarkSignalPersistenceAsync(PerfOptions options)
    {
        var databasePath = Path.Combine(_workspacePath, "phase8a-signals.db");
        await using (var setup = CreateDbContext(databasePath))
        {
            await setup.Database.EnsureDeletedAsync();
            await setup.Database.EnsureCreatedAsync();
            setup.Signals.AddRange(Enumerable.Range(0, 256).Select(CreatePersistedSignal));
            await setup.SaveChangesAsync();
        }

        var addResult = await MeasureAsync(
            "SignalRepository.AddAsync",
            5,
            options.PersistenceIterations,
            async iteration =>
            {
                await using var db = CreateDbContext(databasePath);
                var repo = new SignalRepository(db, new SignalWriterChannel());
                await repo.AddAsync(CreatePersistedSignal(10_000 + iteration));
            });

        var readResult = await MeasureAsync(
            "SignalRepository.GetRecentAsync",
            5,
            options.PersistenceIterations,
            async _ =>
            {
                await using var db = CreateDbContext(databasePath);
                var repo = new SignalRepository(db);
                var signals = await repo.GetRecentAsync(50);
                signals.Should().NotBeEmpty();
            });

        return [addResult, readResult];
    }

    private SwarmOrchestrator CreateSwarmOrchestrator()
    {
        EnsureModelBuilt();

        var sentimentAgent = new SentimentAgent(
            new ConstantSentimentService(),
            new NewsFeedSim(NullLogger<NewsFeedSim>.Instance),
            NullLogger<SentimentAgent>.Instance);

        var inferenceAgent = new InferenceAgent(_modelPath);
        var featureAgent = new FeatureEngineeringAgent();
        for (var i = 0; i < 180; i++)
        {
            featureAgent.ProcessTickNatively(100 + i * 0.12);
        }

        return new SwarmOrchestrator(
            new RiskAgent(NullLogger<RiskAgent>.Instance),
            sentimentAgent,
            inferenceAgent,
            featureAgent,
            new StaticPortfolioStateCache(CreateRecentTrades("BTCUSDT", 96)),
            new OFIExecutionAgent(NullLogger<OFIExecutionAgent>.Instance, new StaticExchangeInfoProvider()),
            new StaticCoordinator(),
            NullLogger<SwarmOrchestrator>.Instance);
    }

    private static AgentCoordinator CreateAgentCoordinator()
    {
        IFoundationAgent[] foundationAgents =
        [
            new ConstantFoundationAgent("foundation.ofi", 0.28d, 0.61d, 101.1m, 0.7m, 3.2d, 100.8m, 1.2m),
            new ConstantFoundationAgent("foundation.vwap", 0.34d, 0.66d, 101.4m, 0.8m, 3.6d, 0m, 2.5m),
            new ConstantFoundationAgent("foundation.wall", 0.31d, 0.63d, 101.3m, 0.75m, 3.4d, 100.9m, 1.8m)
        ];

        IPatternAgent[] patternAgents =
        [
            new ConstantPatternAgent("pattern.bb", 0.72d, true, 100.5m, 0.38d, IchimokuState.AboveCloud, 1.4d, "bb-squeeze", "momentum"),
            new ConstantPatternAgent("pattern.fib", 0d, false, 101.2m, 0d, IchimokuState.Unknown, 0d, "fib"),
            new ConstantPatternAgent("pattern.ichi", 0.64d, false, 0m, 0.22d, IchimokuState.TKCrossBullish, 1.1d, "trend")
        ];

        IStrategyAgent[] strategyAgents =
        [
            new ConstantStrategyAgent("TrendConfluence", 5, true, VoteOutcome.Approve, 18f),
            new ConstantStrategyAgent("MicroBreakout", 4, true, VoteOutcome.Approve, 12f),
            new ConstantStrategyAgent("MeanReversion", 2, true, VoteOutcome.Reject, -4f),
            new ConstantStrategyAgent("LiquiditySweep", 3, false, VoteOutcome.Abstain, 0f),
            new ConstantStrategyAgent("VolumeImpulse", 3, true, VoteOutcome.Approve, 9f),
            new ConstantStrategyAgent("VWAPReclaim", 2, true, VoteOutcome.Abstain, 0f)
        ];

        IRiskGuardAgent[] riskGuards =
        [
            new ConstantRiskGuardAgent("risk.drawdown", 1, RiskGuardResult.Pass("risk.drawdown")),
            new ConstantRiskGuardAgent("risk.liquidity", 2, new RiskGuardResult { Approved = true, SizeMultiplier = 0.92m, Reason = "Scaled for benchmark" })
        ];

        IMetaLearningAgent[] metaAgents =
        [
            new ConstantMetaLearningAgent("meta.weights", new MetaAdaptation
            {
                ConfidenceScalar = 1.05f,
                RecommendedRegimeBias = 1.01f,
                StrategyWeights = new Dictionary<string, float>
                {
                    ["TrendConfluence"] = 1.12f,
                    ["MicroBreakout"] = 1.08f,
                    ["VolumeImpulse"] = 1.04f
                }
            }),
            new ConstantMetaLearningAgent("meta.regime", new MetaAdaptation
            {
                ConfidenceScalar = 1.02f,
                RecommendedRegimeBias = 1.03f,
                StrategyWeights = new Dictionary<string, float>
                {
                    ["MeanReversion"] = 0.95f,
                    ["VWAPReclaim"] = 1.01f
                }
            })
        ];

        return new AgentCoordinator(
            foundationAgents,
            patternAgents,
            strategyAgents,
            riskGuards,
            metaAgents,
            NullLogger<AgentCoordinator>.Instance);
    }

    private static SwarmNegotiationRequest CreateSwarmRequest(
        int iteration,
        IReadOnlyList<Candle> candles,
        FoundationSnapshot foundation,
        PatternSnapshot patterns)
        => new()
        {
            Signal = CreateSignal(iteration),
            InitialDecision = new TradeDecision
            {
                ShouldTrade = true,
                Side = TradeSide.Buy,
                Confidence = 78,
                StopLoss = 98.4m,
                TakeProfit = 103.2m,
                PositionSizePercent = 0.028m
            },
            Risk = RiskParameters.Default,
            Candles = candles,
            Foundation = foundation,
            Patterns = patterns,
            Regime = MarketRegime.Trending
        };

    private static Signal CreateSignal(int iteration)
        => new()
        {
            Id = Guid.NewGuid(),
            Symbol = "BTCUSDT",
            Side = TradeSide.Buy,
            Price = 100m + (iteration % 5) * 0.05m,
            Volume24h = 5_000_000m,
            Source = "Phase8A",
            Timestamp = DateTime.UtcNow,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        };

    private static List<Candle> CreateCandles(int count)
        => Enumerable.Range(0, count)
            .Select(i => new Candle
            {
                Timestamp = DateTime.UtcNow.AddMinutes(-count + i),
                Open = 100m + i * 0.08m,
                High = 101m + i * 0.08m,
                Low = 99m + i * 0.08m,
                Close = 100.4m + i * 0.08m,
                Volume = 1_200_000m + i * 500m
            })
            .ToList();

    private static List<MarketTick> CreateTicks(string symbol, int count)
        => Enumerable.Range(0, count)
            .Select(i => new MarketTick(
                symbol,
                100m + i * 0.03m,
                1.5m + i * 0.02m,
                1_710_000_000_000 + i * 100,
                i % 2 == 0))
            .ToList();

    private static List<Trade> CreateRecentTrades(string symbol, int count)
        => Enumerable.Range(0, count)
            .Select(i =>
            {
                var side = i % 3 == 0 ? TradeSide.Sell : TradeSide.Buy;
                var entry = 99m + i * 0.04m;
                var exit = entry + (i % 4 == 0 ? -0.3m : 0.45m);

                return new Trade
                {
                    Id = Guid.NewGuid(),
                    Symbol = i % 2 == 0 ? symbol : "ETHUSDT",
                    Side = side,
                    Status = TradeStatus.Executed,
                    EntryPrice = entry,
                    ExitPrice = exit,
                    Quantity = 0.05m + i * 0.001m,
                    EntryTime = DateTime.UtcNow.AddMinutes(-count + i),
                    ExitTime = DateTime.UtcNow.AddMinutes(-count + i + 1)
                };
            })
            .ToList();

    private static FoundationSnapshot CreateFoundationSnapshot()
        => new()
        {
            BidAskImbalance = 0.36d,
            TickDirectionRatio = 0.68d,
            Vwap = 101.22m,
            VwapStdDev = 0.71m,
            SpreadBps = 2.9d,
            WallLevel = 100.88m,
            VolumeAtPrice = new Dictionary<decimal, decimal>
            {
                [100.50m] = 4.2m,
                [100.75m] = 3.1m,
                [101.00m] = 5.7m
            }
        };

    private static PatternSnapshot CreatePatternSnapshot()
        => new()
        {
            BollingerPercentB = 0.74d,
            BollingerSqueeze = true,
            FibNearestLevel = 100.9m,
            FibRetracementDepth = 0.33d,
            IchimokuState = IchimokuState.AboveCloud,
            VwapDeviationSigmas = 1.28d,
            PatternTags = ["bb-squeeze", "trend", "vwap-reclaim"]
        };

    private static Signal CreatePersistedSignal(int index)
        => new()
        {
            Id = Guid.NewGuid(),
            Symbol = index % 2 == 0 ? "BTCUSDT" : "ETHUSDT",
            Source = "Phase8A.Persistence",
            Side = index % 3 == 0 ? TradeSide.Sell : TradeSide.Buy,
            Price = 100m + index * 0.01m,
            Status = TradeStatus.Pending,
            Quantity = 0.05m,
            Confidence = 75,
            Reasoning = "Perf persistence baseline",
            StrategyTag = "TrendConfluence",
            RegimeTag = "Trending",
            MarketType = MarketType.Crypto,
            Exchange = "Binance",
            Timestamp = DateTime.UtcNow.AddSeconds(-index),
            Volume24h = 3_500_000m,
            Metadata = new Dictionary<string, string>
            {
                ["Venue"] = "Binance",
                ["BenchmarkBatch"] = "Phase8A"
            }
        };

    private static QuantDbContext CreateDbContext(string databasePath)
    {
        var options = new DbContextOptionsBuilder<QuantDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

        return new QuantDbContext(options);
    }

    private static ReadOnlyMemory<byte> BuildDepthMessage()
    {
        const string bids = """
            [["100.01","3.50"],["100.00","2.10"],["99.99","1.80"],["99.98","4.20"],["99.97","3.00"],
             ["99.96","2.75"],["99.95","1.95"],["99.94","2.65"],["99.93","3.35"],["99.92","1.50"],
             ["99.91","2.45"],["99.90","2.05"],["99.89","1.70"],["99.88","3.80"],["99.87","1.25"],
             ["99.86","2.95"],["99.85","1.15"],["99.84","2.55"],["99.83","1.05"],["99.82","0.95"]]
            """;

        const string asks = """
            [["100.02","2.90"],["100.03","3.40"],["100.04","1.75"],["100.05","4.10"],["100.06","2.25"],
             ["100.07","2.60"],["100.08","1.45"],["100.09","3.05"],["100.10","2.15"],["100.11","1.35"],
             ["100.12","2.05"],["100.13","1.95"],["100.14","1.65"],["100.15","3.55"],["100.16","1.10"],
             ["100.17","2.40"],["100.18","1.05"],["100.19","2.20"],["100.20","0.98"],["100.21","0.88"]]
            """;

        var json = "{\"stream\":\"btcusdt@depth20@100ms\",\"data\":{\"e\":\"depthUpdate\",\"E\":1710000000123,\"s\":\"BTCUSDT\",\"bids\":"
            + bids
            + ",\"asks\":"
            + asks
            + "}}";

        return new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes(json));
    }

    private static TDelegate CreatePrivateMethodDelegate<TDelegate>(Type targetType, string methodName)
        where TDelegate : Delegate
    {
        var method = targetType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method {targetType.FullName}.{methodName} was not found.");

        return method.CreateDelegate<TDelegate>();
    }

    private static async Task<PerfResult> MeasureAsync(
        string name,
        int warmupIterations,
        int measuredIterations,
        Func<int, Task> action)
    {
        for (var i = 0; i < warmupIterations; i++)
        {
            await action(i);
        }

        ResetGc();

        var elapsedTicks = new long[measuredIterations];
        var startAllocated = GC.GetTotalAllocatedBytes(false);
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        var total = Stopwatch.StartNew();

        for (var i = 0; i < measuredIterations; i++)
        {
            var sw = Stopwatch.StartNew();
            await action(i);
            sw.Stop();
            elapsedTicks[i] = sw.ElapsedTicks;
        }

        total.Stop();

        return PerfResult.Create(
            name,
            measuredIterations,
            elapsedTicks,
            total.ElapsedTicks,
            GC.GetTotalAllocatedBytes(false) - startAllocated,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before);
    }

    private static async Task<PerfResult> MeasureSyncAsync(
        string name,
        int warmupIterations,
        int measuredIterations,
        Action<int> action)
    {
        for (var i = 0; i < warmupIterations; i++)
        {
            action(i);
        }

        ResetGc();

        var elapsedTicks = new long[measuredIterations];
        var startAllocated = GC.GetTotalAllocatedBytes(false);
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        var total = Stopwatch.StartNew();

        for (var i = 0; i < measuredIterations; i++)
        {
            var sw = Stopwatch.StartNew();
            action(i);
            sw.Stop();
            elapsedTicks[i] = sw.ElapsedTicks;
        }

        total.Stop();

        return await Task.FromResult(PerfResult.Create(
            name,
            measuredIterations,
            elapsedTicks,
            total.ElapsedTicks,
            GC.GetTotalAllocatedBytes(false) - startAllocated,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before));
    }

    private static void ResetGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static bool IsPerfEnabled()
        => string.Equals(Environment.GetEnvironmentVariable("TMQ_RUN_PERF_BASELINE"), "1", StringComparison.Ordinal);

    private static void BuildTestModel(string modelPath)
    {
        TestModelCache.EnsureModel(modelPath, "phase8a-perf-v1", cachePath =>
        {
            var trainer = new AlphaModelTrainer(NullLogger<AlphaModelTrainer>.Instance);
            var data = Enumerable.Range(0, 192)
                .Select(i =>
                {
                    var positive = i % 2 == 0;
                    var baseValue = positive ? 1.35f : -1.25f;

                    return new CandleMlData
                    {
                        NextClosePrice = positive ? 1f : -1f,
                        Features =
                        [
                            baseValue,
                            baseValue * 0.82f,
                            baseValue * 0.61f,
                            baseValue * 0.43f,
                            baseValue * 0.24f
                        ]
                    };
                })
                .ToList();

            trainer.TrainAndSaveModel(data, cachePath);
        });
    }

    private void EnsureModelBuilt()
    {
        if (!File.Exists(_modelPath))
        {
            BuildTestModel(_modelPath);
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (Directory.Exists(_workspacePath))
        {
            try
            {
                Directory.Delete(_workspacePath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class PerfOptions
    {
        public int WarmupIterations { get; init; } = 20;
        public int MeasureIterations { get; init; } = 120;
        public int ParserIterations { get; init; } = 2_000;
        public int PersistenceIterations { get; init; } = 40;

        public static PerfOptions FromEnvironment()
        {
            return new PerfOptions
            {
                WarmupIterations = Read("TMQ_PERF_WARMUP", 20, 1),
                MeasureIterations = Read("TMQ_PERF_ITERATIONS", 120, 5),
                ParserIterations = Read("TMQ_PERF_PARSER_ITERATIONS", 2_000, 100),
                PersistenceIterations = Read("TMQ_PERF_PERSISTENCE_ITERATIONS", 40, 10)
            };
        }

        private static int Read(string name, int fallback, int minimum)
            => int.TryParse(Environment.GetEnvironmentVariable(name), out var value)
                ? Math.Max(minimum, value)
                : fallback;
    }

    private sealed record PerfResult(
        string Name,
        int Iterations,
        double MeanMicroseconds,
        double P50Microseconds,
        double P95Microseconds,
        double P99Microseconds,
        double OperationsPerSecond,
        double AllocatedBytesPerOperation,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections)
    {
        public static PerfResult Create(
            string name,
            int iterations,
            IReadOnlyList<long> elapsedTicks,
            long totalElapsedTicks,
            long allocatedBytes,
            int gen0Collections,
            int gen1Collections,
            int gen2Collections)
        {
            var sorted = elapsedTicks.Order().ToArray();
            var totalMicroseconds = ToMicroseconds(totalElapsedTicks);

            return new PerfResult(
                name,
                iterations,
                totalMicroseconds / iterations,
                Percentile(sorted, 0.50),
                Percentile(sorted, 0.95),
                Percentile(sorted, 0.99),
                iterations / (totalMicroseconds / 1_000_000d),
                allocatedBytes / (double)iterations,
                gen0Collections,
                gen1Collections,
                gen2Collections);
        }

        private static double Percentile(IReadOnlyList<long> sortedTicks, double percentile)
        {
            if (sortedTicks.Count == 0)
            {
                return 0d;
            }

            var index = (int)Math.Ceiling((sortedTicks.Count - 1) * percentile);
            return ToMicroseconds(sortedTicks[index]);
        }

        private static double ToMicroseconds(long ticks)
            => ticks * 1_000_000d / Stopwatch.Frequency;
    }

    private sealed class ConstantSentimentService : ISentimentService
    {
        public Task<(double SentimentScore, bool IsBlackSwan, string Reasoning)> AnalyzeNewsAsync(string headline)
            => Task.FromResult((0.42d, false, "Constructive sentiment"));
    }

    private sealed class StaticExchangeInfoProvider : IExchangeInfoProvider
    {
        public double GetTickSize(string symbol) => 0.01d;
        public double GetLotSize(string symbol) => 0.001d;
    }

    private sealed class StaticPortfolioStateCache : IPortfolioStateCache
    {
        private readonly IReadOnlyList<Trade> _recentTrades;

        public StaticPortfolioStateCache(IReadOnlyList<Trade> recentTrades) => _recentTrades = recentTrades;

        public IReadOnlyList<Trade> GetRecentTrades() => _recentTrades;
        public void AddOrUpdateTrade(Trade trade) { }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StaticCoordinator : IAgentCoordinator
    {
        private static readonly IReadOnlyList<StrategyVote> Votes =
        [
            StrategyVote.Approve("TrendConfluence", 14f, "Trend alignment"),
            StrategyVote.Approve("MicroBreakout", 9f, "Liquidity expansion"),
            StrategyVote.Reject("MeanReversion", -3f, "Momentum too strong")
        ];

        private static readonly MetaAdaptation Adaptation = new()
        {
            ConfidenceScalar = 1.03f,
            StrategyWeights = new Dictionary<string, float>
            {
                ["TrendConfluence"] = 1.08f,
                ["MicroBreakout"] = 1.04f
            }
        };

        public Task ResetAllAgentsAsync(string symbol, CancellationToken ct = default) => Task.CompletedTask;
        public Task<FoundationSnapshot> BuildFoundationAsync(IReadOnlyList<MarketTick> ticks, IReadOnlyList<Candle> candles, CancellationToken ct = default) => Task.FromResult(FoundationSnapshot.Empty);
        public Task<PatternSnapshot> BuildPatternAsync(Signal signal, FoundationSnapshot foundation, IReadOnlyList<Candle> candles, CancellationToken ct = default) => Task.FromResult(PatternSnapshot.Empty);
        public Task<IReadOnlyList<StrategyVote>> CollectStrategyVotesAsync(Signal signal, PatternSnapshot patterns, FoundationSnapshot foundation, IReadOnlyList<Candle> candles, MarketRegime regime, CancellationToken ct = default) => Task.FromResult(Votes);
        public Task<FiveTierSnapshot> RunFullPipelineAsync(Signal signal, IReadOnlyList<MarketTick> ticks, IReadOnlyList<Candle> candles, MarketRegime regime, IReadOnlyList<Trade> openTrades, IReadOnlyList<Trade> recentHistory, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RiskGuardResult> RunRiskGauntletAsync(Signal signal, TradeDecision decision, IReadOnlyList<Trade> openTrades, CancellationToken ct = default) => Task.FromResult(RiskGuardResult.Pass("Risk.Passed"));
        public Task<MetaAdaptation> RunMetaAdaptationAsync(IReadOnlyList<Trade> recentHistory, MarketRegime regime, CancellationToken ct = default) => Task.FromResult(Adaptation);
    }

    private sealed class ConstantFoundationAgent : IFoundationAgent
    {
        private readonly FoundationSnapshot _snapshot;

        public ConstantFoundationAgent(string agentId, double imbalance, double tickRatio, decimal vwap, decimal vwapStdDev, double spreadBps, decimal wallLevel, decimal volumeBase)
        {
            AgentId = agentId;
            _snapshot = new FoundationSnapshot
            {
                BidAskImbalance = imbalance,
                TickDirectionRatio = tickRatio,
                Vwap = vwap,
                VwapStdDev = vwapStdDev,
                SpreadBps = spreadBps,
                WallLevel = wallLevel,
                VolumeAtPrice = new Dictionary<decimal, decimal>
                {
                    [100.50m] = volumeBase,
                    [100.75m] = volumeBase + 0.6m,
                    [101.00m] = volumeBase + 1.1m
                }
            };
        }

        public string AgentId { get; }
        public Task<FoundationSnapshot> ComputeAsync(IReadOnlyList<MarketTick> ticks, IReadOnlyList<Candle> candles, CancellationToken ct = default) => Task.FromResult(_snapshot);
    }

    private sealed class ConstantPatternAgent : IPatternAgent
    {
        private readonly PatternSnapshot _snapshot;

        public ConstantPatternAgent(string agentId, double bollingerPercentB, bool bollingerSqueeze, decimal fibNearestLevel, double fibRetracementDepth, IchimokuState ichimokuState, double vwapDeviationSigmas, params string[] tags)
        {
            AgentId = agentId;
            _snapshot = new PatternSnapshot
            {
                BollingerPercentB = bollingerPercentB,
                BollingerSqueeze = bollingerSqueeze,
                FibNearestLevel = fibNearestLevel,
                FibRetracementDepth = fibRetracementDepth,
                IchimokuState = ichimokuState,
                VwapDeviationSigmas = vwapDeviationSigmas,
                PatternTags = tags
            };
        }

        public string AgentId { get; }
        public Task<PatternSnapshot> AnalyzeAsync(Signal signal, FoundationSnapshot foundation, IReadOnlyList<Candle> candles, CancellationToken ct = default) => Task.FromResult(_snapshot);
    }

    private sealed class ConstantStrategyAgent : IStrategyAgent
    {
        private readonly bool _canHandle;
        private readonly StrategyVote _vote;

        public ConstantStrategyAgent(string strategyTag, int swarmWeight, bool canHandle, VoteOutcome outcome, float confidenceAdjustment)
        {
            StrategyTag = strategyTag;
            SwarmWeight = swarmWeight;
            _canHandle = canHandle;
            _vote = new StrategyVote
            {
                StrategyTag = strategyTag,
                Vote = outcome,
                ConfidenceAdjustment = confidenceAdjustment,
                Reasoning = "Phase8A vote"
            };
        }

        public string StrategyTag { get; }
        public int SwarmWeight { get; }
        public bool CanHandle(Signal signal, MarketRegime regime) => _canHandle;
        public Task<StrategyVote> EvaluateAsync(Signal signal, PatternSnapshot patterns, FoundationSnapshot foundation, IReadOnlyList<Candle> candles, CancellationToken ct = default) => Task.FromResult(_vote);
    }

    private sealed class ConstantRiskGuardAgent : IRiskGuardAgent
    {
        private readonly RiskGuardResult _result;

        public ConstantRiskGuardAgent(string guardId, int order, RiskGuardResult result)
        {
            GuardId = guardId;
            Order = order;
            _result = result;
        }

        public string GuardId { get; }
        public int Order { get; }
        public Task<RiskGuardResult> EvaluateAsync(Signal signal, TradeDecision decision, IReadOnlyList<Trade> openTrades, CancellationToken ct = default) => Task.FromResult(_result);
    }

    private sealed class ConstantMetaLearningAgent : IMetaLearningAgent
    {
        private readonly MetaAdaptation _adaptation;

        public ConstantMetaLearningAgent(string agentId, MetaAdaptation adaptation)
        {
            AgentId = agentId;
            _adaptation = adaptation;
        }

        public string AgentId { get; }
        public Task<MetaAdaptation> AdaptAsync(IReadOnlyList<Trade> recentHistory, MarketRegime regime, CancellationToken ct = default) => Task.FromResult(_adaptation);
    }
}
