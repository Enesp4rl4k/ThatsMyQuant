using FluentAssertions;
using NSubstitute;
using ThatsMyQuant.Application.Strategy.EdgeLab;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Execution;

/// <summary>2026-11-20: on the OKX demo venue the catastrophe stop runs bot-side on the real market's mark price.</summary>
public class PaperCatastropheStopTests
{
    [Fact]
    public void EntryBook_NewAdd_Reduce_Flip_Flat()
    {
        var b = new LiveMarkEntryBook();
        b.Observe("ARBUSDT", -100m, 0.10m).Should().Be(0.10m);       // new short at the live mark
        b.Observe("ARBUSDT", -200m, 0.12m).Should().Be(0.11m);       // added: averages in
        b.Observe("ARBUSDT", -50m, 0.20m).Should().Be(0.11m);        // reduced: entry kept
        b.Observe("ARBUSDT", 30m, 0.13m).Should().Be(0.13m);         // flipped: restarts
        b.Observe("ARBUSDT", 0m, 0.13m).Should().BeNull();           // flat: forgotten
        b.Get("ARBUSDT").Should().BeNull();
    }

    [Fact]
    public void EntryBook_SurvivesRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tmq_live_entries_{Guid.NewGuid():N}.json");
        try
        {
            new LiveMarkEntryBook(path).Observe("BTCUSDT", -0.01m, 60000m);
            new LiveMarkEntryBook(path).Get("BTCUSDT")!.Price.Should().Be(60000m);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(true, 100, 85.0, 90, false)]      // long: low at the stop -> fires
    [InlineData(true, 100, 85.01, 90, true)]      // long: just above -> holds
    [InlineData(false, 100, 110, 115.0, false)]   // short: high at the stop -> fires
    [InlineData(false, 100, 110, 114.99, true)]   // short: just below -> holds
    public void Triggered_AtFifteenPercent(bool isLong, decimal entry, decimal low, decimal high, bool holds)
        => LiveMarkEntryBook.Triggered(isLong, entry, 0.15m, low, high).Should().Be(!holds);

    private static (ICatastropheStopVenue Venue, IExchangePositionReader Reader) Fakes(decimal shortQty)
    {
        var venue = Substitute.For<ICatastropheStopVenue>();
        venue.IsPaperVenue.Returns(true);
        venue.GetBotStopOrdersAsync(Arg.Any<CancellationToken>()).Returns(new List<VenueStopOrder>
        {
            new("ARBUSDT", "algo1", "tmqcsx", 0.1168m, false, DateTime.UtcNow),   // a stop left resting on the demo engine
        });
        venue.ClosePositionAtMarketAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(true);
        var reader = Substitute.For<IExchangePositionReader>();
        reader.TryGetOpenPositionsAsync(Arg.Any<CancellationToken>())
              .Returns((true, (IReadOnlyList<ExchangePositionSnapshot>)new List<ExchangePositionSnapshot> { new("ARBUSDT", shortQty, false, 0.09m) }));
        return (venue, reader);
    }

    [Fact]
    public async Task DemoWick_DoesNotFire_RealMoveDoes_AndBlocksReentry()
    {
        var (venue, reader) = Fakes(100m);
        var block = new EdgeLabStopBlock();
        var book = new LiveMarkEntryBook();
        var t0 = DateTime.UtcNow;

        // pass 1: the live market sits at 0.100 (the demo engine's 0.09 average entry is ignored)
        venue.GetLiveMarkRangeAsync("ARBUSDT", Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(((decimal, decimal)?)(0.0995m, 0.1005m));
        (await PaperCatastropheStop.RunAsync(venue, reader, block, book, 0.15m, t0)).Should().BeEmpty();
        book.Get("ARBUSDT")!.Price.Should().Be(0.1m);
        await venue.Received(1).CancelStopAsync("ARBUSDT", "algo1", Arg.Any<CancellationToken>());
        await venue.DidNotReceiveWithAnyArgs().PlaceCatastropheStopAsync(default!, default, default, default);

        // pass 2: the real mark rises 10% -> below the 15% stop at 0.115, nothing happens
        venue.GetLiveMarkRangeAsync("ARBUSDT", Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(((decimal, decimal)?)(0.105m, 0.110m));
        (await PaperCatastropheStop.RunAsync(venue, reader, block, book, 0.15m, t0.AddMinutes(1))).Should().BeEmpty();

        // pass 3: the real mark reaches 0.116 -> closes the short at market and blocks new shorts
        venue.GetLiveMarkRangeAsync("ARBUSDT", Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(((decimal, decimal)?)(0.110m, 0.116m));
        var fired = await PaperCatastropheStop.RunAsync(venue, reader, block, book, 0.15m, t0.AddMinutes(2));
        fired.Should().ContainSingle(f => f.Symbol == "ARBUSDT" && !f.WasLong);
        await venue.Received(1).ClosePositionAtMarketAsync("ARBUSDT", false, Arg.Any<CancellationToken>());
        block.Apply("ARBUSDT", -12m, out var blocked).Should().Be(0m);
        blocked.Should().BeTrue();
        block.Apply("ARBUSDT", 0m, out _);                        // signal goes flat -> block cleared
        block.IsBlocked("ARBUSDT").Should().BeFalse();
    }

    [Fact]
    public async Task FailedPositionRead_DoesNothing()
    {
        var venue = Substitute.For<ICatastropheStopVenue>();
        var reader = Substitute.For<IExchangePositionReader>();
        reader.TryGetOpenPositionsAsync(Arg.Any<CancellationToken>())
              .Returns((false, (IReadOnlyList<ExchangePositionSnapshot>)Array.Empty<ExchangePositionSnapshot>()));
        (await PaperCatastropheStop.RunAsync(venue, reader, new EdgeLabStopBlock(), new LiveMarkEntryBook(), 0.15m, DateTime.UtcNow)).Should().BeEmpty();
        await venue.DidNotReceiveWithAnyArgs().GetBotStopOrdersAsync(default);
        await venue.DidNotReceiveWithAnyArgs().ClosePositionAtMarketAsync(default!, default, default);
    }
}
