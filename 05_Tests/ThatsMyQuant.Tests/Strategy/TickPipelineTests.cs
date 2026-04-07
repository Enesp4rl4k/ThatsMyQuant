using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ThatsMyQuant.Application.HftInfrastructure;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class TickPipelineTests
{
    [Fact]
    public async Task Start_WhenTickArrives_LogsTickAndRecordsHeartbeat()
    {
        var streamingService = Substitute.For<IStreamingMarketDataService>();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ProcessSignalCommand>(), Arg.Any<CancellationToken>()).Returns(true);
        var tickLogger = Substitute.For<ITickLogger>();
        var auditLogger = Substitute.For<IAuditLogger>();
        var deadMansSwitch = new DeadMansSwitch(auditLogger);
        var heartbeatMonitor = new HeartbeatMonitor(deadMansSwitch, maxStaleMilliseconds: 1000);

        await using var sut = new TickPipeline(
            streamingService,
            NullLogger<TickPipeline>.Instance,
            mediator,
            new FeatureEngineeringAgent(),
            tickLogger,
            heartbeatMonitor);

        using var cts = new CancellationTokenSource();
        sut.Start(cts.Token);

        var tick = new MarketTick("BTCUSDT", 65000m, 0.25m, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), false);
        streamingService.OnTickReceived += Raise.Event<Action<MarketTick>>(tick);

        await Task.Delay(150);

        await tickLogger.Received(1).LogTickAsync(Arg.Is<MarketTick>(logged => logged.Symbol == "BTCUSDT"));

        heartbeatMonitor.AuditConnection();
        deadMansSwitch.IsTriggered.Should().BeFalse();
    }

    [Fact]
    public async Task Start_WhenOrderBookArrives_DispatchesProcessSignalCommand()
    {
        var streamingService = Substitute.For<IStreamingMarketDataService>();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ProcessSignalCommand>(), Arg.Any<CancellationToken>()).Returns(true);
        var tickLogger = Substitute.For<ITickLogger>();
        var auditLogger = Substitute.For<IAuditLogger>();
        var heartbeatMonitor = new HeartbeatMonitor(new DeadMansSwitch(auditLogger), maxStaleMilliseconds: 1000);

        await using var sut = new TickPipeline(
            streamingService,
            NullLogger<TickPipeline>.Instance,
            mediator,
            new FeatureEngineeringAgent(),
            tickLogger,
            heartbeatMonitor);

        using var cts = new CancellationTokenSource();
        sut.Start(cts.Token);

        var orderBook = new OrderBookSnapshot(
            "BTCUSDT",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            [new OrderBookLevel(64999m, 2m)],
            [new OrderBookLevel(65001m, 1m)]);

        streamingService.OnOrderBookUpdated += Raise.Event<Action<OrderBookSnapshot>>(orderBook);

        await Task.Delay(150);

        await mediator.Received(1).Send(
            Arg.Is<ProcessSignalCommand>(command =>
                command.Signal.Symbol == "BTCUSDT" &&
                command.Signal.BestBid > 0 &&
                command.Signal.BestAsk > 0),
            Arg.Any<CancellationToken>());
    }
}
