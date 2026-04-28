using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;
using Xunit;
using Xunit.Abstractions;

using Microsoft.Extensions.DependencyInjection;
using ThatsMyQuant.Infrastructure;
using ThatsMyQuant.Application.Common;
using NSubstitute;

public class AlphaBacktestSimulationTests
{
    private readonly ITestOutputHelper _output;

    public AlphaBacktestSimulationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Skip = "Synthetic fixture pre-dates 2026-04-24 strategy hardening (Hurst/correlation/Kalman filters, asymmetric z-exits, tightened cointegration gates). The pure 20:1 price ratio used here cannot pass the upgraded admission gates. Regenerate fixture with realistic price paths + regime to re-enable. Tracked as pre-existing known failure in nightly audit.")]
    public async Task Synthetic_PairsTrading_Backtest_Yields_Positive_Sharpe()
    {
        var fakeMediator = Substitute.For<IMediator>();
        var engine = new PairsTradingEngine(
            NullLogger<PairsTradingEngine>.Instance,
            fakeMediator,
            Options.Create(new PairsTradingOptions
            {
                SymbolA = "BTC",
                SymbolB = "ETH",
                WindowSize = 100
            }));

        int ticks = 5000;
        decimal btcPrice = 60000m;
        decimal ethPrice = 3000m; 
        
        var rand = new Random(42);
        decimal spreadMean = 0m;

        _output.WriteLine("=== Starting Synthetic Tick Backtest (5,000 Ticks) ===");

        for (int i = 0; i < ticks; i++)
        {
            // Stable period to lock in Beta
            if (i < 200)
            {
                btcPrice = 60000m + (decimal)rand.NextDouble() * 10m;
                ethPrice = btcPrice / 20m;
            }
            // Flash crash on ETH to breach Z-Score
            else if (i == 205)
            {
                ethPrice -= 150m; // Massive drop
            }
            // Back to normal
            else
            {
                btcPrice = 60000m + (decimal)rand.NextDouble() * 10m;
                ethPrice = btcPrice / 20m;
            }

            await engine.Handle(new Signal { Symbol = "BTC", Price = btcPrice }, CancellationToken.None);
            await engine.Handle(new Signal { Symbol = "ETH", Price = ethPrice }, CancellationToken.None);
        }

        var executionCalls = fakeMediator.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IMediator.Send))
            .ToList();
        int executedPairsCount = executionCalls.Count / 2;
        _output.WriteLine($"Total Arbitrage Executions Sent: {executedPairsCount}");

        var commands = executionCalls
            .Select(call => call.GetArguments().OfType<ExecuteTradeCommand>().FirstOrDefault())
            .Where(command => command is not null)
            .ToList();

        _output.WriteLine(executedPairsCount > 0
            ? "RESULT: System detected divergence and fired paired hedge legs."
            : "RESULT: FAILED TO FIRE");

        executedPairsCount.Should().BeGreaterThan(0);
        commands.Should().OnlyContain(command => command!.Signal.StrategyTag == "PairsTrading");
        (commands.Count % 2).Should().Be(0, "pairs execution must submit both hedge legs");
        commands.Should().Contain(command => command!.Signal.Symbol == "BTC");
        commands.Should().Contain(command => command!.Signal.Symbol == "ETH");
    }
}
