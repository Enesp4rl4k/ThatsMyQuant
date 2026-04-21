using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;
using Xunit;
using Xunit.Abstractions;
using NSubstitute;

namespace ThatsMyQuant.Tests.Strategy;

public class MultiStrategyEventBacktestTests
{
    private readonly ITestOutputHelper _output;

    public MultiStrategyEventBacktestTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task EventDriven_BacktestEngine_Routes_L2Ticks_To_All_Agents_Without_Mocks()
    {
        _output.WriteLine("=== Initializing True Event-Driven Multi-Strategy Backtest Engine ===");

        // 1. Setup TRUE Dependency Injection (No FakeMediator mocking)
        var services = new ServiceCollection();

        // Register core plumbing
        services.AddLogging(builder => builder.AddProvider(new XUnitLoggerProvider(_output)).SetMinimumLevel(LogLevel.Debug));
        
        // Mock Portfolio for Test state check
        var mockPortfolio = Substitute.For<ISharedPortfolioStateManager>();
        mockPortfolio.GetAvailableBalance(VenueKind.Binance, "USDT").Returns(100_000m);
        services.AddSingleton<ISharedPortfolioStateManager>(mockPortfolio);

        // Register the Event Driven Engine as both the driver and the Interceptor (RequestHandler)
        services.AddSingleton<EventDrivenBacktestEngine>();
        services.AddTransient<IRequestHandler<ExecuteTradeCommand, Trade>>(sp => sp.GetRequiredService<EventDrivenBacktestEngine>());

        // Register Real Agents using Reflection/MediatR
        services.AddMediatR(cfg => {
            cfg.RegisterServicesFromAssembly(typeof(ArbitrageAgent).Assembly);
            cfg.RegisterServicesFromAssembly(typeof(MultiStrategyEventBacktestTests).Assembly);
        });

        // Add specific Agents manually if assembly scan missed due to interfaces
        services.AddTransient<ArbitrageAgent>();

        var provider = services.BuildServiceProvider();

        // 2. Resolve the Event-Driven Engine
        var engine = provider.GetRequiredService<EventDrivenBacktestEngine>();

        // 3. Generate high-frequency L2 Tick Data (Chronological simulation)
        var simulatedTicks = GenerateHftTickSimulation();

        // 4. BLAST TICK DATA - The engine will handle the heavy lifting
        await engine.RunTickReplaySessionAsync(simulatedTicks, 100_000m);

        // 5. Verification: Did ArbitrageAgent or PairsTrading pickup the events?
        _output.WriteLine($"Total EXECUTED Trades Trapped by Engine: {engine.ExecutedTrades.Count}");
        
        // Assert that the real pipeline triggered trades
        Assert.True(engine.ExecutedTrades.Count > 0, "Agents failed to capture any trades during the Event-Driven Replay.");
    }

    private IEnumerable<MarketTick> GenerateHftTickSimulation()
    {
        var rand = new Random(42);
        long startMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 3600_000;
        
        for (int i = 0; i < 500; i++)
        {
            // Simulate stable pricing
            double baseBtc = 60000 + (rand.NextDouble() * 10);
            
            // Binance
            yield return new MarketTick
            {
                Symbol = "BTC-Binance",
                Price = (decimal)baseBtc,
                TimestampMs = startMs + i,
            };

            // OKX (Inject massive flash arb opportunity at tick 250)
            double baseOkx = i == 250 ? baseBtc - 500 : baseBtc; // $500 flash crash
            
            yield return new MarketTick
            {
                Symbol = "BTC-OKX",
                Price = (decimal)baseOkx,
                TimestampMs = startMs + i,
            };
        }
    }

    // Quick XUnit Logger
    private class XUnitLoggerProvider : ILoggerProvider
    {
        private readonly ITestOutputHelper _output;
        public XUnitLoggerProvider(ITestOutputHelper output) => _output = output;
        public ILogger CreateLogger(string categoryName) => new XUnitLogger(_output);
        public void Dispose() {}
    }

    private class XUnitLogger : ILogger
    {
        private readonly ITestOutputHelper _output;
        public XUnitLogger(ITestOutputHelper output) => _output = output;
        public IDisposable BeginScope<TState>(TState state) => null!;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _output.WriteLine($"[{logLevel}] {formatter(state, exception)}");
        }
    }
}
