using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ThatsMyQuant.Application.Commands;
using ThatsMyQuant.Application.Handlers;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using ThatsMyQuant.Domain.Interfaces;
using Xunit;

namespace ThatsMyQuant.Tests.Handlers;

public class ExecuteTradeHandlerTests
{
    private readonly ILogger<ExecuteTradeHandler> _logger;
    private readonly IExchangeService _exchangeService;
    private readonly ITradeRepository _tradeRepository;
    private readonly ExecuteTradeHandler _handler;

    public ExecuteTradeHandlerTests()
    {
        _logger = Substitute.For<ILogger<ExecuteTradeHandler>>();
        _exchangeService = Substitute.For<IExchangeService>();
        _tradeRepository = Substitute.For<ITradeRepository>();

        _handler = new ExecuteTradeHandler(_logger, _exchangeService, _tradeRepository);
    }


    [Fact]
    public async Task Handle_SuccessfulExecution_ShouldReturnTradeAndPersist()
    {
        // Arrange
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "GARAN",
            Side = TradeSide.Buy,
            Price = 50.00m,
            Source = "Manual"
        };

        var expectedTrade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            EntryPrice = signal.Price,
            Quantity = 100,
            EntryTime = DateTime.UtcNow
        };

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>()).Returns(expectedTrade);

        // Act
        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Symbol.Should().Be("GARAN");
        result.Status.Should().Be(TradeStatus.Executed);
        result.Side.Should().Be(TradeSide.Buy);
        result.EntrySlippagePercent.Should().Be(0);
        await _tradeRepository.Received(1).AddAsync(Arg.Any<Trade>());
    }

    [Fact]
    public async Task Handle_SuccessfulExecution_ShouldCaptureEntrySlippage()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Source = "Manual"
        };

        var expectedTrade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            EntryPrice = 101m,
            Quantity = 1,
            EntryTime = DateTime.UtcNow
        };

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>()).Returns(expectedTrade);

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        result.EntrySlippagePercent.Should().Be(1m);
    }

    [Fact]
    public async Task Handle_SuccessfulExecution_ShouldPersistEdgeScoreMetadata()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Source = "Manual"
        };
        signal.Metadata["EdgeScore"] = "82.50";
        signal.Metadata["EdgeBand"] = "Reduced";

        var expectedTrade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            EntryPrice = 100m,
            Quantity = 1,
            EntryTime = DateTime.UtcNow
        };

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>()).Returns(expectedTrade);

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        result.EdgeScore.Should().Be(82.50m);
        result.EdgeBand.Should().Be("Reduced");
        await _tradeRepository.Received(1).AddAsync(Arg.Is<Trade>(trade =>
            trade.EdgeScore == 82.50m &&
            trade.EdgeBand == "Reduced"));
    }

    [Fact]
    public async Task Handle_SuccessfulExecution_ShouldPersistVenueImpactLabel()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Source = "Manual"
        };
        signal.Metadata["VenueImpactLabel"] = "Venue capped size (0.96x)";
        signal.Metadata["VenueExecutionScope"] = "Binance execution is softer than target";

        var expectedTrade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            EntryPrice = 100m,
            Quantity = 1,
            EntryTime = DateTime.UtcNow
        };

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>()).Returns(expectedTrade);

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        result.VenueImpactLabel.Should().Be("Venue capped size (0.96x)");
        result.VenueExecutionScope.Should().Be("Binance execution is softer than target");
        await _tradeRepository.Received(1).AddAsync(Arg.Is<Trade>(trade =>
            trade.VenueImpactLabel == "Venue capped size (0.96x)" &&
            trade.VenueExecutionScope == "Binance execution is softer than target"));
    }

    [Fact]
    public async Task Handle_SuccessfulExecution_ShouldPersistExactBucketPressure()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Source = "Manual"
        };
        signal.Metadata["ExactBucketImpactLabel"] = "Exact bucket supports scaling";
        signal.Metadata["ExactBucketSummary"] = "Exact bucket is healthy: PF 1.44, WR 58.0%, EV 2.35.";

        var expectedTrade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            EntryPrice = 100m,
            Quantity = 1,
            EntryTime = DateTime.UtcNow
        };

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>()).Returns(expectedTrade);

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        result.ExactBucketImpactLabel.Should().Be("Exact bucket supports scaling");
        result.ExactBucketSummary.Should().Be("Exact bucket is healthy: PF 1.44, WR 58.0%, EV 2.35.");
        await _tradeRepository.Received(1).AddAsync(Arg.Is<Trade>(trade =>
            trade.ExactBucketImpactLabel == "Exact bucket supports scaling" &&
            trade.ExactBucketSummary == "Exact bucket is healthy: PF 1.44, WR 58.0%, EV 2.35."));
    }

    [Fact]
    public async Task Handle_SuccessfulExecution_ShouldPersistConcordanceSignal()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Source = "Manual"
        };
        signal.Metadata["ConcordanceImpactLabel"] = "Concordance supports scaling";
        signal.Metadata["ConcordanceSummary"] = "Exact bucket edge and venue execution are aligned for scaled deployment.";

        var expectedTrade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            EntryPrice = 100m,
            Quantity = 1,
            EntryTime = DateTime.UtcNow
        };

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>()).Returns(expectedTrade);

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        result.ConcordanceImpactLabel.Should().Be("Concordance supports scaling");
        result.ConcordanceSummary.Should().Be("Exact bucket edge and venue execution are aligned for scaled deployment.");
        await _tradeRepository.Received(1).AddAsync(Arg.Is<Trade>(trade =>
            trade.ConcordanceImpactLabel == "Concordance supports scaling" &&
            trade.ConcordanceSummary == "Exact bucket edge and venue execution are aligned for scaled deployment."));
    }

    [Fact]
    public async Task Handle_SuccessfulExecution_ShouldPersistExitPolicyTag()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Source = "Manual"
        };
        signal.Metadata["ExitPolicyTag"] = "MeanReversion";

        var expectedTrade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            EntryPrice = 100m,
            Quantity = 1,
            EntryTime = DateTime.UtcNow
        };

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>()).Returns(expectedTrade);

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        result.ExitPolicyTag.Should().Be("MeanReversion");
        await _tradeRepository.Received(1).AddAsync(Arg.Is<Trade>(trade =>
            trade.ExitPolicyTag == "MeanReversion"));
    }

    [Fact]
    public async Task Handle_HftSignalWithWeakLiquidity_ShouldReturnFailedTrade()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Source = "HFT-Probe",
            Volume24h = 100_000m
        };
        signal.Metadata["ExecutionMode"] = "HFT";

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        result.Status.Should().Be(TradeStatus.Failed);
        await _exchangeService.DidNotReceive().ExecuteOrderAsync(Arg.Any<Signal>());
    }

    [Fact]
    public async Task Handle_ExchangeFailure_ShouldPersistFailedTradeAndNotThrow()
    {
        // Arrange
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "THYAO",
            Side = TradeSide.Sell,
            Price = 300.00m,
            Source = "TradingView"
        };

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>())
            .ThrowsAsync(new Exception("Broker connection lost"));

        // Act
        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(TradeStatus.Failed);
        result.Symbol.Should().Be("THYAO");
        result.Quantity.Should().Be(0); // Failed trade, no quantity executed
        await _tradeRepository.Received(1).AddAsync(Arg.Any<Trade>());
    }
}
