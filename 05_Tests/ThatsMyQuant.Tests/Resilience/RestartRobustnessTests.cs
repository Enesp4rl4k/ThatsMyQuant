using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Application.Observability;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Services;
using Xunit;

namespace ThatsMyQuant.Tests.Resilience;

/// <summary>2026-11-03: network stalls stopped the whole bot; orders from a previous run stayed live.</summary>
public class RestartRobustnessTests
{
    private static IConfiguration Config(params (string Key, string Value)[] extra)
    {
        var d = new Dictionary<string, string?>
        {
            ["OKX:SecretKey"] = "s", ["OKX:ApiKey"] = "k", ["OKX:Passphrase"] = "p", ["OKX:IsDemoMode"] = "true",
            ["TMQ_EXCHANGE_MODE"] = "OKX",
        };
        foreach (var (k, v) in extra) d[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    }

    [Fact]
    public async Task HealthProbe_HttpTimeout_DoesNotEndTheService()
    {
        var exchange = Substitute.For<IExchangeService>();
        exchange.GetAccountStatusAsync().Returns<Task<Account>>(_ =>
            throw new TaskCanceledException("HttpClient.Timeout of 100 seconds elapsing"));
        var health = Substitute.For<IExchangeHealthProvider>();
        var services = new ServiceCollection().AddSingleton(exchange).BuildServiceProvider();
        var probe = new ExchangeHealthProbeHostedService(
            Substitute.For<ILogger<ExchangeHealthProbeHostedService>>(), services, health, Config());

        await probe.StartAsync(CancellationToken.None);
        await Task.Delay(300);

        probe.ExecuteTask!.IsCompleted.Should().BeFalse("a timed-out probe must not end the service (StopHost stops the bot)");
        health.Received().RecordFailure(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>());
        await probe.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("adaf5c0c61d84a14b6837d14d5a9f977", true)]   // post_only
    [InlineData("cb6fc30931024b19b73ea0e92951e04M", true)]   // market fallback, attempt 1
    [InlineData("4afbebee405f4143ac9dfe50092b57M2", true)]   // market fallback, attempt 2
    [InlineData("", false)]                                  // placed by hand on the venue
    [InlineData(null, false)]
    [InlineData("my-manual-order", false)]
    public void IsBotClientOrderId_TellsOwnOrdersFromManualOnes(string? id, bool expected)
        => OkxExecutionService.IsBotClientOrderId(id).Should().Be(expected);

    private sealed class PendingHandler : HttpMessageHandler
    {
        public readonly List<string> CancelBodies = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string json;
            if (request.RequestUri!.AbsolutePath.EndsWith("/trade/orders-pending"))
            {
                json = """
                {"code":"0","data":[
                  {"instId":"BNB-USDT-SWAP","ordId":"bot1","clOrdId":"adaf5c0c61d84a14b6837d14d5a9f977"},
                  {"instId":"ADA-USDT-SWAP","ordId":"bot2","clOrdId":"4afbebee405f4143ac9dfe50092b57M2"},
                  {"instId":"BTC-USDT-SWAP","ordId":"manual","clOrdId":""}
                ]}
                """;
            }
            else
            {
                CancelBodies.Add(await request.Content!.ReadAsStringAsync(ct));
                json = """{"code":"0","data":[{"ordId":"x","sCode":"0"}]}""";
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task OrphanSweep_CancelsOwnOrders_LeavesManualOnes()
    {
        var handler = new PendingHandler();
        var svc = new OkxExecutionService(Substitute.For<ILogger<OkxExecutionService>>(), new HttpClient(handler),
            Config(("OKX:TradeMode", "swap")), Substitute.For<ISharedPortfolioStateManager>());

        var n = await svc.CancelOwnRestingOrdersAsync();

        n.Should().Be(2);
        handler.CancelBodies.Should().HaveCount(2);
        handler.CancelBodies.Should().Contain(b => b.Contains("bot1"));
        handler.CancelBodies.Should().Contain(b => b.Contains("bot2"));
        handler.CancelBodies.Should().NotContain(b => b.Contains("manual"));
    }
}
