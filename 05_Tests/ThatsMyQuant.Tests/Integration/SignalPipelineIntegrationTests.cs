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
/// Validates: Requirements 1.8, 2.8
/// End-to-end signal pipeline integration tests using an in-memory SQLite database.
/// </summary>
public class SignalPipelineIntegrationTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private SqliteConnection _sharedConnection = null!;
    private string _tempDir = null!;

    public async Task InitializeAsync()
    {
        // Keep a single open SQLite connection so all scopes share the same in-memory database
        _sharedConnection = new SqliteConnection("DataSource=:memory:");
        _sharedConnection.Open();

        // Build a minimal ML model so InferenceAgent can start without a real model file
        _tempDir = Path.Combine(Path.GetTempPath(), "tmq-integration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var modelPath = Path.Combine(_tempDir, "PredictiveAlphaModel.zip");
        BuildTestModel(modelPath);

        var services = new ServiceCollection();

        // Logging
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));

        // Configuration (empty — handler only reads Execution:UseWorkerHandoff)
        var config = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(config);

        // Application + Infrastructure DI
        services.AddThatsMyQuantApplication();
        services.AddThatsMyQuantInfrastructure();

        // Override IAuditLogger with no-op substitute (after Infrastructure registers ConsoleAuditLogger)
        services.AddSingleton(Substitute.For<IAuditLogger>());

        // Override IExchangeService with MockBrokerService
        services.AddSingleton<IExchangeService, MockBrokerService>();

        // SemanticKernel + MockChatCompletionService (required by GeminiSentimentService)
        services.AddKernel();
        services.AddSingleton<IChatCompletionService, MockChatCompletionService>();

        // ISentimentService, IReasoningProvider, IAiAnalystService
        services.AddScoped<ISentimentService, GeminiSentimentService>();
        services.AddScoped<IReasoningProvider, DeepSeekReasoningService>();
        services.AddScoped<IAiAnalystService, AiAnalystService>();

        // Technical analysis + market data
        services.AddScoped<ITechnicalAnalysisService, QuantIndicatorService>();
        services.AddSingleton<IMarketDataService>(Substitute.For<IMarketDataService>());

        // Signal validators
        services.AddScoped<ISignalValidator, MinVolumeValidator>();

        // Repositories
        services.AddScoped<ISignalRepository, SignalRepository>();
        services.AddScoped<ITradeRepository, TradeRepository>();

        // ITickLogger (required by TickPipeline)
        services.AddSingleton<ITickLogger>(Substitute.For<ITickLogger>());

        // IExchangeInfoProvider (required by OFIExecutionAgent notification handler)
        services.AddSingleton<IExchangeInfoProvider>(new NoOpExchangeInfoProvider());

        // Override InferenceAgent with a test model so it doesn't throw FileNotFoundException
        var capturedModelPath = modelPath;
        services.AddSingleton(sp =>
            new InferenceAgent(sp.GetRequiredService<FeatureDriftMonitor>(), capturedModelPath));

        // QuantDbContext — shared in-memory SQLite connection so all scopes see the same data
        var sharedConn = _sharedConnection;
        services.AddDbContext<QuantDbContext>(opt =>
            opt.UseSqlite(sharedConn),
            ServiceLifetime.Scoped);

        // MediatR — scan ProcessSignalCommand assembly (Contracts) and Application handlers
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(ProcessSignalCommand).Assembly);
            cfg.RegisterServicesFromAssembly(typeof(ThatsMyQuant.Application.Handlers.ProcessSignalHandler).Assembly);
        });

        _provider = services.BuildServiceProvider();

        // Create schema once using a root-level scope
        using var initScope = _provider.CreateScope();
        var db = initScope.ServiceProvider.GetRequiredService<QuantDbContext>();
        await db.Database.EnsureCreatedAsync();

        // Configure IMarketDataService mock to return empty candles
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

    private static void BuildTestModel(string modelPath)
    {
        TestModelCache.EnsureModel(modelPath, "signal-pipeline-v1", cachePath =>
        {
            var trainer = new AlphaModelTrainer(Substitute.For<ILogger<AlphaModelTrainer>>());
            var data = Enumerable.Range(0, 160)
                .Select(i =>
                {
                    var positive = i % 2 == 0;
                    var baseValue = positive ? 1.5f : -1.5f;
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

    [Fact]
    public async Task PipelineFlow_ValidSignal_PersistsAndCompletes()
    {
        // Arrange
        var signal = new Signal
        {
            Symbol = "BTCUSDT",
            Price = 60000m,
            Side = TradeSide.Buy,
            Volume24h = 50_000_000m,
            Timestamp = DateTime.UtcNow
        };

        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Act — should not throw
        var act = async () => await mediator.Send(new ProcessSignalCommand(signal));
        await act.Should().NotThrowAsync();

        // Assert — signal persisted
        var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
        var persisted = await db.Signals.AnyAsync(s => s.Symbol == "BTCUSDT");
        persisted.Should().BeTrue("the signal should have been persisted to the database");
    }

    [Fact]
    public async Task PipelineRejection_LowVolumeSignal_ReturnsFalseAndPersistsRejected()
    {
        // Arrange — Volume24h = 500m is below the $1M MinVolumeValidator threshold
        var signal = new Signal
        {
            Symbol = "BTCUSDT",
            Price = 60000m,
            Side = TradeSide.Buy,
            Volume24h = 500m,
            Timestamp = DateTime.UtcNow
        };

        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Act
        var result = await mediator.Send(new ProcessSignalCommand(signal));

        // Assert — handler returns false for rejected signals
        result.Should().BeFalse("low-volume signals should be rejected by MinVolumeValidator");

        // Assert — persisted signal has Rejected status
        var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
        var persisted = await db.Signals.FirstOrDefaultAsync(s => s.Symbol == "BTCUSDT");
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(TradeStatus.Rejected);
    }
}

/// <summary>No-op implementation of IExchangeInfoProvider for integration tests.</summary>
file sealed class NoOpExchangeInfoProvider : IExchangeInfoProvider
{
    public double GetTickSize(string symbol) => 0.01;
    public double GetLotSize(string symbol) => 0.001;
}
