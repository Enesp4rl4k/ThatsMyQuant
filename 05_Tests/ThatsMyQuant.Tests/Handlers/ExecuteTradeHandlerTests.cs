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
    private readonly HftExecutionService _hftExecutionService;
    private readonly ExecuteTradeHandler _handler;

    public ExecuteTradeHandlerTests()
    {
        _logger = Substitute.For<ILogger<ExecuteTradeHandler>>();
        _exchangeService = Substitute.For<IExchangeService>();
        _tradeRepository = Substitute.For<ITradeRepository>();
        _hftExecutionService = new HftExecutionService(_exchangeService, Substitute.For<ILogger<HftExecutionService>>());

        _handler = new ExecuteTradeHandler(_logger, _exchangeService, _tradeRepository, _hftExecutionService);
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
