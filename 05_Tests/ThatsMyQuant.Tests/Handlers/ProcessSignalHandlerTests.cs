using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Globalization;
using ThatsMyQuant.Application.Commands;
using ThatsMyQuant.Application.Handlers;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using ThatsMyQuant.Domain.Interfaces;
using ThatsMyQuant.Domain.ValueObjects;
using Xunit;

namespace ThatsMyQuant.Tests.Handlers;

public class ProcessSignalHandlerTests : IDisposable
{
    private readonly ILogger<ProcessSignalHandler> _logger;
    private readonly IMediator _mediator;
    private readonly IAiReasoningService _aiService;
    private readonly IMarketDataService _marketDataService;
    private readonly IHftMarketDataService _hftMarketDataService;
    private readonly ISignalRepository _signalRepository;
    private readonly ITradeRepository _tradeRepository;
    private readonly ITechnicalAnalysisService _technicalAnalysis;
    private readonly IHftSignalGenerationService _hftSignalGenerationService;
    private readonly IPortfolioStateCache _portfolioStateCache;
    private readonly RiskAgent _riskAgent;
    private readonly StrategyExpectancyService _expectancyService;
    private readonly SymbolExpectancyService _symbolExpectancyService;
    private readonly StrategyPromotionService _promotionService;
    private readonly EdgeScoreService _edgeScoreService;
    private readonly IConfiguration _configuration;
    private readonly ProcessSignalHandler _handler;
    private static readonly DateTime TestSessionUtc = new(2026, 03, 21, 8, 30, 0, DateTimeKind.Utc);

    public ProcessSignalHandlerTests()
    {
        // Override market hours for testing
        MarketHoursScheduler.OverrideCanTrade = true;

        _logger = Substitute.For<ILogger<ProcessSignalHandler>>();
        _mediator = Substitute.For<IMediator>();
        _aiService = Substitute.For<IAiReasoningService>();
        _marketDataService = Substitute.For<IMarketDataService>();
        _hftMarketDataService = Substitute.For<IHftMarketDataService>();
        _signalRepository = Substitute.For<ISignalRepository>();
        _tradeRepository = Substitute.For<ITradeRepository>();
        _technicalAnalysis = Substitute.For<ITechnicalAnalysisService>();
        _hftSignalGenerationService = Substitute.For<IHftSignalGenerationService>();
        _portfolioStateCache = Substitute.For<IPortfolioStateCache>();
        _riskAgent = new RiskAgent(Substitute.For<ILogger<RiskAgent>>());
        _expectancyService = new StrategyExpectancyService();
        _symbolExpectancyService = new SymbolExpectancyService();
        _promotionService = new StrategyPromotionService(new InMemoryStrategyPerformanceStore());
        _edgeScoreService = new EdgeScoreService();
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Execution:UseWorkerHandoff"] = "false"
            })
            .Build();
        _portfolioStateCache.GetRecentTrades().Returns(Array.Empty<Trade>());

        _handler = new ProcessSignalHandler(
            _logger, _mediator, _aiService, _marketDataService,
            _signalRepository, _tradeRepository, _technicalAnalysis, _hftMarketDataService, _riskAgent, _expectancyService, _symbolExpectancyService, _promotionService, _edgeScoreService, _configuration, _portfolioStateCache, _hftSignalGenerationService);
    }

    public void Dispose()
    {
        // Reset override after each test so production code is unaffected
        MarketHoursScheduler.OverrideCanTrade = null;
    }

    private static Signal CreateTestSignal() => new()
    {
        Id = Guid.NewGuid(),
        Symbol = "THYAO",
        Side = TradeSide.Buy,
        Price = 285.50m,
        Source = "TradingView",
        Timestamp = TestSessionUtc
    };

    private static List<Candle> CreateTestCandles() =>
        Enumerable.Range(0, 100).Select(i => new Candle
        {
            Timestamp = DateTime.UtcNow.AddDays(-100 + i),
            Open = 280 + i * 0.1m,
            High = 282 + i * 0.1m,
            Low = 278 + i * 0.1m,
            Close = 281 + i * 0.1m,
            Volume = 5_000_000
        }).ToList();

    [Fact]
    public async Task Handle_IndicatorsApprove_ShouldDispatchTradeCommand()
    {
        // Arrange
        var signal = CreateTestSignal();
        var candles = CreateTestCandles();
        _marketDataService.GetCandlesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>())
            .Returns(candles);
        _technicalAnalysis.AnalyzeAsync(Arg.Any<Signal>(), Arg.Any<List<Candle>>(), Arg.Any<RiskParameters>())
            .Returns(new TradeDecision
            {
                ShouldTrade = true,
                Side = TradeSide.Buy,
                Confidence = 75,
                Reasoning = "RSI + MACD + EMA confluence",
                StopLoss = 280m,
                TakeProfit = 295m,
                PositionSizePercent = 0.03m,
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending"
            });
        _aiService.ShouldExecuteAsync(Arg.Any<Signal>()).Returns(true);
        SeedPromotionReady();
        _mediator.Send(Arg.Any<ExecuteTradeCommand>(), Arg.Any<CancellationToken>())
            .Returns(new Trade
            {
                Id = Guid.NewGuid(),
                SignalId = signal.Id,
                Symbol = signal.Symbol,
                EntryPrice = signal.Price,
                Quantity = 1,
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryTime = DateTime.UtcNow
            });

        // Act
        var result = await _handler.Handle(new ProcessSignalCommand(signal), CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        await _mediator.Received(1).Send(Arg.Any<ExecuteTradeCommand>(), Arg.Any<CancellationToken>());
        signal.Status.Should().Be(TradeStatus.Executed);
        signal.Confidence.Should().Be(75);
    }

    [Fact]
    public async Task Handle_IndicatorsReject_ShouldNotDispatchAndReturnFalse()
    {
        // Arrange
        var signal = CreateTestSignal();
        _marketDataService.GetCandlesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>())
            .Returns(CreateTestCandles());
        _technicalAnalysis.AnalyzeAsync(Arg.Any<Signal>(), Arg.Any<List<Candle>>(), Arg.Any<RiskParameters>())
            .Returns(TradeDecision.Reject("Confluence too weak (35/100)"));

        // Act
        var result = await _handler.Handle(new ProcessSignalCommand(signal), CancellationToken.None);

        // Assert
        result.Should().BeFalse();
        await _mediator.DidNotReceive().Send(Arg.Any<ExecuteTradeCommand>(), Arg.Any<CancellationToken>());
        signal.Status.Should().Be(TradeStatus.Rejected);
    }

    [Fact]
    public async Task Handle_AiOverride_ShouldRejectDespiteIndicators()
    {
        // Arrange
        var signal = CreateTestSignal();
        _marketDataService.GetCandlesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>())
            .Returns(CreateTestCandles());
        _technicalAnalysis.AnalyzeAsync(Arg.Any<Signal>(), Arg.Any<List<Candle>>(), Arg.Any<RiskParameters>())
            .Returns(new TradeDecision
            {
                ShouldTrade = true,
                Side = TradeSide.Buy,
                Confidence = 80,
                Reasoning = "Strong confluence",
                StopLoss = 280m,
                TakeProfit = 295m,
                PositionSizePercent = 0.04m,
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending"
            });
        SeedPromotionReady();
        _aiService.ShouldExecuteAsync(Arg.Any<Signal>()).Returns(false);
        _aiService.GetReasoningAsync(Arg.Any<Signal>()).Returns("Negative news detected");

        // Act
        var result = await _handler.Handle(new ProcessSignalCommand(signal), CancellationToken.None);

        // Assert
        result.Should().BeFalse();
        signal.Status.Should().Be(TradeStatus.Rejected);
        signal.Reasoning.Should().Contain("AI Override");
    }

    [Fact]
    public async Task Handle_AnalysisFails_ShouldReturnFalseAndMarkFailed()
    {
        // Arrange
        var signal = CreateTestSignal();
        _marketDataService.GetCandlesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>())
            .Returns(CreateTestCandles());
        _technicalAnalysis.AnalyzeAsync(Arg.Any<Signal>(), Arg.Any<List<Candle>>(), Arg.Any<RiskParameters>())
            .ThrowsAsync(new Exception("Indicator computation failed"));

        // Act
        var result = await _handler.Handle(new ProcessSignalCommand(signal), CancellationToken.None);

        // Assert
        result.Should().BeFalse();
        signal.Status.Should().Be(TradeStatus.Failed);
    }

    [Fact]
    public async Task Handle_NegativeExpectancyBucket_ShouldRejectBeforeExecution()
    {
        var signal = CreateTestSignal();
        _marketDataService.GetCandlesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>())
            .Returns(CreateTestCandles());
        _technicalAnalysis.AnalyzeAsync(Arg.Any<Signal>(), Arg.Any<List<Candle>>(), Arg.Any<RiskParameters>())
            .Returns(new TradeDecision
            {
                ShouldTrade = true,
                Side = TradeSide.Buy,
                Confidence = 78,
                Reasoning = "Strong trend confluence",
                StopLoss = 280m,
                TakeProfit = 295m,
                PositionSizePercent = 0.04m,
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending"
            });

        _portfolioStateCache.GetRecentTrades().Returns(new[]
        {
            CreateClosedTrade(-120m),
            CreateClosedTrade(-100m),
            CreateClosedTrade(-90m),
            CreateClosedTrade(-80m),
            CreateClosedTrade(30m)
        });

        var result = await _handler.Handle(new ProcessSignalCommand(signal), CancellationToken.None);

        result.Should().BeFalse();
        signal.Status.Should().Be(TradeStatus.Rejected);
        signal.Reasoning.Should().Contain("Expectancy Guard");
        await _mediator.DidNotReceive().Send(Arg.Any<ExecuteTradeCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WeakSymbolHistory_ShouldRejectBeforeExecution()
    {
        var signal = CreateTestSignal();
        _marketDataService.GetCandlesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>())
            .Returns(CreateTestCandles());
        _technicalAnalysis.AnalyzeAsync(Arg.Any<Signal>(), Arg.Any<List<Candle>>(), Arg.Any<RiskParameters>())
            .Returns(new TradeDecision
            {
                ShouldTrade = true,
                Side = TradeSide.Buy,
                Confidence = 82,
                Reasoning = "Clean setup",
                StopLoss = 280m,
                TakeProfit = 295m,
                PositionSizePercent = 0.04m,
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending"
            });

        _portfolioStateCache.GetRecentTrades().Returns(new[]
        {
            CreateClosedTradeForSymbol(signal.Symbol, -120m),
            CreateClosedTradeForSymbol(signal.Symbol, -90m),
            CreateClosedTradeForSymbol(signal.Symbol, -75m),
            CreateClosedTradeForSymbol(signal.Symbol, -60m),
            CreateClosedTradeForSymbol(signal.Symbol, 10m),
            CreateClosedTradeForSymbol("GARAN", 140m),
            CreateClosedTradeForSymbol("GARAN", 120m),
            CreateClosedTradeForSymbol("AKBNK", 110m),
            CreateClosedTradeForSymbol("AKBNK", 100m),
            CreateClosedTradeForSymbol("SISE", 95m)
        });

        var result = await _handler.Handle(new ProcessSignalCommand(signal), CancellationToken.None);

        result.Should().BeFalse();
        signal.Status.Should().Be(TradeStatus.Rejected);
        signal.Reasoning.Should().Contain("Symbol Guard");
        await _mediator.DidNotReceive().Send(Arg.Any<ExecuteTradeCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_HftSignal_ShouldUseDedicatedHftSignalGenerator()
    {
        var signal = CreateTestSignal();
        signal.Source = "HFT-Probe";
        signal.Metadata["ExecutionMode"] = "HFT";
        var candles = CreateTestCandles();

        _hftMarketDataService.GetCandlesAsync(Arg.Any<Signal>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(candles);
        _hftSignalGenerationService.AnalyzeAsync(Arg.Any<Signal>(), Arg.Any<List<Candle>>(), Arg.Any<RiskParameters>())
            .Returns(new TradeDecision
            {
                ShouldTrade = true,
                Side = TradeSide.Buy,
                Confidence = 81,
                Reasoning = "Dedicated HFT path approved",
                StopLoss = 280m,
                TakeProfit = 290m,
                PositionSizePercent = 0.01m,
                StrategyTag = "HftMicroEdge",
                RegimeTag = "Volatile"
            });
        SeedPromotionReady("THYAO", "HftMicroEdge", "Volatile");
        _aiService.ShouldExecuteAsync(Arg.Any<Signal>()).Returns(true);
        _mediator.Send(Arg.Any<ExecuteTradeCommand>(), Arg.Any<CancellationToken>())
            .Returns(new Trade
            {
                Id = Guid.NewGuid(),
                SignalId = signal.Id,
                Symbol = signal.Symbol,
                EntryPrice = signal.Price,
                Quantity = 1,
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryTime = DateTime.UtcNow
            });

        var result = await _handler.Handle(new ProcessSignalCommand(signal), CancellationToken.None);

        result.Should().BeTrue();
        await _hftMarketDataService.Received(1)
            .GetCandlesAsync(Arg.Any<Signal>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _hftSignalGenerationService.Received(1)
            .AnalyzeAsync(Arg.Any<Signal>(), Arg.Any<List<Candle>>(), Arg.Any<RiskParameters>());
        await _technicalAnalysis.DidNotReceive()
            .AnalyzeAsync(Arg.Any<Signal>(), Arg.Any<List<Candle>>(), Arg.Any<RiskParameters>());
    }

    [Fact]
    public async Task Handle_ShouldApplyFinalAllocatorBiasMetadata_ForExactBucket()
    {
        var signal = CreateTestSignal();
        _marketDataService.GetCandlesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>())
            .Returns(CreateTestCandles());
        _technicalAnalysis.AnalyzeAsync(Arg.Any<Signal>(), Arg.Any<List<Candle>>(), Arg.Any<RiskParameters>())
            .Returns(new TradeDecision
            {
                ShouldTrade = true,
                Side = TradeSide.Buy,
                Confidence = 88,
                Reasoning = "Exact bucket is healthy",
                StopLoss = 280m,
                TakeProfit = 298m,
                PositionSizePercent = 0.04m,
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending",
                ExitPolicyTag = "TrendRunner"
            });
        SeedPromotionReady();
        _portfolioStateCache.GetRecentTrades().Returns(Enumerable.Range(0, 8)
            .Select(index => CreateClosedTrade(
                pnl: index < 6 ? 18m : -4m,
                edgeScore: index < 6 ? 88m : 72m,
                edgeBand: index < 6 ? "Full" : "Reduced"))
            .ToArray());
        _aiService.ShouldExecuteAsync(Arg.Any<Signal>()).Returns(true);
        _mediator.Send(Arg.Any<ExecuteTradeCommand>(), Arg.Any<CancellationToken>())
            .Returns(new Trade
            {
                Id = Guid.NewGuid(),
                SignalId = signal.Id,
                Symbol = signal.Symbol,
                EntryPrice = signal.Price,
                Quantity = 1,
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryTime = DateTime.UtcNow
            });

        var result = await _handler.Handle(new ProcessSignalCommand(signal), CancellationToken.None);

        result.Should().BeTrue();
        signal.Metadata.Should().ContainKey("FinalAllocatorBias");
        decimal.Parse(signal.Metadata["FinalAllocatorBias"], CultureInfo.InvariantCulture).Should().BeGreaterThan(1.0m);
        signal.Metadata["FinalAllocatorScope"].Should().Contain("exact symbol-session bucket");
        signal.Reasoning.Should().Contain("Final Allocator:");
    }

    private static Trade CreateClosedTrade(decimal pnl, decimal edgeScore = 0m, string edgeBand = "Full")
    {
        var entry = 100m;
        return new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = Guid.NewGuid(),
            Symbol = "THYAO",
            Side = TradeSide.Buy,
            EntryPrice = entry,
            ExitPrice = entry + pnl,
            Quantity = 1m,
            Status = TradeStatus.Executed,
            StrategyTag = "TrendConfluence",
            RegimeTag = "Trending",
            EdgeScore = edgeScore,
            EdgeBand = edgeBand,
            EntryTime = TestSessionUtc.AddHours(-1),
            ExitTime = TestSessionUtc.AddMinutes(15)
        };
    }

    private static Trade CreateClosedTradeForSymbol(string symbol, decimal pnl, decimal edgeScore = 0m, string edgeBand = "Full")
    {
        var trade = CreateClosedTrade(pnl, edgeScore, edgeBand);
        trade.Symbol = symbol;
        return trade;
    }

    private sealed class InMemoryStrategyPerformanceStore : IStrategyPerformanceStore
    {
        private StrategyPerformanceState _state = new();

        public StrategyPerformanceState Load() => _state;

        public void Save(StrategyPerformanceState state) => _state = state;
    }

    private void SeedPromotionReady(string symbol = "THYAO", string strategyTag = "TrendConfluence", string regimeTag = "Trending")
    {
        _promotionService.RecordBacktestState(new BacktestResult
        {
            Symbol = symbol,
            Trades = Enumerable.Range(0, 8)
                .Select(index => new BacktestTradeResult
                {
                    EntryTime = TestSessionUtc.AddMinutes(-(index + 1) * 30),
                    ExitTime = TestSessionUtc.AddMinutes(index),
                    Side = "Buy",
                    StrategyTag = strategyTag,
                    RegimeTag = regimeTag,
                    ExecutionMode = "SIMULATED",
                    PromotionStage = "Backtest",
                    EntryPrice = 100m,
                    ExitPrice = 108m,
                    PnL = index < 6 ? 12m : -4m,
                    PnLPercent = index < 6 ? 12m : -4m
                })
                .ToList()
        });

        _promotionService.RecordPaperState(Enumerable.Range(0, 8)
            .Select(index => new PaperPosition
            {
                Symbol = symbol,
                Side = "Buy",
                EntryPrice = 100m,
                CurrentPrice = 100m,
                Quantity = 1m,
                RealizedPnL = index < 6 ? 10m : -3m,
                ExitTime = TestSessionUtc.AddMinutes(index),
                StrategyTag = strategyTag,
                RegimeTag = regimeTag
            })
            .ToList());
    }
}
