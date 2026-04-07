using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ThatsMyQuant.Application.Security;
using ThatsMyQuant.Infrastructure.Services;
using Xunit;

namespace ThatsMyQuant.Tests.Execution;

public class OkxExecutionSyncTests
{
    [Fact]
    public void ParseExecutionReports_WhenOrdersPayloadProvided_MapsVenueFields()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "arg": { "channel": "orders", "instType": "SPOT" },
              "data": [
                {
                  "instId": "BTC-USDT",
                  "clOrdId": "tmq-signal-42",
                  "ordId": "9918271",
                  "state": "partially_filled",
                  "sz": "1.0000",
                  "accFillSz": "0.2500",
                  "fillSz": "0.1000",
                  "px": "65000.0",
                  "avgPx": "64980.5",
                  "fillPx": "64990.0",
                  "uTime": "1710000000000"
                }
              ]
            }
            """);

        var parsed = OkxExecutionSync.TryParseExecutionReports(document.RootElement, out var reports);

        parsed.Should().BeTrue();
        reports.Should().ContainSingle();
        var report = reports.Single();
        report.Symbol.Should().Be("BTCUSDT");
        report.ClientOrderId.Should().Be("tmq-signal-42");
        report.ExchangeOrderId.Should().Be("9918271");
        report.Status.Should().Be("PARTIALLY_FILLED");
        report.FilledQuantity.Should().Be(0.25m);
        report.RemainingQuantity.Should().Be(0.75m);
        report.LastFillQuantity.Should().Be(0.10m);
        report.LastFillPrice.Should().Be(64990.0m);
        report.AveragePrice.Should().Be(64980.5m);
    }

    [Fact]
    public void CreateLoginMessage_UsesVaultBackedSignatureAndCarriesCredentials()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OKX:ApiKey"] = "api-key",
                ["OKX:Passphrase"] = "pass",
                ["OKX:SecretKey"] = "plain-secret"
            })
            .Build();

        using var vault = new ApiVault();
        vault.SecureInboundKey("super-secret");

        var sync = new OkxExecutionSync(
            NullLogger<OkxExecutionSync>.Instance,
            configuration,
            vault);

        var payload = sync.CreateLoginMessage();
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        root.GetProperty("op").GetString().Should().Be("login");
        var credentials = root.GetProperty("args")[0];
        credentials.GetProperty("apiKey").GetString().Should().Be("api-key");
        credentials.GetProperty("passphrase").GetString().Should().Be("pass");
        credentials.GetProperty("sign").GetString().Should().NotBeNullOrWhiteSpace();
        credentials.GetProperty("sign").GetString().Should().NotBe(sync.ComputeLoginSignature("0"));
    }
}
