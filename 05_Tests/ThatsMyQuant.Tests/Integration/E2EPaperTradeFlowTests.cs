using FluentAssertions;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using NSubstitute;
using ThatsMyQuant.Application.Common;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure;
using ThatsMyQuant.Infrastructure.Ai;
using ThatsMyQuant.Infrastructure.Persistence;
using ThatsMyQuant.Infrastructure.Repositories;
using ThatsMyQuant.Infrastructure.Services;
using ThatsMyQuant.Infrastructure.Services.Validators;
using ThatsMyQuant.Tests.TestInfrastructure;
using Xunit;

namespace ThatsMyQuant.Tests.Integration;

/// <summary>
/// End-to-end: trending signal → paper trade created → sync reconciliation passes.
/// Verifies that our gate calibrations (no score multiplier, volume 1.3x, ratio-based regime)
/// actually produce executable trades on realistic BTC candles.
/// </summary>
public class E2EPaperTradeFlowTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private SqliteConnection _sharedConnection = null!;
    private string _tempDir = null!;

    public async Task InitializeAsync()
    {
        _sharedConnection = new SqliteConnection("DataSource=:memory:");
        _sharedConnection.Open();

        _tempDir = Path.Combine(Path.GetTempPath(), "tmq-e2e-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var modelPath = Path.Combine(_tempDir, "PredictiveAlphaModel.zip");
        BuildTestModel(modelPath);

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));

        var config = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(config);

        services.AddThatsMyQuantApplication();
        services.AddThatsMyQuantInfrastructure();
        services.AddSingleton(Substitute.For<IAuditLogger>());
        services.AddSingleton<IExchangeService, MockBrokerService>();
        services.AddKernel();
        services.AddSingleton<IChatCompletionService, MockChatCompletionService>();
        services.AddScoped<ISentimentService, GeminiSentimentService>();
        services.AddScoped<IReasoningProvider, DeepSeekReasoningService>();
        services.AddScoped<IAiAnalystService, AiAnalystService>();
        services.AddScoped<ITechnicalAnalysisService, QuantIndicatorService>();
        services.AddSingleton<IMarketDataService>(Substitute.For<IMarketDataService>());
        services.AddScoped<ISignalValidator, MinVolumeValidator>();
        services.AddScoped<ISignalRepository, SignalRepository>();
        services.AddScoped<ITradeRepository, TradeRepository>();
        services.AddSingleton<ITickLogger>(Substitute.For<ITickLogger>());
        services.AddSingleton<IExchangeInfoProvider>(new NoOpExchangeInfoProviderE2E());

        var capturedModelPath = modelPath;
        services.AddSingleton(sp =>
            new InferenceAgent(sp.GetRequiredService<FeatureDriftMonitor>(), capturedModelPath));

        var sharedConn = _sharedConnection;
        services.AddDbContext<QuantDbContext>(opt => opt.UseSqlite(sharedConn), ServiceLifetime.Scoped);

        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(ProcessSignalCommand).Assembly);
            cfg.RegisterServicesFromAssembly(typeof(ThatsMyQuant.Application.Handlers.ProcessSignalHandler).Assembly);
        });

        _provider = services.BuildServiceProvider();

        var venuePortfolio = _provider.GetRequiredService<VenuePortfolioManager>();
        venuePortfolio.Clear();
        venuePortfolio.SetBalance(100_000m);

        using var initScope = _provider.CreateScope();
        var db = initScope.ServiceProvider.GetRequiredService<QuantDbContext>();
        await db.Database.EnsureCreatedAsync();

        // Default: empty candles (overridden per-test where needed)
        var marketData = _provider.GetRequiredService<IMarketDataService>();
        marketData.GetCandlesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>())
            .Returns(Task.FromResult(new List<Candle>()));
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _sharedConnection.DisposeAsync();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task TrendingBtcSignal_ShouldProducePaperTrade_InDatabase()
    {
        // Configure market data to return trending BTC candles
        var candles = BuildTrendingBtcCandles(200);
        var marketData = _provider.GetRequiredService<IMarketDataService>();
        marketData.GetCandlesAsync(
                Arg.Is<string>(s => s.Contains("BTC", StringComparison.OrdinalIgnoreCase)),
                Arg.Any<int>(),
                Arg.Any<string>())
            .Returns(Task.FromResult(candles));

        var signal = new Signal
        {
            Symbol = "BTC-USDT",
            Price = candles.Last().Close,
            Side = TradeSide.Buy,
            Volume24h = 5_000_000_000m,
            Timestamp = DateTime.UtcNow
        };

        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Act: full signal pipeline
        await mediator.Send(new ProcessSignalCommand(signal));

        // Assert: a Trade was created (not just a Signal)
        var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
        var trade = await db.Trades.FirstOrDefaultAsync(t => t.SignalId == signal.Id);

        trade.Should().NotBeNull("a paper trade must be created when confluence passes");
        trade!.Symbol.Should().Be("BTC-USDT");
        trade.Side.Should().Be(TradeSide.Buy);
        trade.Status.Should().NotBe(TradeStatus.Failed,
            "MockBrokerService fills immediately — trade should not fail");
        trade.EntryPrice.Should().BeGreaterThan(0m);
    }

    [Fact]
    public async Task SyncService_AfterPaperTrade_ReconcilationWithNoOpenOrders_ReturnsZeroDiscrepancies()
    {
        // Arrange: create and persist an active trade directly
        using var setupScope = _provider.CreateScope();
        var tradeRepo = setupScope.ServiceProvider.GetRequiredService<ITradeRepository>();

        var signalId = Guid.NewGuid();
        var trade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signalId,
            Symbol = "BTC-USDT",
            Side = TradeSide.Buy,
            EntryPrice = 62000m,
            MarkPrice = 62000m,
            Quantity = 0.01m,
            FilledQuantity = 0.01m,
            RequestedQuantity = 0.01m,
            RemainingQuantity = 0.01m,
            Status = TradeStatus.Executed,
            LifecycleStatus = TradeLifecycleStatus.Active,
            StrategyTag = "TrendConfluence",
            RegimeTag = "Trending",
            ExitPolicyTag = "TrendRunner",
            ExecutionMode = "PAPER",
            PromotionStage = "Backtest",
            SubmittedTime = DateTime.UtcNow.AddMinutes(-1),
            EntryTime = DateTime.UtcNow.AddMinutes(-1),
            LastUpdatedTime = DateTime.UtcNow
        };
        await tradeRepo.AddAsync(trade);

        // Act: reconcile with no open orders on the venue
        using var syncScope = _provider.CreateScope();
        var syncService = syncScope.ServiceProvider.GetRequiredService<TradeExecutionSyncService>();
        var result = await syncService.ReconcileOpenOrdersAsync([], CancellationToken.None);

        // Assert: active trades don't require venue presence — no false discrepancies
        result.Should().NotBeNull();
        result.OrphanedVenueOrders.Should().Be(0, "no venue orders were passed in");
        result.MissingLocalOrders.Should().Be(0,
            "Active trades don't require venue presence — paper trades are managed locally");
    }

    [Fact]
    public async Task SyncService_ApplyExecutionReport_UpdatesTradeFilledQuantity()
    {
        // Arrange: insert a submitted trade whose SignalId encodes as the ClientOrderId
        using var setupScope = _provider.CreateScope();
        var tradeRepo = setupScope.ServiceProvider.GetRequiredService<ITradeRepository>();

        var signalId = Guid.NewGuid();
        // ClientOrderId format: Guid.ToString("N") — no hyphens
        var clientOrderId = signalId.ToString("N");

        var trade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signalId,
            Symbol = "ETH-USDT",
            Side = TradeSide.Buy,
            EntryPrice = 0m,
            MarkPrice = 0m,
            Quantity = 0.1m,
            RequestedQuantity = 0.1m,
            RemainingQuantity = 0.1m,
            Status = TradeStatus.Pending,
            LifecycleStatus = TradeLifecycleStatus.Submitted,
            StrategyTag = "TrendConfluence",
            RegimeTag = "Trending",
            ExitPolicyTag = "TrendRunner",
            ExecutionMode = "PAPER",
            PromotionStage = "Backtest",
            SubmittedTime = DateTime.UtcNow,
            EntryTime = DateTime.UtcNow,
            LastUpdatedTime = DateTime.UtcNow
        };
        await tradeRepo.AddAsync(trade);

        var report = new ExecutionReport(
            Symbol: "ETH-USDT",
            ClientOrderId: clientOrderId,
            ExchangeOrderId: "EX-001",
            FilledQuantity: 0.1m,
            RemainingQuantity: 0m,
            AveragePrice: 3500m,
            LastFillPrice: 3500m,
            LastFillQuantity: 0.1m,
            Status: "FILLED",
            LatencyMs: 12,
            TimestampUtc: DateTime.UtcNow);

        // Act
        using var syncScope = _provider.CreateScope();
        var syncService = syncScope.ServiceProvider.GetRequiredService<TradeExecutionSyncService>();
        var applied = await syncService.ApplyExecutionReportAsync(report, CancellationToken.None);

        // Assert
        applied.Should().BeTrue("a matching submitted trade should absorb the fill report");

        using var verifyScope = _provider.CreateScope();
        var verifyRepo = verifyScope.ServiceProvider.GetRequiredService<ITradeRepository>();
        var updated = await verifyRepo.GetBySignalIdAsync(signalId);
        updated.Should().NotBeNull();
        updated!.FilledQuantity.Should().Be(0.1m);
        updated.EntryPrice.Should().Be(3500m);
    }

    // ── Candle Factory ──────────────────────────────────────────────────────────

    /// <summary>
    /// Produces 200 BTC-USDT hourly candles with a clear uptrend designed to trigger
    /// Trending regime, positive MACD/EMA alignment, and volume spike on final bars.
    ///
    /// Pattern: 4 up (+50) then 1 pullback (-80) per 5-bar cycle, phase-shifted so the
    /// final bar is an UP bar (MACD histogram stays positive, bestSide=Buy).
    /// Last 4 bars carry 2× volume for the 1.67× VolumeRatio bonus.
    /// </summary>
    private static List<Candle> BuildTrendingBtcCandles(int count = 200)
    {
        var candles = new List<Candle>(count);
        decimal close = 60_000m;
        var baseTime = DateTime.UtcNow.AddHours(-count);

        for (var i = 0; i < count; i++)
        {
            // Pullbacks in main body (every 5th bar) for realistic ATR/RSI profile, but
            // force the last 10 bars to be strictly up so pattern detection and MACD
            // histogram read bullish on the evaluation bar.
            var isTailUp = i >= count - 10;
            decimal delta = !isTailUp && i % 5 == 4 ? -80m : +50m;
            close = Math.Max(1m, close + delta);

            var volume = i >= count - 4 ? 2000 : 1000;

            candles.Add(new Candle
            {
                Open = close - delta,
                High = close + 200m,
                Low = close - 200m,
                Close = close,
                Volume = volume,
                Timestamp = baseTime.AddHours(i)
            });
        }

        return candles;
    }

    private static void BuildTestModel(string modelPath)
    {
        TestModelCache.EnsureModel(modelPath, "e2e-v1", cachePath =>
        {
            var trainer = new AlphaModelTrainer(Substitute.For<ILogger<AlphaModelTrainer>>());
            var data = Enumerable.Range(0, 160)
                .Select(i =>
                {
                    var positive = i % 2 == 0;
                    // Positive examples use 0.3 (not 1.5) so FastTree's split threshold lands at
                    // (-1.5 + 0.3)/2 = -0.6. Zero-input features [0,0,0,0,0] are then above
                    // the threshold on every split → predict +1.0, avoiding a spurious ML veto
                    // when FeatureEngineeringAgent hasn't yet processed any tick data.
                    var baseValue = positive ? 0.3f : -1.5f;
                    return new CandleMlData
                    {
                        NextClosePrice = positive ? 1f : -1f,
                        Features = [baseValue, baseValue * 0.8f, baseValue * 0.6f, baseValue * 0.4f, baseValue * 0.2f]
                    };
                })
                .ToList();
            trainer.TrainAndSaveModel(data, cachePath);
        });
    }
}

file sealed class NoOpExchangeInfoProviderE2E : IExchangeInfoProvider
{
    public double GetTickSize(string symbol) => 0.01;
    public double GetLotSize(string symbol) => 0.001;
}
