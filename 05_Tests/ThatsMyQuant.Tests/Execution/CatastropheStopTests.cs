using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Application.Strategy.EdgeLab;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Services;
using Xunit;

namespace ThatsMyQuant.Tests.Execution;

/// <summary>2026-11-06: 15% mark-price catastrophe stop, isolated 4x, no re-entry after a stop-out.</summary>
public class CatastropheStopTests
{
    [Fact]
    public void DesiredTrigger_RoundsAwayFromPrice()
    {
        CatastropheStopPlanner.DesiredTrigger(100m, true, 0.15m, 0.1m).Should().Be(85.0m);
        CatastropheStopPlanner.DesiredTrigger(100.07m, true, 0.15m, 0.1m).Should().Be(85.0m);    // 85.0595 -> floor
        CatastropheStopPlanner.DesiredTrigger(100.07m, false, 0.15m, 0.1m).Should().Be(115.1m);  // 115.0805 -> ceil
    }

    private static VenueStopOrder Stop(string sym, decimal px, bool closesLong, string id = "a1")
        => new(sym, id, "tmqcs" + id, px, closesLong, DateTime.UtcNow);

    [Fact]
    public void Reconcile_PlacesMissing_KeepsCorrect_ReplacesMoved_CancelsStale()
    {
        var positions = new List<ExchangePositionSnapshot>
        {
            new("BTCUSDT", 0.01m, false, 80000m),   // short, no stop yet
            new("ETHUSDT", 0.1m, true, 2000m),      // long, correct stop
            new("SOLUSDT", 1m, false, 100m),        // short, stop at an old entry
        };
        var stops = new List<VenueStopOrder>
        {
            Stop("ETHUSDT", 1700m, true, "e"),
            Stop("SOLUSDT", 105m, false, "s"),
            Stop("DOGEUSDT", 0.2m, true, "d"),      // position gone
        };
        var desired = new Dictionary<string, decimal> { ["BTCUSDT"] = 92000m, ["ETHUSDT"] = 1700m, ["SOLUSDT"] = 115m };

        var plan = CatastropheStopPlanner.Reconcile(positions, stops, desired);

        plan.Cancel.Select(s => s.AlgoId).Should().BeEquivalentTo(new[] { "s", "d" });
        plan.Place.Should().BeEquivalentTo(new[] { ("BTCUSDT", false, 92000m), ("SOLUSDT", false, 115m) });
    }

    [Fact]
    public void Reconcile_WrongSideStop_IsReplaced()
    {
        var plan = CatastropheStopPlanner.Reconcile(
            new List<ExchangePositionSnapshot> { new("ETHUSDT", 0.1m, true, 2000m) },
            new List<VenueStopOrder> { Stop("ETHUSDT", 1700m, closesLong: false) },
            new Dictionary<string, decimal> { ["ETHUSDT"] = 1700m });
        plan.Cancel.Should().HaveCount(1);
        plan.Place.Should().ContainSingle();
    }

    [Fact]
    public void StopBlock_HoldsStoppedSide_UntilSignalFlatOrFlips()
    {
        var b = new EdgeLabStopBlock();
        b.Block("ETHUSDT", 1);                                  // a long was stopped out
        b.Apply("ETHUSDT", 250m, out var blocked).Should().Be(0m);
        blocked.Should().BeTrue();
        b.Apply("ETHUSDT", 300m, out _).Should().Be(0m);        // still long -> still blocked
        b.Apply("ETHUSDT", 0m, out blocked).Should().Be(0m);    // signal went flat -> block cleared
        blocked.Should().BeFalse();
        b.Apply("ETHUSDT", 250m, out blocked).Should().Be(250m);
        blocked.Should().BeFalse();

        b.Block("BTCUSDT", -1);
        b.Apply("BTCUSDT", 40m, out blocked).Should().Be(40m);  // flipped to long -> allowed, block cleared
        b.IsBlocked("BTCUSDT").Should().BeFalse();
    }

    private sealed class Capture : HttpMessageHandler
    {
        public readonly List<(string Path, string Body)> Calls = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls.Add((r.RequestUri!.AbsolutePath, r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":"0","data":[{"algoId":"1","sCode":"0"}]}""", Encoding.UTF8, "application/json")
            };
        }
    }

    private static OkxExecutionService Svc(Capture h) => new(Substitute.For<ILogger<OkxExecutionService>>(), new HttpClient(h),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OKX:SecretKey"] = "s", ["OKX:ApiKey"] = "k", ["OKX:Passphrase"] = "p", ["OKX:IsDemoMode"] = "true",
            ["TMQ_EXCHANGE_MODE"] = "OKX_SWAP", ["OrderExecution:SwapLeverage"] = "4", ["OrderExecution:CatastropheStopPct"] = "0.15",
        }).Build(), Substitute.For<ISharedPortfolioStateManager>());

    [Fact]
    public async Task PlaceStop_ClosesWholePosition_ReduceOnly_MarkTrigger()
    {
        var h = new Capture();
        (await Svc(h).PlaceCatastropheStopAsync("ETHUSDT", positionIsLong: true, triggerPx: 1700m)).Should().BeTrue();
        var (path, body) = h.Calls.Single();
        path.Should().EndWith("/trade/order-algo");
        body.Should().Contain("\"side\":\"sell\"").And.Contain("\"closeFraction\":\"1\"").And.Contain("\"reduceOnly\":true")
            .And.Contain("\"slTriggerPxType\":\"mark\"").And.Contain("\"slOrdPx\":\"-1\"").And.Contain("\"slTriggerPx\":\"1700\"")
            .And.Contain("\"algoClOrdId\":\"tmqcs");
    }

    [Fact]
    public async Task EnsureLeverage_SetsIsolatedOnce_PerInstrument()
    {
        var h = new Capture();
        var svc = Svc(h);
        await svc.EnsureLeverageAsync("BTCUSDT");
        await svc.EnsureLeverageAsync("BTCUSDT");
        await svc.EnsureLeverageAsync("ETHUSDT");
        h.Calls.Should().HaveCount(2);
        h.Calls[0].Path.Should().EndWith("/account/set-leverage");
        h.Calls[0].Body.Should().Contain("\"lever\":\"4\"").And.Contain("\"mgnMode\":\"isolated\"");
    }

    [Theory]
    [InlineData(14.56, 2.04, 4, 1.60)]   // ARB on 2026-11-12: effective 7.1x -> add 1.60 to get back to 3.64
    [InlineData(22.30, 5.58, 4, 0.0)]    // healthy 4x position: nothing to add
    [InlineData(20.00, 4.60, 4, 0.0)]    // within 10% of the target margin: left alone
    public void MarginShortfall_RestoresNotionalOverLeverage(double notional, double margin, double lever, double expected)
        => OkxExecutionService.MarginShortfall((decimal)notional, (decimal)margin, (decimal)lever).Should().Be((decimal)expected);

    private sealed class PositionsHandler : HttpMessageHandler
    {
        public readonly List<(string Path, string Body)> Posts = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var json = """{"code":"0","data":[{"sCode":"0"}]}""";
            if (r.Method == HttpMethod.Get)
                json = """
                    {"code":"0","data":[
                      {"instId":"ARB-USDT-SWAP","mgnMode":"isolated","notionalUsd":"14.56","margin":"2.04","lever":"4","pos":"-100"},
                      {"instId":"ETH-USDT-SWAP","mgnMode":"isolated","notionalUsd":"36.2","margin":"9.54","lever":"4","pos":"-0.17"},
                      {"instId":"BTC-USDT-SWAP","mgnMode":"cross","notionalUsd":"40","margin":"1","lever":"4","pos":"-0.0005"}]}
                    """;
            else
                Posts.Add((r.RequestUri!.AbsolutePath, await r.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task TopUp_AddsMargin_OnlyToShortIsolatedPositions()
    {
        var h = new PositionsHandler();
        var svc = new OkxExecutionService(Substitute.For<ILogger<OkxExecutionService>>(), new HttpClient(h),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OKX:SecretKey"] = "s", ["OKX:ApiKey"] = "k", ["OKX:Passphrase"] = "p", ["OKX:IsDemoMode"] = "true", ["TMQ_EXCHANGE_MODE"] = "OKX_SWAP",
            }).Build(), Substitute.For<ISharedPortfolioStateManager>());

        (await svc.TopUpIsolatedMarginAsync()).Should().Be(1);
        var (path, body) = h.Posts.Single();
        path.Should().EndWith("/account/position/margin-balance");
        body.Should().Contain("\"instId\":\"ARB-USDT-SWAP\"").And.Contain("\"type\":\"add\"").And.Contain("\"amt\":\"1.60\"");
    }
}
