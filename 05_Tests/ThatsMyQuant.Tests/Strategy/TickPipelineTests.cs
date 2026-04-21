using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.Exceptions;
using System.Diagnostics;
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
    private static async Task WaitForAssertionAsync(Func<Task> assertion, int timeoutMs = 3000, int pollMs = 50)
    {
        var timeout = TimeSpan.FromMilliseconds(timeoutMs);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                await assertion();
                return;
            }
            catch (ReceivedCallsException) when (stopwatch.Elapsed < timeout)
            {
                await Task.Delay(Math.Min(pollMs, 25));
            }
            catch (OperationCanceledException) when (stopwatch.Elapsed < timeout)
            {
                await Task.Delay(Math.Min(pollMs, 25));
            }
        }
    }

    private static DeadMansSwitch CreateDeadMansSwitch(IAuditLogger auditLogger)
    {
        var portfolio = Substitute.For<IPortfolioManager>();
        portfolio.GetDrawdownPercent().Returns(0m);

        var circuitBreaker = new CircuitBreakerService(
            NullLogger<CircuitBreakerService>.Instance,
            portfolio,
            maxDrawdownPercent: 50m,
            maxConsecutiveErrors: 5,
            maxOrdersPerSecond: 100);

        return new DeadMansSwitch(auditLogger, circuitBreaker, NullLogger<DeadMansSwitch>.Instance);
    }

    [Fact]
    public async Task Start_WhenTickArrives_LogsTickAndRecordsHeartbeat()
    {
        var streamingService = Substitute.For<IStreamingMarketDataService>();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ProcessSignalCommand>(), Arg.Any<CancellationToken>()).Returns(true);
        var tickLogger = Substitute.For<ITickLogger>();
        var auditLogger = Substitute.For<IAuditLogger>();
        var deadMansSwitch = CreateDeadMansSwitch(auditLogger);
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

        await WaitForAssertionAsync(async () => await tickLogger.Received(1).LogTickAsync(
            Arg.Is<MarketTick>(logged => logged.Symbol == "BTCUSDT")));

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
        var heartbeatMonitor = new HeartbeatMonitor(CreateDeadMansSwitch(auditLogger), maxStaleMilliseconds: 1000);

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

        await WaitForAssertionAsync(() => mediator.Received(1).Send(
            Arg.Is<ProcessSignalCommand>(command =>
                command.Signal.Symbol == "BTCUSDT" &&
                command.Signal.BestBid > 0 &&
                command.Signal.BestAsk > 0),
            Arg.Any<CancellationToken>()));
    }
}
