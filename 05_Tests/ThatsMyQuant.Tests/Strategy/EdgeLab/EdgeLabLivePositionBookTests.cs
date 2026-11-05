using ThatsMyQuant.Application.Strategy.EdgeLab;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy.EdgeLab;

public class EdgeLabLivePositionBookTests
{
    [Fact]
    public void TwoStrategiesOnOneSymbol_EachSeesOnlyItsOwnShare()
    {
        var book = new EdgeLabLivePositionBook();
        book.Register("eth_1h", "ETHUSDT");
        book.Register("t50e2_eth", "ETHUSDT");

        // eth_1h buys 0.5 ETH, then the basket sells 0.03 ETH; exchange net = +0.47
        Assert.Equal(0m, book.Resolve("eth_1h", "ETHUSDT", 0m, out _));
        book.ApplyFill("eth_1h", "ETHUSDT", 0.5m);
        Assert.Equal(0m, book.Resolve("t50e2_eth", "ETHUSDT", 0.5m, out var w1));   // seeded as 0.5 - 0.5
        Assert.Null(w1);
        book.ApplyFill("t50e2_eth", "ETHUSDT", -0.03m);

        Assert.Equal(0.5m, book.Resolve("eth_1h", "ETHUSDT", 0.47m, out var w2));
        Assert.Equal(-0.03m, book.Resolve("t50e2_eth", "ETHUSDT", 0.47m, out _));
        Assert.Null(w2);
    }

    [Fact]
    public void SingleOwner_SelfHealsToExchangePosition()
    {
        var book = new EdgeLabLivePositionBook();
        book.Register("t50e2_btc", "BTCUSDT");
        // position opened before the book existed (or a fill the book missed)
        Assert.Equal(-0.0012m, book.Resolve("t50e2_btc", "BTCUSDT", -0.0012m, out _));
        book.ApplyFill("t50e2_btc", "BTCUSDT", -0.0005m);                  // book drifts
        Assert.Equal(-0.0012m, book.Resolve("t50e2_btc", "BTCUSDT", -0.0012m, out _)); // exchange wins
    }

    [Fact]
    public void FlatOnExchange_ResetsEveryOwner()
    {
        var book = new EdgeLabLivePositionBook();
        book.Register("a", "SOLUSDT"); book.Register("b", "SOLUSDT");
        book.ApplyFill("a", "SOLUSDT", 2m); book.ApplyFill("b", "SOLUSDT", -1m);
        Assert.Equal(0m, book.Resolve("a", "SOLUSDT", 0m, out _));          // manual close / liquidation
        Assert.Equal(0m, book.Get("b", "SOLUSDT"));
    }

    [Fact]
    public void SharedSymbol_DivergenceFromExchange_ReturnsWarning()
    {
        var book = new EdgeLabLivePositionBook();
        book.Register("a", "ETHUSDT"); book.Register("b", "ETHUSDT");
        book.ApplyFill("a", "ETHUSDT", 1m); book.ApplyFill("b", "ETHUSDT", 1m);
        var share = book.Resolve("a", "ETHUSDT", 1.5m, out var warning);     // exchange disagrees
        Assert.Equal(1m, share);
        Assert.NotNull(warning);
    }

    [Fact]
    public void PersistsAcrossRestarts()
    {
        var path = Path.Combine(Path.GetTempPath(), "tmq-book-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var book = new EdgeLabLivePositionBook(path);
            book.Register("a", "ETHUSDT"); book.Register("b", "ETHUSDT");
            book.ApplyFill("a", "ETHUSDT", 0.4m);
            var reloaded = new EdgeLabLivePositionBook(path);
            Assert.Equal(0.4m, reloaded.Get("a", "ETHUSDT"));
            Assert.Equal(2, reloaded.OwnersOf("ETHUSDT").Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
