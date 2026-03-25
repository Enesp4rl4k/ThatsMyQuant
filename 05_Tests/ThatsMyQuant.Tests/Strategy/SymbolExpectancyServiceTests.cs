using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
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

    [Fact]
    public void Evaluate_ShouldUseSessionBucketBeforeGlobalSymbolHistory()
    {
        var signal = new Signal
        {
            Symbol = "THYAO",
            Timestamp = new DateTime(2026, 03, 21, 8, 30, 0, DateTimeKind.Utc)
        };

        var trades = new[]
        {
            CreateTrade("THYAO", -120m, new DateTime(2026, 03, 20, 8, 0, 0, DateTimeKind.Utc)),
            CreateTrade("THYAO", -110m, new DateTime(2026, 03, 19, 8, 15, 0, DateTimeKind.Utc)),
            CreateTrade("THYAO", -90m, new DateTime(2026, 03, 18, 8, 20, 0, DateTimeKind.Utc)),
            CreateTrade("THYAO", -80m, new DateTime(2026, 03, 17, 8, 25, 0, DateTimeKind.Utc)),
            CreateTrade("THYAO", 140m, new DateTime(2026, 03, 20, 2, 0, 0, DateTimeKind.Utc)),
            CreateTrade("THYAO", 130m, new DateTime(2026, 03, 19, 2, 0, 0, DateTimeKind.Utc)),
            CreateTrade("THYAO", 120m, new DateTime(2026, 03, 18, 2, 0, 0, DateTimeKind.Utc)),
            CreateTrade("THYAO", 110m, new DateTime(2026, 03, 17, 2, 0, 0, DateTimeKind.Utc))
        };

        var result = _service.Evaluate(trades, signal);

        result.Approved.Should().BeFalse();
        result.Reason.Should().Contain("blocked");
    }

    private static Trade CreateTrade(string symbol, decimal pnl, DateTime? exitTime = null) => new()
    {
        Symbol = symbol,
        Side = TradeSide.Buy,
        EntryPrice = 100m,
        ExitPrice = 100m + pnl,
        Quantity = 1m,
        Status = TradeStatus.Executed,
        ExitTime = exitTime ?? DateTime.UtcNow.AddHours(-1)
    };
}
