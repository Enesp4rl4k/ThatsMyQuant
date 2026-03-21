using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class SymbolExpectancyServiceTests
{
    private readonly SymbolExpectancyService _service = new();

    [Fact]
    public void Evaluate_ShouldBlock_WhenSymbolEdgeIsNegative()
    {
        var signal = new Signal { Symbol = "THYAO" };
        var trades = new[]
        {
            CreateTrade("THYAO", -120m),
            CreateTrade("THYAO", -90m),
            CreateTrade("THYAO", -80m),
            CreateTrade("THYAO", -70m),
            CreateTrade("THYAO", 10m)
        };

        var result = _service.Evaluate(trades, signal);

        result.Approved.Should().BeFalse();
        result.Reason.Should().Contain("blocked");
    }

    [Fact]
    public void Evaluate_ShouldReduceSize_WhenSymbolEdgeIsSoft()
    {
        var signal = new Signal { Symbol = "THYAO" };
        var trades = new[]
        {
            CreateTrade("THYAO", 60m),
            CreateTrade("THYAO", 55m),
            CreateTrade("THYAO", -40m),
            CreateTrade("THYAO", -40m),
            CreateTrade("THYAO", -40m)
        };

        var result = _service.Evaluate(trades, signal);

        result.Approved.Should().BeTrue();
        result.SizeMultiplier.Should().BeLessThan(1m);
    }

    private static Trade CreateTrade(string symbol, decimal pnl) => new()
    {
        Symbol = symbol,
        Side = TradeSide.Buy,
        EntryPrice = 100m,
        ExitPrice = 100m + pnl,
        Quantity = 1m,
        Status = TradeStatus.Executed,
        ExitTime = DateTime.UtcNow.AddHours(-1)
    };
}
