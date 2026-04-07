// Feature: live-trading-safety, Property 12: No-throw on missing Telegram config
// Validates: Requirements 7.3, 7.4

using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;
using ThatsMyQuant.Infrastructure.Services;
using Xunit;

namespace ThatsMyQuant.Tests.Infrastructure;

public class TelegramAlertServiceTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public int CallCount { get; private set; }
        private readonly HttpStatusCode _statusCode;

        public CapturingHandler(HttpStatusCode statusCode = HttpStatusCode.OK)
            => _statusCode = statusCode;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }

    private static IOptions<TelegramOptions> Options(string botToken = "", string chatId = "")
        => Microsoft.Extensions.Options.Options.Create(new TelegramOptions
        {
            BotToken = botToken,
            ChatId   = chatId
        });

    private static (TradeDecision decision, IndicatorSnapshot snapshot, AlertAuditContext audit) MakeArgs()
    {
        var decision = new TradeDecision { Side = TradeSide.Buy, Confidence = 80, StopLoss = 49_000m, TakeProfit = 52_000m };
        var snapshot = new IndicatorSnapshot { CurrentPrice = 50_000m };
        var audit    = new AlertAuditContext { ExecutionMode = "LIVE", PromotionStage = "A" };
        return (decision, snapshot, audit);
    }

    // ── Property 12: No-throw on missing config ───────────────────────────────
    // When BotToken is empty, SendTradeAlertAsync must complete without throwing
    // and must NOT make any HTTP call.
    // Validates: Requirements 7.4
    [Fact]
    public async Task SendTradeAlertAsync_WhenBotTokenEmpty_LogsWithoutHttpCall()
    {
        var handler = new CapturingHandler();
        var svc = new TelegramAlertService(
            NullLogger<TelegramAlertService>.Instance,
            new HttpClient(handler),
            Options(botToken: ""));

        var (decision, snapshot, audit) = MakeArgs();

        var act = async () => await svc.SendTradeAlertAsync("BTCUSDT", decision, snapshot, audit);
        await act.Should().NotThrowAsync("missing BotToken must fall back to mock-logging, not throw");

        handler.CallCount.Should().Be(0, "no HTTP call should be made when BotToken is empty");
    }

    // Req 7.3 — when BotToken is set, an HTTP POST is sent to the Telegram API
    [Fact]
    public async Task SendTradeAlertAsync_WhenBotTokenSet_SendsHttpRequest()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var svc = new TelegramAlertService(
            NullLogger<TelegramAlertService>.Instance,
            new HttpClient(handler),
            Options(botToken: "test-bot-token", chatId: "12345"));

        var (decision, snapshot, audit) = MakeArgs();

        await svc.SendTradeAlertAsync("BTCUSDT", decision, snapshot, audit);

        handler.CallCount.Should().Be(1, "exactly one HTTP POST should be sent to Telegram");
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Contain("test-bot-token", "the URL must include the configured BotToken");
    }
}
