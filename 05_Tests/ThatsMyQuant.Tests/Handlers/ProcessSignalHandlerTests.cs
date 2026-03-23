using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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
    private readonly ISignalRepository _signalRepository;
    private readonly ITradeRepository _tradeRepository;
    private readonly ITechnicalAnalysisService _technicalAnalysis;
    private readonly RiskAgent _riskAgent;
    private readonly IConfiguration _configuration;
    private readonly ProcessSignalHandler _handler;

    public ProcessSignalHandlerTests()
    {
        // Override market hours for testing
        MarketHoursScheduler.OverrideCanTrade = true;

        _logger = Substitute.For<ILogger<ProcessSignalHandler>>();
        _mediator = Substitute.For<IMediator>();
        _aiService = Substitute.For<IAiReasoningService>();
        _marketDataService = Substitute.For<IMarketDataService>();
        _signalRepository = Substitute.For<ISignalRepository>();
        _tradeRepository = Substitute.For<ITradeRepository>();
        _technicalAnalysis = Substitute.For<ITechnicalAnalysisService>();
        _riskAgent = new RiskAgent(Substitute.For<ILogger<RiskAgent>>());
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Execution:UseWorkerHandoff"] = "false"
            })
            .Build();
        _tradeRepository.GetAllAsync(Arg.Any<int>()).Returns(Array.Empty<Trade>());

        _handler = new ProcessSignalHandler(
            _logger, _mediator, _aiService, _marketDataService,
            _signalRepository, _tradeRepository, _technicalAnalysis, _riskAgent, _configuration);
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
        Source = "TradingView"
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
                PositionSizePercent = 0.03m
            });
        _aiService.ShouldExecuteAsync(Arg.Any<Signal>()).Returns(true);

        _mediator.Send(Arg.Any<SwarmNegotiationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SwarmConsensusResult
            {
                Approved = true,
                AdjustedKellyFraction = 0.03m,
                AdjustedTakeProfit = 295m,
                ExecutionAction = ExecutionActionType.MarketTaker
            });

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

        _mediator.Send(Arg.Any<SwarmNegotiationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SwarmConsensusResult { Approved = false, RejectionFlags = SignalReasonFlags.SentimentVeto });

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
                PositionSizePercent = 0.04m
            });
        
        _mediator.Send(Arg.Any<SwarmNegotiationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SwarmConsensusResult { Approved = false, RejectionFlags = SignalReasonFlags.AiVeto });

        _aiService.ShouldExecuteAsync(Arg.Any<Signal>()).Returns(false);


        _aiService.GetReasoningAsync(Arg.Any<Signal>()).Returns("Negative news detected");

        // Act
        var result = await _handler.Handle(new ProcessSignalCommand(signal), CancellationToken.None);

        // Assert
        result.Should().BeFalse();
        signal.Status.Should().Be(TradeStatus.Rejected);
        signal.Reasoning.Should().Contain("Swarm Rejected: AiVeto");
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
}
